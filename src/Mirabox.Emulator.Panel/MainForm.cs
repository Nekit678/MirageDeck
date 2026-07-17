using Mirabox.Emulator.Core;

namespace Mirabox.Emulator.Panel;

internal sealed class MainForm : Form
{
    private readonly DeviceSurface _surface = new() { Dock = DockStyle.Fill };
    private readonly StatusBanner _status = new() { Dock = DockStyle.Bottom, Height = 64 };
    private readonly StreamDeckPlusProtocolDecoder _decoder = new();
    private readonly CancellationTokenSource _shutdown = new();
    private HidDevice? _device;

    public MainForm()
    {
        Text = "MirageDeck — виртуальный Elgato Stream Deck +";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(740, 530);
        ClientSize = new Size(900, 600);
        BackColor = Palette.Window;
        AutoScaleMode = AutoScaleMode.Dpi;
        Controls.Add(_surface);
        Controls.Add(_status);
        _status.SetStatus("Инициализация виртуального HID-устройства…", StatusKind.Connecting);
        _surface.InputGenerated += Inject;
        _surface.StatusChanged += message => SetStatus(message, StatusKind.Activity);
        Shown += (_, _) => Connect();
        FormClosed += (_, _) => _shutdown.Cancel();
    }

    private void Connect()
    {
        try
        {
            _device = HidDevice.OpenStreamDeckPlus();
            SetStatus("HID 0FD9:0084 (Stream Deck +) готов к работе", StatusKind.Success);
            _ = Task.Run(() => PollAsync(_shutdown.Token));
        }
        catch (Exception error)
        {
            SetStatus(error.Message, StatusKind.Error);
            MessageBox.Show(this, error.Message, "MirageDeck", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void Inject(byte[] report, string description)
    {
        try
        {
            _device?.InjectInput(report);
            SetStatus(description, StatusKind.Activity);
        }
        catch (Exception error) { SetStatus(error.Message, StatusKind.Error); }
    }

    private async Task PollAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var received = false;
                for (var i = 0; i < 64 && _device!.TryReadCapture(out var capture); i++)
                {
                    received = true;
                    var updates = capture.Kind == PanelCaptureKind.Output
                        ? _decoder.PushOutput(capture.Data)
                        : _decoder.PushFeature(capture.Data);
                    foreach (var update in updates)
                        BeginInvoke(() => Apply(update));
                }
                if (!received) await Task.Delay(8, cancellationToken);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (!IsDisposed) BeginInvoke(() => SetStatus($"Ошибка HID: {error.Message}", StatusKind.Error));
        }
    }

    private void Apply(DeviceUpdate update)
    {
        switch (update)
        {
        case ButtonImageUpdate image:
            _surface.SetKeyImage(image.Button, DecodeImage(image.EncodedImage));
            SetStatus($"Изображение клавиши {image.Button + 1}: {image.EncodedImage.Length:N0} байт", StatusKind.Activity);
            break;
        case FullScreenImageUpdate fullScreen:
            _surface.SetFullScreenImage(DecodeImage(fullScreen.EncodedImage));
            SetStatus($"Полный LCD-кадр: {fullScreen.EncodedImage.Length:N0} байт", StatusKind.Activity);
            break;
        case WindowImageUpdate window:
            _surface.SetWindowImage(DecodeImage(window.EncodedImage));
            SetStatus($"Touch strip обновлён: {window.EncodedImage.Length:N0} байт", StatusKind.Activity);
            break;
        case PartialWindowImageUpdate partial:
            _surface.SetPartialWindowImage(
                new Rectangle(partial.X, partial.Y, partial.Width, partial.Height),
                DecodeImage(partial.EncodedImage));
            SetStatus($"Touch strip: область {partial.X},{partial.Y} {partial.Width}×{partial.Height}", StatusKind.Activity);
            break;
        case BrightnessUpdate brightness:
            _surface.Brightness = brightness.Value;
            _surface.Invalidate();
            SetStatus($"Яркость экрана: {brightness.Value}%", StatusKind.Activity);
            break;
        case ShowLogoUpdate:
            _surface.ShowLogo();
            break;
        case FillLcdColorUpdate fillLcd:
            _surface.FillLcd(Color.FromArgb(fillLcd.Red, fillLcd.Green, fillLcd.Blue));
            SetStatus($"LCD заполнен цветом #{fillLcd.Red:X2}{fillLcd.Green:X2}{fillLcd.Blue:X2}", StatusKind.Activity);
            break;
        case FillButtonColorUpdate fillButton:
            _surface.FillKey(fillButton.Button, Color.FromArgb(fillButton.Red, fillButton.Green, fillButton.Blue));
            SetStatus($"Клавиша {fillButton.Button + 1} заполнена цветом", StatusKind.Activity);
            break;
        case SleepDurationUpdate sleep:
            SetStatus(sleep.Seconds == 0
                ? "Автоматический сон отключён"
                : $"Таймер сна: {sleep.Seconds} с", StatusKind.Activity);
            break;
        case UnknownCommandUpdate unknown:
            SetStatus($"Неизвестный HID report: {Convert.ToHexString(unknown.Report.AsSpan(0, Math.Min(8, unknown.Report.Length)))}",
                StatusKind.Activity);
            break;
        }
    }

    private void SetStatus(string message, StatusKind kind) => _status.SetStatus(message, kind);

    private static Image DecodeImage(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        using var source = Image.FromStream(stream, useEmbeddedColorManagement: false, validateImageData: true);
        return new Bitmap(source);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _shutdown.Cancel();
            _device?.Dispose();
            _shutdown.Dispose();
        }
        base.Dispose(disposing);
    }
}

internal enum StatusKind
{
    Connecting,
    Success,
    Activity,
    Error,
}

internal sealed class StatusBanner : Control
{
    private readonly ToolTip _toolTip = new() { InitialDelay = 350, ReshowDelay = 100 };
    private string _message = string.Empty;
    private StatusKind _kind;

    public StatusBanner()
    {
        DoubleBuffered = true;
        BackColor = Palette.Window;
        ForeColor = Palette.PrimaryText;
        SetStyle(ControlStyles.ResizeRedraw, true);
        AccessibleRole = AccessibleRole.StatusBar;
    }

    public void SetStatus(string message, StatusKind kind)
    {
        _message = string.IsNullOrWhiteSpace(message) ? "Нет дополнительных сведений" : message.Trim();
        _kind = kind;
        AccessibleName = $"{Title(kind)}: {_message}";
        _toolTip.SetToolTip(this, _message);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

        using (var separator = new Pen(Color.FromArgb(44, 46, 53)))
            e.Graphics.DrawLine(separator, 0, 0, ClientSize.Width, 0);

        var banner = new RectangleF(14, 9, Math.Max(0, ClientSize.Width - 28), Math.Max(0, ClientSize.Height - 18));
        var colors = Colors(_kind);
        using (var fill = new SolidBrush(colors.Background))
            e.Graphics.FillRoundedRectangle(fill, banner, 10);
        using (var border = new Pen(colors.Border))
            e.Graphics.DrawRoundedRectangle(border, banner, 10);

        var centerY = banner.Top + banner.Height / 2;
        using (var glow = new SolidBrush(Color.FromArgb(35, colors.Highlight)))
            e.Graphics.FillEllipse(glow, banner.Left + 13, centerY - 9, 18, 18);
        using (var dot = new SolidBrush(colors.Highlight))
            e.Graphics.FillEllipse(dot, banner.Left + 19, centerY - 3, 6, 6);

        using var titleFont = new Font(Font.FontFamily, 8.5f, FontStyle.Bold);
        using var messageFont = new Font(Font.FontFamily, 9.5f, FontStyle.Regular);
        var titleRect = new Rectangle(
            (int)banner.Left + 43,
            (int)banner.Top + 5,
            Math.Min(115, Math.Max(0, (int)banner.Width - 55)),
            16);
        var messageRect = new Rectangle(
            titleRect.Left,
            titleRect.Bottom - 1,
            Math.Max(0, (int)banner.Right - titleRect.Left - 12),
            20);
        TextRenderer.DrawText(e.Graphics, Title(_kind).ToUpperInvariant(), titleFont, titleRect, colors.Highlight,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
        TextRenderer.DrawText(e.Graphics, _message, messageFont, messageRect, Palette.PrimaryText,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis |
            TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
    }

    private static string Title(StatusKind kind) => kind switch
    {
        StatusKind.Connecting => "Подключение",
        StatusKind.Success => "Готово",
        StatusKind.Activity => "Событие",
        StatusKind.Error => "Ошибка",
        _ => "Статус",
    };

    private static (Color Background, Color Border, Color Highlight) Colors(StatusKind kind) => kind switch
    {
        StatusKind.Connecting =>
            (Color.FromArgb(28, 36, 49), Color.FromArgb(51, 76, 108), Color.FromArgb(103, 165, 238)),
        StatusKind.Success =>
            (Color.FromArgb(25, 42, 34), Color.FromArgb(48, 91, 68), Color.FromArgb(80, 200, 120)),
        StatusKind.Error =>
            (Color.FromArgb(55, 29, 34), Color.FromArgb(128, 54, 64), Color.FromArgb(255, 105, 118)),
        _ =>
            (Color.FromArgb(31, 33, 39), Color.FromArgb(58, 61, 69), Palette.Accent),
    };

    protected override void Dispose(bool disposing)
    {
        if (disposing) _toolTip.Dispose();
        base.Dispose(disposing);
    }
}
