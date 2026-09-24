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

/// <summary>Large circular Play/Stop toggle button (~80px) - the sole playback control.</summary>
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
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
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
            // Stop-square glyph - this is the only playback control now (see
            // class remarks); a real stop, not a pause, so the icon says so.
            int side = Height / 3;
            var stopRect = new Rectangle((Width - side) / 2, (Height - side) / 2, side, side);
            using var path = new GraphicsPath();
            path.AddRectangle(stopRect);
            g.FillPath(glyphBrush, path);
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
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
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

/// <summary>Which glyph a <see cref="TitleBarButton"/> draws.</summary>
internal enum TitleBarGlyph
{
    Minimize,
    Close,
}

/// <summary>
/// A minimize/close button for the custom borderless title bar (see
/// PlayerForm.DrawTitleBar/OnPaint) - plain flat glyph, Windows 11-style
/// hover highlight (subtle grey for minimize, red for close).
/// </summary>
internal sealed class TitleBarButton : Control
{
    private readonly TitleBarGlyph _glyph;
    private bool _hover;

    public event EventHandler? Activated;

    public TitleBarButton(TitleBarGlyph glyph)
    {
        _glyph = glyph;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        Cursor = Cursors.Default;
        Size = new Size(46, 32);
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button == MouseButtons.Left && ClientRectangle.Contains(e.Location))
            Activated?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        if (_hover)
        {
            Color hoverFill = _glyph == TitleBarGlyph.Close ? Color.FromArgb(0xc4, 0x2b, 0x1c) : Color.FromArgb(40, Color.White);
            using var hoverBrush = new SolidBrush(hoverFill);
            g.FillRectangle(hoverBrush, ClientRectangle);
        }

        using var pen = new Pen(Color.Gainsboro, 1f);
        int cx = Width / 2;
        int cy = Height / 2;

        if (_glyph == TitleBarGlyph.Minimize)
        {
            g.DrawLine(pen, cx - 5, cy, cx + 5, cy);
        }
        else
        {
            g.DrawLine(pen, cx - 5, cy - 5, cx + 5, cy + 5);
            g.DrawLine(pen, cx - 5, cy + 5, cx + 5, cy - 5);
        }
    }
}
