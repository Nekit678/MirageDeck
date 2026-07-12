using Mirabox.Emulator.Core;
using System.Drawing.Drawing2D;

namespace Mirabox.Emulator.Panel;

internal sealed class DeviceSurface : Control
{
    private const int LogicalWidth = 800;
    private const int LogicalHeight = 480;
    private const float CanvasWidth = 800f;
    private const float CanvasHeight = 420f;
    private readonly Image?[] _keys = new Image?[10];
    private readonly Image?[] _secondaryKeys = new Image?[4];
    private readonly RectangleF[] _keyRects = new RectangleF[10];
    private readonly RectangleF[] _knobRects = new RectangleF[4];
    private RectangleF _touchRect;
    private int _activeKey = -1;
    private int _activeKnob = -1;
    private int _activeSecondary = -1;
    private int _hoverKey = -1;
    private int _hoverKnob = -1;
    private int _hoverSecondary = -1;
    private Point _touchStart;
    private bool _touching;
    private bool _hoverTouch;
    private TouchDisplayMode _touchMode = TouchDisplayMode.Button;

    public event Action<byte[], string>? InputGenerated;
    public event Action<string>? StatusChanged;
    public byte Brightness { get; set; } = 100;
    public Image? BackgroundImageValue { get; private set; }
    public TouchDisplayMode TouchMode => _touchMode;

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

    public void ClearAll()
    {
        for (var i = 0; i < _keys.Length; i++) ClearKey(i);
        for (var i = 0; i < _secondaryKeys.Length; i++)
        {
            _secondaryKeys[i]?.Dispose();
            _secondaryKeys[i] = null;
        }
        Invalidate();
    }

    public void SetSecondaryImage(int index, Image image)
    {
        if ((uint)index >= _secondaryKeys.Length) { image.Dispose(); return; }
        _secondaryKeys[index]?.Dispose();
        _secondaryKeys[index] = image;
        Invalidate();
    }

    public void ClearSecondaryImage(int index)
    {
        if ((uint)index >= _secondaryKeys.Length) return;
        _secondaryKeys[index]?.Dispose();
        _secondaryKeys[index] = null;
        Invalidate();
    }

    public void SetBackground(Image image)
    {
        BackgroundImageValue?.Dispose();
        BackgroundImageValue = image;
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

        var faceRect = At(10, 6, 780, 408);
        using (var shadow = new SolidBrush(Color.FromArgb(80, 0, 0, 0)))
            e.Graphics.FillRoundedRectangle(shadow, Offset(faceRect, 0, 5 * scale), 24 * scale);
        using (var face = new SolidBrush(Palette.DeviceFace))
            e.Graphics.FillRoundedRectangle(face, faceRect, 24 * scale);
        using (var edge = new Pen(Palette.DeviceEdge, Math.Max(1f, 1.5f * scale)))
            e.Graphics.DrawRoundedRectangle(edge, faceRect, 24 * scale);

        const float keySize = 64f;
        const float keyGap = 20f;
        const float gridLeft = 200f;
        const float gridTop = 22f;
        const float rowGap = 18f;
        for (var row = 0; row < 2; row++)
        for (var col = 0; col < 5; col++)
        {
            var index = row * 5 + col;
            _keyRects[index] = At(
                gridLeft + col * (keySize + keyGap),
                gridTop + row * (keySize + rowGap),
                keySize,
                keySize);
            DrawKey(e.Graphics, index, _keyRects[index], scale);
        }

        // The real N4 Pro layout places the touch display between the key grid
        // and the encoders. Keeping the same order also makes gestures easier to
        // understand than the old bottom-mounted bar.
        _touchRect = At(198, 183, 404, 68);
        DrawTouchBar(e.Graphics, scale);

        var knobTop = 274f;
        var knobCenters = new[] { 253f, 350f, 446f, 543f };
        for (var i = 0; i < knobCenters.Length; i++)
        {
            _knobRects[i] = At(knobCenters[i] - 32, knobTop, 64, 64);
            DrawKnob(e.Graphics, i, _knobRects[i], scale);
        }

        DrawHint(e.Graphics, At(125, 371, 550, 24), scale);
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
        else
            DrawPlaceholder(graphics, (index + 1).ToString(), rect, scale);

        var borderColor = active ? Palette.Accent : hovered ? Palette.KeyHoverEdge : Palette.KeyEdge;
        using var border = new Pen(borderColor, Math.Max(1f, (active ? 2f : 1f) * scale));
        graphics.DrawRoundedRectangle(border, rect, radius);

        if (active)
        {
            using var pressedOverlay = new SolidBrush(Color.FromArgb(45, Palette.Accent));
            graphics.FillRoundedRectangle(pressedOverlay, rect, radius);
        }
    }

    private void DrawPlaceholder(Graphics graphics, string text, RectangleF rect, float scale)
    {
        var badge = new RectangleF(
            rect.Left + rect.Width / 2 - 11 * scale,
            rect.Top + rect.Height / 2 - 11 * scale,
            22 * scale,
            22 * scale);
        using var badgeFill = new SolidBrush(Palette.PlaceholderBadge);
        graphics.FillEllipse(badgeFill, badge);
        using var font = new Font(Font.FontFamily, Math.Max(7f, 8.5f * scale), FontStyle.Bold, GraphicsUnit.Pixel);
        TextRenderer.DrawText(graphics, text, font, Rectangle.Round(badge), Palette.SecondaryText,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }

    private void DrawTouchBar(Graphics graphics, float scale)
    {
        var radius = 10 * scale;
        using (var shadow = new SolidBrush(Color.FromArgb(115, 0, 0, 0)))
            graphics.FillRoundedRectangle(shadow, Offset(_touchRect, 0, 3 * scale), radius);
        using (var touch = new SolidBrush(Palette.Touch))
            graphics.FillRoundedRectangle(touch, _touchRect, radius);

        var clipState = graphics.Save();
        using (var path = RoundedRectangleExtensions.Path(_touchRect, radius))
            graphics.SetClip(path);

        // BGPIC is the touch-display background, not a wallpaper for the
        // complete device face. Drawing it here prevents bright touch artwork
        // from leaking through the key grid and the encoder area.
        if (_touchMode == TouchDisplayMode.TouchBar && BackgroundImageValue is not null)
            graphics.DrawImage(BackgroundImageValue, _touchRect);

        if (_touchMode == TouchDisplayMode.Button)
        {
            for (var i = 0; i < _secondaryKeys.Length; i++)
            {
                var segment = TouchSegment(i);
                if (_secondaryKeys[i] is not null)
                    graphics.DrawImage(_secondaryKeys[i]!, segment);
                if (i == _hoverSecondary || i == _activeSecondary)
                {
                    using var hover = new SolidBrush(Color.FromArgb(i == _activeSecondary ? 55 : 28, Palette.Accent));
                    graphics.FillRectangle(hover, segment);
                }
                if (i > 0)
                {
                    using var divider = new Pen(Palette.TouchDivider, Math.Max(1, scale));
                    graphics.DrawLine(divider, segment.Left, segment.Top + 10 * scale, segment.Left, segment.Bottom - 10 * scale);
                }
            }
        }
        else if (_hoverTouch || _touching)
        {
            using var hover = new SolidBrush(Color.FromArgb(_touching ? 35 : 18, Palette.Accent));
            graphics.FillRectangle(hover, _touchRect);
        }

        graphics.Restore(clipState);

        using (var border = new Pen(_touching ? Palette.Accent : Palette.TouchEdge,
                   Math.Max(1f, (_touching ? 2f : 1.5f) * scale)))
            graphics.DrawRoundedRectangle(border, _touchRect, radius);

        if (_touchMode == TouchDisplayMode.TouchBar && BackgroundImageValue is null)
        {
            using var titleFont = new Font(Font.FontFamily, Math.Max(8f, 10f * scale), FontStyle.Bold, GraphicsUnit.Pixel);
            using var hintFont = new Font(Font.FontFamily, Math.Max(7f, 8.5f * scale), FontStyle.Regular, GraphicsUnit.Pixel);
            var titleRect = Rectangle.Round(new RectangleF(_touchRect.Left, _touchRect.Top + 17 * scale, _touchRect.Width, 16 * scale));
            var hintRect = Rectangle.Round(new RectangleF(_touchRect.Left, _touchRect.Top + 34 * scale, _touchRect.Width, 15 * scale));
            TextRenderer.DrawText(graphics, "TOUCH BAR", titleFont, titleRect, Palette.SecondaryText,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            TextRenderer.DrawText(graphics, "нажмите или проведите в сторону", hintFont, hintRect, Palette.MutedText,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }

        else if (_touchMode == TouchDisplayMode.Button && _secondaryKeys.All(image => image is null))
        {
            using var hintFont = new Font(Font.FontFamily, Math.Max(7f, 8.5f * scale), FontStyle.Regular, GraphicsUnit.Pixel);
            for (var i = 0; i < _secondaryKeys.Length; i++)
                TextRenderer.DrawText(graphics, $"Энкодер {i + 1}", hintFont, Rectangle.Round(TouchSegment(i)), Palette.MutedText,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }

        DrawChevron(graphics, _touchRect.Left - 22 * scale, _touchRect.Top + _touchRect.Height / 2, -1, scale);
        DrawChevron(graphics, _touchRect.Right + 22 * scale, _touchRect.Top + _touchRect.Height / 2, 1, scale);
    }

    private static void DrawChevron(Graphics graphics, float centerX, float centerY, int direction, float scale)
    {
        var points = direction < 0
            ? new[] { new PointF(centerX + 4 * scale, centerY - 8 * scale), new PointF(centerX - 4 * scale, centerY), new PointF(centerX + 4 * scale, centerY + 8 * scale) }
            : new[] { new PointF(centerX - 4 * scale, centerY - 8 * scale), new PointF(centerX + 4 * scale, centerY), new PointF(centerX - 4 * scale, centerY + 8 * scale) };
        using var pen = new Pen(Palette.MutedText, Math.Max(1.5f, 2f * scale))
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round,
        };
        graphics.DrawLines(pen, points);
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
        graphics.DrawLine(marker,
            centerX, rect.Top + 10 * scale,
            centerX, rect.Top + 22 * scale);
    }

    private void DrawHint(Graphics graphics, RectangleF rect, float scale)
    {
        using var font = new Font(Font.FontFamily, Math.Max(7f, 8.5f * scale), FontStyle.Regular, GraphicsUnit.Pixel);
        var hint = _touchMode == TouchDisplayMode.Button
            ? "Экран: клик — функция  •  свайп ↑ — Touchbar  •  энкодер: колесо — вращение"
            : "Touchbar: касание — функция  •  свайп ↓ — Button Mode  •  энкодер: колесо — вращение";
        TextRenderer.DrawText(graphics, hint, font,
            Rectangle.Round(rect), Palette.MutedText,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }

    private static void DrawClippedImage(Graphics graphics, Image image, RectangleF rect, float radius)
    {
        var state = graphics.Save();
        using var path = RoundedRectangleExtensions.Path(rect, radius);
        graphics.SetClip(path);
        graphics.DrawImage(image, rect);
        graphics.Restore(state);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        for (var i = 0; i < _keyRects.Length; i++)
            if (_keyRects[i].Contains(e.Location))
            {
                _activeKey = i;
                Emit(InputReportFactory.Key(N4ProProfile.KeyCodes[i], true), $"Кнопка {i + 1}: нажата");
                Invalidate();
                return;
            }
        for (var i = 0; i < _knobRects.Length; i++)
            if (_knobRects[i].Contains(e.Location))
            {
                _activeKnob = i;
                Emit(InputReportFactory.KnobPress(i), $"Энкодер {i + 1}: нажат");
                Invalidate();
                return;
            }
        if (_touchRect.Contains(e.Location))
        {
            _touching = true;
            _touchStart = e.Location;
            _activeSecondary = _touchMode == TouchDisplayMode.Button ? SegmentAt(e.Location) : -1;
            if (_touchMode == TouchDisplayMode.TouchBar)
                EmitTouch(e.Location);
            Invalidate();
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_touching)
        {
            if (_touchMode == TouchDisplayMode.TouchBar)
                EmitTouch(e.Location);
        }
        UpdateHover(e.Location);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hoverKey = _hoverKnob = _hoverSecondary = -1;
        _hoverTouch = false;
        Cursor = Cursors.Default;
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (_activeKey >= 0)
        {
            Emit(InputReportFactory.Key(N4ProProfile.KeyCodes[_activeKey], false), $"Кнопка {_activeKey + 1}: отпущена");
            _activeKey = -1;
        }
        if (_activeKnob >= 0)
        {
            _activeKnob = -1;
        }
        if (_touching)
        {
            var deltaX = e.X - _touchStart.X;
            var deltaY = e.Y - _touchStart.Y;
            var verticalSwipe = Math.Abs(deltaY) > Math.Max(24, _touchRect.Height / 3) && Math.Abs(deltaY) > Math.Abs(deltaX);
            var horizontalSwipe = Math.Abs(deltaX) > Math.Max(30, _touchRect.Width / 10);
            if (verticalSwipe)
            {
                SetTouchMode(deltaY < 0 ? TouchDisplayMode.TouchBar : TouchDisplayMode.Button);
            }
            else if (horizontalSwipe)
            {
                Emit(InputReportFactory.Swipe(deltaX < 0), deltaX < 0 ? "Свайп влево" : "Свайп вправо");
            }
            else if (_touchMode == TouchDisplayMode.Button && _activeSecondary >= 0)
            {
                Emit(InputReportFactory.SecondaryTap(_activeSecondary), $"Функция энкодера {_activeSecondary + 1}");
            }
            _touching = false;
            _activeSecondary = -1;
        }
        UpdateHover(e.Location);
        Invalidate();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        for (var i = 0; i < _knobRects.Length; i++)
            if (_knobRects[i].Contains(PointToClient(MousePosition)))
            {
                var direction = e.Delta < 0 ? -1 : 1;
                Emit(InputReportFactory.KnobRotate(i, direction), $"Энкодер {i + 1}: {(direction < 0 ? "влево" : "вправо")}");
                break;
            }
    }

    private void UpdateHover(Point location)
    {
        var oldKey = _hoverKey;
        var oldKnob = _hoverKnob;
        var oldSecondary = _hoverSecondary;
        var oldTouch = _hoverTouch;
        _hoverKey = Array.FindIndex(_keyRects, rect => rect.Contains(location));
        _hoverKnob = Array.FindIndex(_knobRects, rect => rect.Contains(location));
        _hoverTouch = _touchRect.Contains(location);
        _hoverSecondary = _hoverTouch && _touchMode == TouchDisplayMode.Button ? SegmentAt(location) : -1;
        Cursor = _hoverKey >= 0 || _hoverKnob >= 0 || _hoverTouch ? Cursors.Hand : Cursors.Default;
        if (oldKey != _hoverKey || oldKnob != _hoverKnob || oldSecondary != _hoverSecondary || oldTouch != _hoverTouch)
            Invalidate();
    }

    private void SetTouchMode(TouchDisplayMode mode)
    {
        if (_touchMode == mode) return;
        _touchMode = mode;
        _activeSecondary = -1;
        _hoverSecondary = -1;
        StatusChanged?.Invoke(mode == TouchDisplayMode.Button
            ? "Button Mode: экран связан с четырьмя энкодерами"
            : "Touchbar Mode: экран принимает координатные касания");
        Invalidate();
    }

    private int SegmentAt(Point location) =>
        Math.Clamp((int)((location.X - _touchRect.Left) / (_touchRect.Width / 4)), 0, 3);

    private RectangleF TouchSegment(int index) =>
        new(_touchRect.Left + index * _touchRect.Width / 4, _touchRect.Top, _touchRect.Width / 4, _touchRect.Height);

    private void EmitTouch(Point location)
    {
        var x = (ushort)Math.Clamp((location.X - _touchRect.Left) / _touchRect.Width * LogicalWidth, 0, LogicalWidth - 1);
        var y = (ushort)Math.Clamp((location.Y - _touchRect.Top) / _touchRect.Height * LogicalHeight, 0, LogicalHeight - 1);
        Emit(InputReportFactory.Touch(x, y), $"Touch: {x}, {y}");
    }

    private void Emit(byte[] report, string description) => InputGenerated?.Invoke(report, description);

    private static RectangleF Offset(RectangleF rect, float x, float y) =>
        new(rect.X + x, rect.Y + y, rect.Width, rect.Height);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            foreach (var image in _keys) image?.Dispose();
            foreach (var image in _secondaryKeys) image?.Dispose();
            BackgroundImageValue?.Dispose();
        }
        base.Dispose(disposing);
    }
}

internal enum TouchDisplayMode
{
    Button,
    TouchBar,
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
    public static readonly Color TouchDivider = Color.FromArgb(53, 56, 63);
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
    public static void FillRoundedRectangle(this Graphics graphics, Brush brush, RectangleF bounds, float radius)
        => graphics.FillPath(brush, Path(bounds, radius));
    public static void DrawRoundedRectangle(this Graphics graphics, Pen pen, RectangleF bounds, float radius)
        => graphics.DrawPath(pen, Path(bounds, radius));

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
