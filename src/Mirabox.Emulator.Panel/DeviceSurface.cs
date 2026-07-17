using Mirabox.Emulator.Core;
using System.Drawing.Drawing2D;

namespace Mirabox.Emulator.Panel;

internal sealed class DeviceSurface : Control
{
    private const int TouchGestureThreshold = 10;
    private const int TouchHoldThresholdMs = 500;
    private const float CanvasWidth = 800f;
    private const float CanvasHeight = 440f;

    private readonly Image?[] _keys = new Image?[StreamDeckPlusProfile.KeyCount];
    private readonly RectangleF[] _keyRects = new RectangleF[StreamDeckPlusProfile.KeyCount];
    private readonly RectangleF[] _knobRects = new RectangleF[StreamDeckPlusProfile.EncoderCount];
    private readonly bool[] _keyStates = new bool[StreamDeckPlusProfile.KeyCount];
    private readonly bool[] _encoderStates = new bool[StreamDeckPlusProfile.EncoderCount];
    private Image? _windowImage;
    private Image? _fullScreenImage;
    private RectangleF _touchRect;
    private int _activeKey = -1;
    private int _activeKnob = -1;
    private int _hoverKey = -1;
    private int _hoverKnob = -1;
    private bool _hoverTouch;
    private bool _touching;
    private Point _touchStart;
    private long _touchStartedAt;

    public event Action<byte[], string>? InputGenerated;
    public event Action<string>? StatusChanged;
    public byte Brightness { get; set; } = 100;

    public DeviceSurface()
    {
        DoubleBuffered = true;
        BackColor = Palette.Window;
        ForeColor = Palette.PrimaryText;
        MinimumSize = new Size(700, 430);
        SetStyle(ControlStyles.Selectable | ControlStyles.ResizeRedraw, true);
    }

    public void SetKeyImage(int index, Image image)
    {
        if ((uint)index >= _keys.Length) { image.Dispose(); return; }
        _keys[index]?.Dispose();
        _keys[index] = image;
        Invalidate();
    }

    public void ClearKey(int index)
    {
        if ((uint)index >= _keys.Length) return;
        _keys[index]?.Dispose();
        _keys[index] = null;
        Invalidate();
    }

    public void SetWindowImage(Image image)
    {
        _windowImage?.Dispose();
        _windowImage = image;
        Invalidate();
    }

    public void SetPartialWindowImage(Rectangle target, Image image)
    {
        var canvas = new Bitmap(StreamDeckPlusProfile.TouchWidth, StreamDeckPlusProfile.TouchHeight);
        using (var graphics = Graphics.FromImage(canvas))
        {
            graphics.Clear(Color.Black);
            if (_windowImage is not null)
                graphics.DrawImage(_windowImage, new Rectangle(0, 0, canvas.Width, canvas.Height));
            graphics.DrawImage(image, target);
        }
        image.Dispose();
        _windowImage?.Dispose();
        _windowImage = canvas;
        Invalidate();
    }

    public void SetFullScreenImage(Image image)
    {
        _fullScreenImage?.Dispose();
        _fullScreenImage = image;
        Invalidate();
    }

    public void FillLcd(Color color)
    {
        var image = new Bitmap(StreamDeckPlusProfile.LcdWidth, StreamDeckPlusProfile.LcdHeight);
        using (var graphics = Graphics.FromImage(image)) graphics.Clear(color);
        SetFullScreenImage(image);
        for (var i = 0; i < _keys.Length; i++) ClearKey(i);
        _windowImage?.Dispose();
        _windowImage = null;
        Invalidate();
    }

    public void FillKey(int index, Color color)
    {
        if ((uint)index >= _keys.Length) return;
        var image = new Bitmap(StreamDeckPlusProfile.KeyImageWidth, StreamDeckPlusProfile.KeyImageHeight);
        using (var graphics = Graphics.FromImage(image)) graphics.Clear(color);
        SetKeyImage(index, image);
    }

    public void ShowLogo()
    {
        ClearAll();
        StatusChanged?.Invoke("Показан экран ожидания Stream Deck +");
    }

    public void ClearAll()
    {
        for (var i = 0; i < _keys.Length; i++)
        {
            _keys[i]?.Dispose();
            _keys[i] = null;
        }
        _windowImage?.Dispose();
        _windowImage = null;
        _fullScreenImage?.Dispose();
        _fullScreenImage = null;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        e.Graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;

        var scale = Math.Min((ClientSize.Width - 32f) / CanvasWidth, (ClientSize.Height - 24f) / CanvasHeight);
        var origin = new PointF(
            (ClientSize.Width - CanvasWidth * scale) / 2f,
            (ClientSize.Height - CanvasHeight * scale) / 2f);
        RectangleF At(float x, float y, float width, float height) =>
            new(origin.X + x * scale, origin.Y + y * scale, width * scale, height * scale);

        var faceRect = At(10, 6, 780, 426);
        using (var shadow = new SolidBrush(Color.FromArgb(80, 0, 0, 0)))
            e.Graphics.FillRoundedRectangle(shadow, Offset(faceRect, 0, 5 * scale), 24 * scale);
        using (var face = new SolidBrush(Palette.DeviceFace))
            e.Graphics.FillRoundedRectangle(face, faceRect, 24 * scale);
        using (var edge = new Pen(Palette.DeviceEdge, Math.Max(1f, 1.5f * scale)))
            e.Graphics.DrawRoundedRectangle(edge, faceRect, 24 * scale);

        const float keySize = 80f;
        const float columnGap = 26f;
        const float rowGap = 18f;
        const float gridLeft = 201f;
        const float gridTop = 22f;
        for (var row = 0; row < StreamDeckPlusProfile.KeyRows; row++)
        for (var column = 0; column < StreamDeckPlusProfile.KeyColumns; column++)
        {
            var index = row * StreamDeckPlusProfile.KeyColumns + column;
            _keyRects[index] = At(
                gridLeft + column * (keySize + columnGap),
                gridTop + row * (keySize + rowGap),
                keySize,
                keySize);
            DrawKey(e.Graphics, index, _keyRects[index], scale);
        }

        _touchRect = At(125, 226, 550, 69);
        DrawTouchStrip(e.Graphics, scale);

        var knobTop = 319f;
        var knobCenters = new[] { 241f, 347f, 453f, 559f };
        for (var i = 0; i < knobCenters.Length; i++)
        {
            _knobRects[i] = At(knobCenters[i] - 31, knobTop, 62, 62);
            DrawKnob(e.Graphics, i, _knobRects[i], scale);
        }

        using var hintFont = new Font(Font.FontFamily, Math.Max(7f, 8.5f * scale), FontStyle.Regular, GraphicsUnit.Pixel);
        TextRenderer.DrawText(e.Graphics,
            "Touch strip: тап / удержание / flick  •  энкодеры: щелчок и колесо мыши",
            hintFont, Rectangle.Round(At(100, 402, 600, 20)), Palette.MutedText,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }

    private void DrawKey(Graphics graphics, int index, RectangleF rect, float scale)
    {
        var active = index == _activeKey;
        var hovered = index == _hoverKey;
        var radius = 10 * scale;
        using (var shadow = new SolidBrush(Color.FromArgb(105, 0, 0, 0)))
            graphics.FillRoundedRectangle(shadow, Offset(rect, 0, 3 * scale), radius);
        using (var fill = new SolidBrush(active ? Palette.KeyPressed : hovered ? Palette.KeyHover : Palette.Key))
            graphics.FillRoundedRectangle(fill, rect, radius);

        if (_keys[index] is not null)
            DrawClippedImage(graphics, _keys[index]!, rect, radius);
        else if (_fullScreenImage is not null)
        {
            var row = index / StreamDeckPlusProfile.KeyColumns;
            var column = index % StreamDeckPlusProfile.KeyColumns;
            var source = new RectangleF(column * 200, row * 190, 200, 190);
            DrawClippedImage(graphics, _fullScreenImage, rect, radius, source);
        }
        else
            DrawPlaceholder(graphics, (index + 1).ToString(), rect, scale);

        ApplyBrightness(graphics, rect, radius);
        var borderColor = active ? Palette.Accent : hovered ? Palette.KeyHoverEdge : Palette.KeyEdge;
        using var border = new Pen(borderColor, Math.Max(1f, (active ? 2f : 1f) * scale));
        graphics.DrawRoundedRectangle(border, rect, radius);
        if (active)
        {
            using var overlay = new SolidBrush(Color.FromArgb(45, Palette.Accent));
            graphics.FillRoundedRectangle(overlay, rect, radius);
        }
    }

    private void DrawTouchStrip(Graphics graphics, float scale)
    {
        var radius = 10 * scale;
        using (var shadow = new SolidBrush(Color.FromArgb(115, 0, 0, 0)))
            graphics.FillRoundedRectangle(shadow, Offset(_touchRect, 0, 3 * scale), radius);
        using (var touch = new SolidBrush(Palette.Touch))
            graphics.FillRoundedRectangle(touch, _touchRect, radius);

        if (_windowImage is not null)
            DrawClippedImage(graphics, _windowImage, _touchRect, radius);
        else if (_fullScreenImage is not null)
            DrawClippedImage(graphics, _fullScreenImage, _touchRect, radius,
                new RectangleF(0, StreamDeckPlusProfile.LcdHeight - StreamDeckPlusProfile.TouchHeight,
                    StreamDeckPlusProfile.TouchWidth, StreamDeckPlusProfile.TouchHeight));
        else
        {
            using var titleFont = new Font(Font.FontFamily, Math.Max(8f, 10f * scale), FontStyle.Bold, GraphicsUnit.Pixel);
            TextRenderer.DrawText(graphics, "TOUCH STRIP  800 × 100", titleFont, Rectangle.Round(_touchRect), Palette.SecondaryText,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }

        if (_hoverTouch || _touching)
        {
            using var hover = new SolidBrush(Color.FromArgb(_touching ? 38 : 18, Palette.Accent));
            graphics.FillRoundedRectangle(hover, _touchRect, radius);
        }
        ApplyBrightness(graphics, _touchRect, radius);
        using var border = new Pen(_touching ? Palette.Accent : Palette.TouchEdge,
            Math.Max(1f, (_touching ? 2f : 1.5f) * scale));
        graphics.DrawRoundedRectangle(border, _touchRect, radius);
    }

    private void DrawKnob(Graphics graphics, int index, RectangleF rect, float scale)
    {
        var active = index == _activeKnob;
        var hovered = index == _hoverKnob;
        using (var shadow = new SolidBrush(Color.FromArgb(120, 0, 0, 0)))
            graphics.FillEllipse(shadow, Offset(rect, 0, 4 * scale));
        using (var outer = new SolidBrush(active ? Palette.KnobPressed : hovered ? Palette.KnobHover : Palette.KnobEdge))
            graphics.FillEllipse(outer, rect);
        var inner = RectangleF.Inflate(rect, -4 * scale, -4 * scale);
        using (var fill = new LinearGradientBrush(inner, Palette.KnobTop, Palette.KnobBottom, 90f))
            graphics.FillEllipse(fill, inner);
        using (var ring = new Pen(Color.FromArgb(90, 255, 255, 255), Math.Max(1f, scale)))
            graphics.DrawEllipse(ring, inner);
        var centerX = rect.Left + rect.Width / 2;
        using var marker = new Pen(active || hovered ? Palette.Accent : Palette.KnobMarker,
            Math.Max(1.5f, 2f * scale)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        graphics.DrawLine(marker, centerX, rect.Top + 10 * scale, centerX, rect.Top + 22 * scale);
    }

    private void DrawPlaceholder(Graphics graphics, string text, RectangleF rect, float scale)
    {
        var badge = new RectangleF(rect.Left + rect.Width / 2 - 11 * scale, rect.Top + rect.Height / 2 - 11 * scale,
            22 * scale, 22 * scale);
        using var badgeFill = new SolidBrush(Palette.PlaceholderBadge);
        graphics.FillEllipse(badgeFill, badge);
        using var font = new Font(Font.FontFamily, Math.Max(7f, 8.5f * scale), FontStyle.Bold, GraphicsUnit.Pixel);
        TextRenderer.DrawText(graphics, text, font, Rectangle.Round(badge), Palette.SecondaryText,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }

    private void ApplyBrightness(Graphics graphics, RectangleF rect, float radius)
    {
        var opacity = (int)((100 - Math.Min(Brightness, (byte)100)) * 2.25);
        if (opacity <= 0) return;
        using var shade = new SolidBrush(Color.FromArgb(opacity, Color.Black));
        graphics.FillRoundedRectangle(shade, rect, radius);
    }

    private static void DrawClippedImage(Graphics graphics, Image image, RectangleF target, float radius,
        RectangleF? source = null)
    {
        var state = graphics.Save();
        using var path = RoundedRectangleExtensions.Path(target, radius);
        graphics.SetClip(path);
        if (source is { } sourceRect)
            graphics.DrawImage(image, target, sourceRect, GraphicsUnit.Pixel);
        else
            graphics.DrawImage(image, target);
        graphics.Restore(state);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        Focus();
        for (var i = 0; i < _keyRects.Length; i++)
            if (_keyRects[i].Contains(e.Location))
            {
                _activeKey = i;
                _keyStates[i] = true;
                Emit(InputReportFactory.Buttons(_keyStates), $"Клавиша {i + 1}: нажата");
                Invalidate();
                return;
            }
        for (var i = 0; i < _knobRects.Length; i++)
            if (_knobRects[i].Contains(e.Location))
            {
                _activeKnob = i;
                _encoderStates[i] = true;
                Emit(InputReportFactory.EncoderButtons(_encoderStates), $"Энкодер {i + 1}: нажат");
                Invalidate();
                return;
            }
        if (_touchRect.Contains(e.Location))
        {
            _touching = true;
            _touchStart = e.Location;
            _touchStartedAt = Environment.TickCount64;
            Invalidate();
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        UpdateHover(e.Location);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hoverKey = _hoverKnob = -1;
        _hoverTouch = false;
        Cursor = Cursors.Default;
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Left) return;
        if (_activeKey >= 0)
        {
            var index = _activeKey;
            _activeKey = -1;
            _keyStates[index] = false;
            Emit(InputReportFactory.Buttons(_keyStates), $"Клавиша {index + 1}: отпущена");
        }
        if (_activeKnob >= 0)
        {
            var index = _activeKnob;
            _activeKnob = -1;
            _encoderStates[index] = false;
            Emit(InputReportFactory.EncoderButtons(_encoderStates), $"Энкодер {index + 1}: отпущен");
        }
        if (_touching)
        {
            _touching = false;
            EmitTouchGesture(e.Location);
        }
        UpdateHover(e.Location);
        Invalidate();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        var location = PointToClient(MousePosition);
        for (var i = 0; i < _knobRects.Length; i++)
            if (_knobRects[i].Contains(location))
            {
                var ticks = e.Delta > 0 ? 1 : -1;
                Emit(InputReportFactory.EncoderRotate(i, ticks),
                    $"Энкодер {i + 1}: {(ticks > 0 ? "по часовой стрелке" : "против часовой стрелки")}");
                break;
            }
    }

    private void EmitTouchGesture(Point endLocation)
    {
        var start = ToLogicalTouchPoint(_touchStart);
        var end = ToLogicalTouchPoint(endLocation);
        var deltaX = endLocation.X - _touchStart.X;
        var deltaY = endLocation.Y - _touchStart.Y;
        var moved = deltaX * deltaX + deltaY * deltaY >= TouchGestureThreshold * TouchGestureThreshold;
        if (moved)
            Emit(InputReportFactory.TouchFlick((ushort)start.X, (ushort)start.Y, (ushort)end.X, (ushort)end.Y),
                $"Touch strip: flick {start.X},{start.Y} → {end.X},{end.Y}");
        else if (Environment.TickCount64 - _touchStartedAt >= TouchHoldThresholdMs)
            Emit(InputReportFactory.TouchPress((ushort)start.X, (ushort)start.Y),
                $"Touch strip: удержание {start.X},{start.Y}");
        else
            Emit(InputReportFactory.TouchTap((ushort)start.X, (ushort)start.Y),
                $"Touch strip: тап {start.X},{start.Y}");
    }

    private Point ToLogicalTouchPoint(Point location) => new(
        Math.Clamp((int)((location.X - _touchRect.Left) / _touchRect.Width * StreamDeckPlusProfile.TouchWidth),
            0, StreamDeckPlusProfile.TouchWidth - 1),
        Math.Clamp((int)((location.Y - _touchRect.Top) / _touchRect.Height * StreamDeckPlusProfile.TouchHeight),
            0, StreamDeckPlusProfile.TouchHeight - 1));

    private void UpdateHover(Point location)
    {
        var oldKey = _hoverKey;
        var oldKnob = _hoverKnob;
        var oldTouch = _hoverTouch;
        _hoverKey = Array.FindIndex(_keyRects, rect => rect.Contains(location));
        _hoverKnob = Array.FindIndex(_knobRects, rect => rect.Contains(location));
        _hoverTouch = _touchRect.Contains(location);
        Cursor = _hoverKey >= 0 || _hoverKnob >= 0 || _hoverTouch ? Cursors.Hand : Cursors.Default;
        if (oldKey != _hoverKey || oldKnob != _hoverKnob || oldTouch != _hoverTouch) Invalidate();
    }

    private void Emit(byte[] report, string description) => InputGenerated?.Invoke(report, description);

    private static RectangleF Offset(RectangleF rect, float x, float y) =>
        new(rect.X + x, rect.Y + y, rect.Width, rect.Height);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            foreach (var image in _keys) image?.Dispose();
            _windowImage?.Dispose();
            _fullScreenImage?.Dispose();
        }
        base.Dispose(disposing);
    }
}

internal static class Palette
{
    public static readonly Color Window = Color.FromArgb(20, 21, 25);
    public static readonly Color DeviceFace = Color.FromArgb(42, 44, 50);
    public static readonly Color DeviceEdge = Color.FromArgb(72, 75, 84);
    public static readonly Color Key = Color.FromArgb(12, 13, 16);
    public static readonly Color KeyHover = Color.FromArgb(25, 27, 32);
    public static readonly Color KeyPressed = Color.FromArgb(34, 36, 42);
    public static readonly Color KeyEdge = Color.FromArgb(31, 33, 39);
    public static readonly Color KeyHoverEdge = Color.FromArgb(92, 96, 108);
    public static readonly Color Touch = Color.FromArgb(10, 11, 14);
    public static readonly Color TouchEdge = Color.FromArgb(76, 79, 88);
    public static readonly Color KnobEdge = Color.FromArgb(35, 37, 43);
    public static readonly Color KnobHover = Color.FromArgb(76, 80, 91);
    public static readonly Color KnobPressed = Color.FromArgb(255, 176, 0);
    public static readonly Color KnobTop = Color.FromArgb(109, 113, 125);
    public static readonly Color KnobBottom = Color.FromArgb(79, 83, 93);
    public static readonly Color KnobMarker = Color.FromArgb(221, 223, 229);
    public static readonly Color Accent = Color.FromArgb(255, 181, 0);
    public static readonly Color PrimaryText = Color.FromArgb(239, 240, 244);
    public static readonly Color SecondaryText = Color.FromArgb(186, 190, 200);
    public static readonly Color MutedText = Color.FromArgb(126, 130, 141);
    public static readonly Color PlaceholderBadge = Color.FromArgb(34, 36, 42);
}

internal static class RoundedRectangleExtensions
{
    public static void FillRoundedRectangle(this Graphics graphics, Brush brush, RectangleF bounds, float radius) =>
        graphics.FillPath(brush, Path(bounds, radius));
    public static void DrawRoundedRectangle(this Graphics graphics, Pen pen, RectangleF bounds, float radius) =>
        graphics.DrawPath(pen, Path(bounds, radius));

    public static GraphicsPath Path(RectangleF rect, float radius)
    {
        var path = new GraphicsPath();
        var diameter = Math.Min(radius * 2, Math.Min(rect.Width, rect.Height));
        if (diameter <= 0)
        {
            path.AddRectangle(rect);
            return path;
        }
        path.AddArc(rect.X, rect.Y, diameter, diameter, 180, 90);
        path.AddArc(rect.Right - diameter, rect.Y, diameter, diameter, 270, 90);
        path.AddArc(rect.Right - diameter, rect.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(rect.X, rect.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}
