using Mirabox.Emulator.Core;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Mirabox.Emulator.Panel;

internal sealed class MainForm : Form
{
    private readonly DeviceSurface _surface = new() { Dock = DockStyle.Fill };
    private readonly StatusBanner _status = new() { Dock = DockStyle.Bottom, Height = 64 };
    private readonly MiraboxProtocolDecoder _decoder = new();
    private readonly CancellationTokenSource _shutdown = new();
    private HidDevice? _device;
    private bool _hasExplicitTouchMode;

    public MainForm()
    {
        Text = "Mirabox N4 Pro — виртуальная панель";
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
        _surface.TouchModeSelected += () => _hasExplicitTouchMode = true;
        Shown += (_, _) => Connect();
        FormClosed += (_, _) => _shutdown.Cancel();
    }

    private void Connect()
    {
        try
        {
            _device = HidDevice.OpenN4Pro();
            SetStatus("HID 5548:1021 (Global) готов к работе", StatusKind.Success);
            _ = Task.Run(() => PollAsync(_shutdown.Token));
        }
        catch (Exception error)
        {
            SetStatus(error.Message, StatusKind.Error);
            MessageBox.Show(this, error.Message, "Mirabox HID Emulator", MessageBoxButtons.OK, MessageBoxIcon.Warning);
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
                for (var i = 0; i < 64 && _device!.TryReadOutput(out var packet); i++)
                {
                    received = true;
                    foreach (var update in _decoder.Push(packet))
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
        case ImageUpdate image:
            var index = N4ProProfile.UiKeyForImageSlot(image.Slot);
            if (index >= 0)
            {
                _surface.SetKeyImage(index, DecodeImage(image.EncodedImage));
                SetStatus($"Изображение кнопки {index + 1}: {image.EncodedImage.Length:N0} байт", StatusKind.Activity);
            }
            else
            {
                var secondary = N4ProProfile.SecondaryKeyForImageSlot(image.Slot);
                if (secondary >= 0)
                {
                    if (!_hasExplicitTouchMode) _surface.SetTouchMode(TouchDisplayMode.Button);
                    _surface.SetSecondaryImage(secondary, DecodeImage(image.EncodedImage));
                    SetStatus($"Изображение touch-кнопки {secondary + 1}: {image.EncodedImage.Length:N0} байт", StatusKind.Activity);
                }
            }
            break;
        case BackgroundUpdate background:
            if (!_hasExplicitTouchMode) _surface.SetTouchMode(TouchDisplayMode.TouchBar);
            _surface.SetBackground(DecodeImage(background.EncodedImage));
            SetStatus($"Фон обновлён: {background.EncodedImage.Length:N0} байт", StatusKind.Activity);
            break;
        case BrightnessUpdate brightness:
            _surface.Brightness = brightness.Value;
            _surface.Invalidate();
            SetStatus($"Яркость экрана: {brightness.Value}%", StatusKind.Activity);
            break;
        case ClearKeyUpdate clear:
            var main = N4ProProfile.UiKeyForImageSlot(clear.Slot);
            if (main >= 0) _surface.ClearKey(main);
            else _surface.ClearSecondaryImage(N4ProProfile.SecondaryKeyForImageSlot(clear.Slot));
            break;
        case ClearAllUpdate:
            _surface.ClearAll();
            break;
        case WakeUpdate:
            SetStatus("Экран включён", StatusKind.Success);
            break;
        case TouchModeUpdate mode:
            _hasExplicitTouchMode = true;
            _surface.SetTouchMode(mode.TouchBar ? TouchDisplayMode.TouchBar : TouchDisplayMode.Button);
            break;
        }
    }

    private void SetStatus(string message, StatusKind kind) => _status.SetStatus(message, kind);

    private static Image DecodeImage(byte[] bytes)
    {
        Bitmap image;
        if (TryGetRawDimensions(bytes.Length, out var width, out var height))
        {
            image = DecodeBgr24(bytes, width, height);
        }
        else
        {
            using var stream = new MemoryStream(bytes, writable: false);
            using var source = Image.FromStream(stream, useEmbeddedColorManagement: false, validateImageData: true);
            image = new Bitmap(source);
        }
        image.RotateFlip(RotateFlipType.Rotate180FlipNone);
        return image;
    }

    private static bool TryGetRawDimensions(int length, out int width, out int height)
    {
        (width, height) = length switch
        {
            800 * 480 * 3 => (800, 480),
            176 * 112 * 3 => (176, 112),
            112 * 112 * 3 => (112, 112),
            _ => (0, 0),
        };
        return width != 0;
    }

    private static Bitmap DecodeBgr24(byte[] bytes, int width, int height)
    {
        var bitmap = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        var data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
        try
        {
            var sourceStride = width * 3;
            for (var y = 0; y < height; y++)
                Marshal.Copy(bytes, y * sourceStride, IntPtr.Add(data.Scan0, y * data.Stride), sourceStride);
        }
        finally { bitmap.UnlockBits(data); }
        return bitmap;
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
