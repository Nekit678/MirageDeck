using Mirabox.Emulator.Core;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Mirabox.Emulator.Panel;

internal sealed class MainForm : Form
{
    private readonly DeviceSurface _surface = new() { Dock = DockStyle.Fill };
    private readonly ToolStripStatusLabel _status = new("Подключение…");
    private readonly MiraboxProtocolDecoder _decoder = new();
    private readonly CancellationTokenSource _shutdown = new();
    private HidDevice? _device;

    public MainForm()
    {
        Text = "Mirabox N4 Pro — виртуальная панель";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(760, 620);
        ClientSize = new Size(930, 680);
        BackColor = Color.FromArgb(22, 23, 27);
        var statusStrip = new StatusStrip { SizingGrip = false };
        statusStrip.Items.Add(_status);
        Controls.Add(_surface);
        Controls.Add(statusStrip);
        _surface.InputGenerated += Inject;
        Shown += (_, _) => Connect();
        FormClosed += (_, _) => _shutdown.Cancel();
    }

    private void Connect()
    {
        try
        {
            _device = HidDevice.OpenN4Pro();
            _status.Text = "Подключено: HID 5548:1021 (Global)";
            _ = Task.Run(() => PollAsync(_shutdown.Token));
        }
        catch (Exception error)
        {
            _status.Text = error.Message;
            MessageBox.Show(this, error.Message, "Mirabox HID Emulator", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void Inject(byte[] report, string description)
    {
        try
        {
            _device?.InjectInput(report);
            _status.Text = description;
        }
        catch (Exception error) { _status.Text = error.Message; }
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
            if (!IsDisposed) BeginInvoke(() => _status.Text = $"Ошибка HID: {error.Message}");
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
                _status.Text = $"Изображение кнопки {index + 1}: {image.EncodedImage.Length:N0} байт";
            }
            else
            {
                var secondary = N4ProProfile.SecondaryKeyForImageSlot(image.Slot);
                if (secondary >= 0)
                {
                    _surface.SetSecondaryImage(secondary, DecodeImage(image.EncodedImage));
                    _status.Text = $"Изображение touch-кнопки {secondary + 1}: {image.EncodedImage.Length:N0} байт";
                }
            }
            break;
        case BackgroundUpdate background:
            _surface.SetBackground(DecodeImage(background.EncodedImage));
            _status.Text = $"Фон: {background.EncodedImage.Length:N0} байт";
            break;
        case BrightnessUpdate brightness:
            _surface.Brightness = brightness.Value;
            _surface.Invalidate();
            _status.Text = $"Яркость: {brightness.Value}";
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
            _status.Text = "Экран включён";
            break;
        }
    }

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
