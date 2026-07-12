using Mirabox.Emulator.Core;

namespace Mirabox.Emulator.Panel;

internal sealed class DeviceSurface : Control
{
    private const int LogicalWidth = 800;
    private const int LogicalHeight = 480;
    private readonly Image?[] _keys = new Image?[10];
    private readonly Image?[] _secondaryKeys = new Image?[4];
    private readonly RectangleF[] _keyRects = new RectangleF[10];
    private readonly RectangleF[] _knobRects = new RectangleF[4];
    private RectangleF _touchRect;
    private int _activeKey = -1;
    private int _activeKnob = -1;
    private int _activeSecondary = -1;
    private Point _touchStart;
    private bool _touching;

    public event Action<byte[], string>? InputGenerated;
    public byte Brightness { get; set; } = 100;
    public Image? BackgroundImageValue { get; private set; }

    public DeviceSurface()
    {
        DoubleBuffered = true;
        BackColor = Color.FromArgb(22, 23, 27);
        ForeColor = Color.WhiteSmoke;
        MinimumSize = new Size(700, 520);
        SetStyle(ControlStyles.Selectable, true);
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
        e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        var scale = Math.Min((ClientSize.Width - 80f) / 720f, (ClientSize.Height - 60f) / 500f);
        var width = 720f * scale;
        var left = (ClientSize.Width - width) / 2;
        var top = 28f;
        var key = 112f * scale;
        var gap = 24f * scale;

        using var body = new SolidBrush(Color.FromArgb(39, 41, 47));
        using var edge = new Pen(Color.FromArgb(70, 73, 82), Math.Max(1, 2 * scale));
        var bodyRect = new RectangleF(left - 30 * scale, top - 18 * scale, width + 60 * scale, 455 * scale);
        e.Graphics.FillRoundedRectangle(body, bodyRect, 24 * scale);
        e.Graphics.DrawRoundedRectangle(edge, bodyRect, 24 * scale);

        if (BackgroundImageValue is not null)
        {
            var overlay = new RectangleF(left, top, width, 360 * scale);
            e.Graphics.DrawImage(BackgroundImageValue, overlay);
        }

        for (var row = 0; row < 2; row++)
        for (var col = 0; col < 5; col++)
        {
            var index = row * 5 + col;
            var x = left + col * (key + gap);
            var y = top + row * (key + gap);
            _keyRects[index] = new RectangleF(x, y, key, key);
            using var keyBrush = new SolidBrush(index == _activeKey ? Color.FromArgb(72, 78, 91) : Color.FromArgb(13, 14, 17));
            e.Graphics.FillRoundedRectangle(keyBrush, _keyRects[index], 10 * scale);
            if (_keys[index] is not null) e.Graphics.DrawImage(_keys[index]!, _keyRects[index]);
            else TextRenderer.DrawText(e.Graphics, (index + 1).ToString(), Font, Rectangle.Round(_keyRects[index]), Color.Gray,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        var knobY = top + 284 * scale;
        for (var i = 0; i < 4; i++)
        {
            var x = left + (i + 0.55f) * (width / 4) - 35 * scale;
            _knobRects[i] = new RectangleF(x, knobY, 70 * scale, 70 * scale);
            using var knob = new SolidBrush(i == _activeKnob ? Color.FromArgb(130, 137, 151) : Color.FromArgb(90, 94, 104));
            e.Graphics.FillEllipse(knob, _knobRects[i]);
            e.Graphics.DrawLine(Pens.WhiteSmoke, x + 35 * scale, knobY + 8 * scale, x + 35 * scale, knobY + 22 * scale);
        }

        _touchRect = new RectangleF(left, top + 374 * scale, width, 42 * scale);
        using var touch = new SolidBrush(Color.FromArgb(10, 11, 14));
        e.Graphics.FillRoundedRectangle(touch, _touchRect, 8 * scale);
        for (var i = 0; i < _secondaryKeys.Length; i++)
        {
            var segment = new RectangleF(_touchRect.Left + i * _touchRect.Width / 4, _touchRect.Top, _touchRect.Width / 4, _touchRect.Height);
            if (_secondaryKeys[i] is not null) e.Graphics.DrawImage(_secondaryKeys[i]!, segment);
            using var divider = new Pen(Color.FromArgb(55, 57, 64));
            if (i > 0) e.Graphics.DrawLine(divider, segment.Left, segment.Top + 3, segment.Left, segment.Bottom - 3);
        }
        if (_secondaryKeys.All(image => image is null))
            TextRenderer.DrawText(e.Graphics, "TOUCH BAR — проведите влево или вправо", Font, Rectangle.Round(_touchRect), Color.DimGray,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        for (var i = 0; i < _keyRects.Length; i++)
            if (_keyRects[i].Contains(e.Location))
            {
                _activeKey = i;
                Emit(InputReportFactory.Key(N4ProProfile.KeyCodes[i], true), $"Кнопка {i + 1}: down");
                Invalidate();
                return;
            }
        for (var i = 0; i < _knobRects.Length; i++)
            if (_knobRects[i].Contains(e.Location))
            {
                _activeKnob = i;
                Emit(InputReportFactory.KnobPress(i, true), $"Энкодер {i + 1}: down");
                Invalidate();
                return;
            }
        if (_touchRect.Contains(e.Location))
        {
            _touching = true;
            _touchStart = e.Location;
            _activeSecondary = Math.Clamp((int)((e.X - _touchRect.Left) / (_touchRect.Width / 4)), 0, 3);
            EmitTouch(e.Location);
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_touching) EmitTouch(e.Location);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (_activeKey >= 0)
        {
            Emit(InputReportFactory.Key(N4ProProfile.KeyCodes[_activeKey], false), $"Кнопка {_activeKey + 1}: up");
            _activeKey = -1;
        }
        if (_activeKnob >= 0)
        {
            Emit(InputReportFactory.KnobPress(_activeKnob, false), $"Энкодер {_activeKnob + 1}: up");
            _activeKnob = -1;
        }
        if (_touching)
        {
            var delta = e.X - _touchStart.X;
            if (Math.Abs(delta) > Math.Max(30, _touchRect.Width / 10))
                Emit(InputReportFactory.Swipe(delta < 0), delta < 0 ? "Свайп влево" : "Свайп вправо");
            else if (_activeSecondary >= 0)
                Emit(InputReportFactory.Key(N4ProProfile.SecondaryKeyCodes[_activeSecondary], false), $"Touch-кнопка {_activeSecondary + 1}");
            _touching = false;
            _activeSecondary = -1;
        }
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

    private void EmitTouch(Point location)
    {
        var x = (ushort)Math.Clamp((location.X - _touchRect.Left) / _touchRect.Width * LogicalWidth, 0, LogicalWidth - 1);
        var y = (ushort)Math.Clamp((location.Y - _touchRect.Top) / _touchRect.Height * LogicalHeight, 0, LogicalHeight - 1);
        Emit(InputReportFactory.Touch(x, y), $"Touch: {x}, {y}");
    }

    private void Emit(byte[] report, string description) => InputGenerated?.Invoke(report, description);

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

internal static class RoundedRectangleExtensions
{
    public static void FillRoundedRectangle(this Graphics graphics, Brush brush, RectangleF bounds, float radius)
        => graphics.FillPath(brush, Path(bounds, radius));
    public static void DrawRoundedRectangle(this Graphics graphics, Pen pen, RectangleF bounds, float radius)
        => graphics.DrawPath(pen, Path(bounds, radius));
    private static System.Drawing.Drawing2D.GraphicsPath Path(RectangleF r, float radius)
    {
        var path = new System.Drawing.Drawing2D.GraphicsPath();
        var d = radius * 2;
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
