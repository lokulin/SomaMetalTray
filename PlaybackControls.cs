using System;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace SomaMetalTray;

/// <summary>
/// Custom-painted playback/volume controls matching the redesigned player UI
/// (dark background, thin red borders, rounded shapes) - none of this reads
/// as a native WinForms Button/TrackBar, so plain owner painting on a Control
/// subclass is used throughout rather than fighting FlatStyle/visual styles.
/// </summary>
internal static class UiColors
{
    public static readonly Color Accent = Color.FromArgb(0xc0, 0x30, 0x30);
    public static readonly Color AccentHover = Color.FromArgb(0xe0, 0x45, 0x45);
    public static readonly Color AccentPressed = Color.FromArgb(0x90, 0x20, 0x20);
    public static readonly Color ButtonFill = Color.FromArgb(0x28, 0x10, 0x10);
    public static readonly Color ButtonFillHover = Color.FromArgb(0x36, 0x14, 0x14);
    public static readonly Color TrackDark = Color.FromArgb(0x2a, 0x22, 0x22);
}

/// <summary>Large circular Play/Pause-style toggle button (~80px).</summary>
internal sealed class CirclePlayButton : Control
{
    private bool _hover;
    private bool _pressed;
    private bool _isPlaying;

    public event EventHandler? Toggled;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool IsPlaying
    {
        get => _isPlaying;
        set
        {
            if (_isPlaying == value) return;
            _isPlaying = value;
            Invalidate();
        }
    }

    public CirclePlayButton()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Color.Transparent;
        Cursor = Cursors.Hand;
        Size = new Size(80, 80);
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; _pressed = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { _pressed = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e)
    {
        bool wasPressed = _pressed;
        _pressed = false;
        Invalidate();
        base.OnMouseUp(e);
        if (wasPressed && ClientRectangle.Contains(e.Location))
            Toggled?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        int inset = _pressed ? 2 : 0;
        var rect = new Rectangle(inset, inset, Width - 1 - inset * 2, Height - 1 - inset * 2);

        Color border = _pressed ? UiColors.AccentPressed : _hover ? UiColors.AccentHover : UiColors.Accent;
        Color fill = _hover ? UiColors.ButtonFillHover : UiColors.ButtonFill;

        using (var fillBrush = new SolidBrush(fill))
            g.FillEllipse(fillBrush, rect);
        using (var pen = new Pen(border, 2f))
            g.DrawEllipse(pen, rect);

        using var glyphBrush = new SolidBrush(Color.White);
        if (_isPlaying)
        {
            // Pause-style "II" glyph - purely a "click to toggle" convention,
            // not implying a real pause capability (see AudioPlayerService remarks).
            int barWidth = Math.Max(4, Width / 10);
            int barHeight = Height / 3;
            int gap = barWidth;
            int totalWidth = barWidth * 2 + gap;
            int startX = (Width - totalWidth) / 2;
            int y = (Height - barHeight) / 2;
            g.FillRectangle(glyphBrush, startX, y, barWidth, barHeight);
            g.FillRectangle(glyphBrush, startX + barWidth + gap, y, barWidth, barHeight);
        }
        else
        {
            // Play triangle, nudged slightly right of center to look visually centered.
            int triSize = Height / 3;
            int cx = Width / 2 + triSize / 6;
            int cy = Height / 2;
            var points = new[]
            {
                new Point(cx - triSize / 2, cy - triSize / 2),
                new Point(cx - triSize / 2, cy + triSize / 2),
                new Point(cx + triSize / 2, cy),
            };
            g.FillPolygon(glyphBrush, points);
        }
    }
}

/// <summary>Small square Stop button (~50px) - always stops regardless of current state.</summary>
internal sealed class SquareStopButton : Control
{
    private bool _hover;
    private bool _pressed;

    public event EventHandler? Clicked;

    public SquareStopButton()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Color.Transparent;
        Cursor = Cursors.Hand;
        Size = new Size(50, 50);
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; _pressed = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { _pressed = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e)
    {
        bool wasPressed = _pressed;
        _pressed = false;
        Invalidate();
        base.OnMouseUp(e);
        if (wasPressed && ClientRectangle.Contains(e.Location))
            Clicked?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        int inset = _pressed ? 2 : 0;
        var rect = new Rectangle(inset, inset, Width - 1 - inset * 2, Height - 1 - inset * 2);

        Color border = _pressed ? UiColors.AccentPressed : _hover ? UiColors.AccentHover : UiColors.Accent;
        Color fill = _hover ? UiColors.ButtonFillHover : UiColors.ButtonFill;

        using (var path = RoundedRect(rect, 6))
        {
            using (var fillBrush = new SolidBrush(fill))
                g.FillPath(fillBrush, path);
            using (var pen = new Pen(border, 1.5f))
                g.DrawPath(pen, path);
        }

        int glyphSize = Height / 3;
        var glyphRect = new Rectangle((Width - glyphSize) / 2, (Height - glyphSize) / 2, glyphSize, glyphSize);
        using var glyphBrush = new SolidBrush(Color.White);
        g.FillRectangle(glyphBrush, glyphRect);
    }

    private static GraphicsPath RoundedRect(Rectangle rect, int radius)
    {
        int d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}

/// <summary>Pill-shaped, owner-drawn horizontal volume slider (0.0-1.0) - no native TrackBar chrome.</summary>
internal sealed class VolumeSliderControl : Control
{
    private double _value = 0.8;
    private bool _dragging;

    public event EventHandler? ValueChanged;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public double Value
    {
        get => _value;
        set
        {
            double clamped = Math.Clamp(value, 0.0, 1.0);
            if (Math.Abs(clamped - _value) < 0.0001) return;
            _value = clamped;
            Invalidate();
        }
    }

    public VolumeSliderControl()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Color.Transparent;
        Cursor = Cursors.Hand;
        Height = 20;
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        _dragging = true;
        SetFromMouseX(e.X);
        base.OnMouseDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_dragging)
            SetFromMouseX(e.X);
        base.OnMouseMove(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        _dragging = false;
        base.OnMouseUp(e);
    }

    private void SetFromMouseX(int x)
    {
        int thumbRadius = Height / 2;
        int usableWidth = Math.Max(1, Width - thumbRadius * 2);
        double fraction = Math.Clamp((x - thumbRadius) / (double)usableWidth, 0.0, 1.0);
        Value = fraction;
        ValueChanged?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        int trackHeight = Math.Max(6, Height / 3);
        var trackRect = new Rectangle(0, (Height - trackHeight) / 2, Width, trackHeight);

        using (var trackPath = RoundedRect(trackRect, trackHeight / 2))
        {
            using var trackBrush = new SolidBrush(UiColors.TrackDark);
            g.FillPath(trackBrush, trackPath);
        }

        int filledWidth = (int)(Width * _value);
        if (filledWidth > 0)
        {
            var filledRect = new Rectangle(0, trackRect.Y, Math.Max(trackHeight, filledWidth), trackHeight);
            using var filledPath = RoundedRect(filledRect, trackHeight / 2);
            using var filledBrush = new SolidBrush(UiColors.Accent);
            g.FillPath(filledBrush, filledPath);
        }

        int thumbDiameter = Height;
        int thumbX = (int)((Width - thumbDiameter) * _value);
        var thumbRect = new Rectangle(thumbX, 0, thumbDiameter, thumbDiameter);
        using (var thumbBrush = new SolidBrush(Color.White))
            g.FillEllipse(thumbBrush, thumbRect);
        using (var thumbPen = new Pen(UiColors.Accent, 1.5f))
            g.DrawEllipse(thumbPen, thumbRect);
    }

    private static GraphicsPath RoundedRect(Rectangle rect, int radius)
    {
        radius = Math.Max(1, Math.Min(radius, Math.Min(rect.Width, rect.Height) / 2));
        int d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
