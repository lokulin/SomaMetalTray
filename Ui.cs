using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace SomaMetalTray;

// Shared look-and-feel pieces, borrowed from the SpaceStation tray player so the two apps feel alike:
// Segoe Fluent icon glyphs, the icon button, and the dark rounded popup menu.

/// <summary>Fonts: Segoe UI Variable where Windows has it (11), Segoe UI otherwise; Segoe Fluent Icons for glyphs (MDL2 on Windows 10).</summary>
internal static class UiFonts
{
    private static readonly HashSet<string> Installed = new InstalledFontCollection().Families.Select(f => f.Name).ToHashSet();

    public static readonly string TextFamily = Installed.Contains("Segoe UI Variable Text") ? "Segoe UI Variable Text" : "Segoe UI";
    public static readonly string IconFamily = Installed.Contains("Segoe Fluent Icons") ? "Segoe Fluent Icons" : "Segoe MDL2 Assets";

    public static Font Body(float size, FontStyle style = FontStyle.Regular) => new(TextFamily, size, style, GraphicsUnit.Point);
    public static Font Icon(float size) => new(IconFamily, size, FontStyle.Regular, GraphicsUnit.Point);
}

/// <summary>Code points in Segoe Fluent Icons / Segoe MDL2 Assets.</summary>
internal static class Glyphs
{
    public static readonly string Heart = ((char)0xEB51).ToString();
    public static readonly string HeartFilled = ((char)0xEB52).ToString();
    public static readonly string Cast = ((char)0xEC15).ToString();
    public static readonly string Chevron = ((char)0xE76C).ToString();
    public static readonly string ChevronDown = ((char)0xE70D).ToString();
    public static readonly string ChevronUp = ((char)0xE70E).ToString();
    public static readonly string Check = ((char)0xE73E).ToString();
    public static readonly string History = ((char)0xE81C).ToString();
    public static readonly string Copy = ((char)0xE8C8).ToString();
    public static readonly string Clear = ((char)0xE74D).ToString();
    public static readonly string Settings = ((char)0xE713).ToString();
    public static readonly string Music = ((char)0xE8D6).ToString();
    public static readonly string Minimize = ((char)0xE921).ToString();
    public static readonly string Exit = ((char)0xE7E8).ToString();
}

internal static class Draw
{
    public static GraphicsPath Rounded(RectangleF rect, float radius)
    {
        radius = Math.Max(0.5f, Math.Min(radius, Math.Min(rect.Width, rect.Height) / 2));
        float d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static readonly Dictionary<(string Family, float Size), float> GlyphOffsets = new();

    /// <summary>
    /// How far (in pixels) an icon-font glyph has to be moved down so its ink - not its line box - is centred in the rectangle it is drawn in.
    /// Icon fonts have tall, lopsided line boxes, so a glyph "centred" by the text layout sits several pixels low beside text. Measured once per
    /// font by drawing a symmetric reference glyph (the maximize square) and finding where its ink landed.
    /// </summary>
    public static float GlyphOffset(Font font)
    {
        var key = (font.FontFamily.Name, font.Size);
        lock (GlyphOffsets)
        {
            if (GlyphOffsets.TryGetValue(key, out float known))
                return known;
        }

        float offset = 0;
        try
        {
            int box = Math.Max(24, (int)Math.Ceiling(font.SizeInPoints * 4));
            using var bitmap = new Bitmap(box, box);
            using (var g = Graphics.FromImage(bitmap))
            {
                g.Clear(Color.Black);
                DrawGlyphInRectangle(g, ((char)0xE922).ToString(), font, Color.White, new RectangleF(0, 0, box, box));
            }

            int top = -1, bottom = -1;
            for (int y = 0; y < box; y++)
            {
                for (int x = 0; x < box; x++)
                {
                    if (bitmap.GetPixel(x, y).R > 96)
                    {
                        if (top < 0) top = y;
                        bottom = y;
                        break;
                    }
                }
            }
            if (top >= 0)
                offset = box / 2f - (top + bottom + 1) / 2f;
        }
        catch (Exception ex) when (ex is ArgumentException or ExternalException)
        {
            offset = 0; // no measurement: draw as the layout says
        }

        lock (GlyphOffsets) GlyphOffsets[key] = offset;
        return offset;
    }

    private static void DrawGlyphInRectangle(Graphics g, string glyph, Font font, Color color, RectangleF bounds)
    {
        using var brush = new SolidBrush(color);
        using var format = new StringFormat(StringFormat.GenericTypographic)
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
            FormatFlags = StringFormatFlags.NoWrap | StringFormatFlags.NoClip,
        };
        TextRenderingHint old = g.TextRenderingHint;
        g.TextRenderingHint = TextRenderingHint.AntiAlias;
        g.DrawString(glyph, font, brush, bounds, format);
        g.TextRenderingHint = old;
    }

    /// <summary>A glyph from the icon font, with its ink centred in <paramref name="bounds"/>.</summary>
    public static void Glyph(Graphics g, string glyph, Font font, Color color, RectangleF bounds)
    {
        var shifted = new RectangleF(bounds.X, bounds.Y + GlyphOffset(font), bounds.Width, bounds.Height);
        DrawGlyphInRectangle(g, glyph, font, color, shifted);
    }
}

/// <summary>A flat icon button: a glyph that brightens on hover, turns the accent colour when <see cref="Active"/> and dims when disabled.</summary>
internal sealed class IconButton : Control
{
    private string _glyph;
    private bool _hover;
    private bool _active;
    private readonly Font _font;

    public event EventHandler? Activated;

    public IconButton(string glyph, float size = 14f)
    {
        _glyph = glyph;
        _font = UiFonts.Icon(size);
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        Cursor = Cursors.Hand;
        Size = new Size(40, 40);
        TabStop = false;
    }

    /// <summary>The glyph drawn (changes with state: heart outline / filled).</summary>
    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string Glyph
    {
        get => _glyph;
        set
        {
            if (_glyph == value) return;
            _glyph = value;
            Invalidate();
        }
    }

    /// <summary>Lit up (the track is liked, a cast is running).</summary>
    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Active
    {
        get => _active;
        set
        {
            if (_active == value) return;
            _active = value;
            Invalidate();
        }
    }

    /// <summary>Colour used instead of the accent while active (the Cast icon goes green).</summary>
    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color? ActiveColor { get; set; }

    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (Enabled && e.Button == MouseButtons.Left && ClientRectangle.Contains(e.Location))
            Activated?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        if (_hover && Enabled)
        {
            using GraphicsPath path = Draw.Rounded(new RectangleF(3, 3, Width - 6, Height - 6), 10);
            using var fill = new SolidBrush(Color.FromArgb(70, UiColors.Surface));
            g.FillPath(fill, path);
        }

        Color color = !Enabled ? UiColors.TextFaint
            : _active ? (ActiveColor ?? UiColors.Accent)
            : _hover ? UiColors.Text
            : UiColors.TextDim;
        Draw.Glyph(g, _glyph, _font, color, new RectangleF(0, 0, Width, Height));
    }
}

/// <summary>
/// A pill that shows the current choice and a chevron - the "styled drop-down". It only raises <see cref="Clicked"/>; the owner pops up
/// a <see cref="ThemedMenu"/> next to it.
/// </summary>
internal sealed class DropdownButton : Control
{
    private bool _hover;
    private bool _open;
    private readonly Font _chevronFont = UiFonts.Icon(8f);

    public event EventHandler? Clicked;

    public DropdownButton()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        Cursor = Cursors.Hand;
        Font = UiFonts.Body(10f);
        Size = new Size(168, 34);
        TabStop = false;
    }

    /// <summary>Marks the pill as open while its menu is showing.</summary>
    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool IsOpen
    {
        get => _open;
        set
        {
            if (_open == value) return;
            _open = value;
            Invalidate();
        }
    }

    protected override void OnTextChanged(EventArgs e) { Invalidate(); base.OnTextChanged(e); }
    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button == MouseButtons.Left && ClientRectangle.Contains(e.Location))
            Clicked?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var rect = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        using (GraphicsPath path = Draw.Rounded(rect, Height / 2f))
        {
            using var fill = new SolidBrush(_hover || _open ? UiColors.SurfaceHover : Color.FromArgb(200, UiColors.Surface));
            g.FillPath(fill, path);
            using var border = new Pen(_open ? UiColors.Accent : UiColors.Border);
            g.DrawPath(border, path);
        }

        var textRect = new Rectangle(14, 0, Width - 14 - 30, Height);
        TextRenderer.DrawText(g, Text, Font, textRect, _hover || _open ? UiColors.TextBright : UiColors.Text,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        Draw.Glyph(g, _open ? Glyphs.ChevronUp : Glyphs.ChevronDown, _chevronFont, UiColors.TextDim, new RectangleF(Width - 30, 0, 24, Height));
    }
}

/// <summary>
/// Context menus in the player's look: dark rounded popup, a leading icon on each item, and a red rounded highlight. On Windows 11 the
/// corners and 1 px border come from the window manager; on older Windows the popup stays square with a drawn border.
/// </summary>
internal static class ThemedMenu
{
    private static readonly Font IconFont = UiFonts.Icon(11f);

    // A transparent image in every item reserves the icon column (the glyph itself is drawn by the renderer).
    private static readonly Bitmap IconSpace = new(20, 20);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    public static ContextMenuStrip Create()
    {
        var menu = new ContextMenuStrip
        {
            Renderer = new MenuRenderer(),
            BackColor = UiColors.Surface,
            ForeColor = UiColors.Text,
            Font = UiFonts.Body(10f),
            ShowImageMargin = true,
            Padding = new Padding(4),
        };
        menu.HandleCreated += (_, _) =>
        {
            if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)) return;
            int round = 2; // DWMWCP_ROUND
            DwmSetWindowAttribute(menu.Handle, 33, ref round, sizeof(int));
            int border = UiColors.Border.R | (UiColors.Border.G << 8) | (UiColors.Border.B << 16);
            DwmSetWindowAttribute(menu.Handle, 34, ref border, sizeof(int));
        };
        return menu;
    }

    /// <summary>
    /// Shows a menu at a screen position and disposes it once it has closed. The disposal is deferred: the menu closes from inside the
    /// click that picked an item, and disposing it right then crashes WinForms ("Cannot access a disposed object").
    /// </summary>
    public static void ShowAndDispose(Control owner, ContextMenuStrip menu, Point screenLocation, Action? closed = null,
        ToolStripDropDownDirection direction = ToolStripDropDownDirection.BelowRight)
    {
        menu.Closed += (_, _) =>
        {
            closed?.Invoke();
            if (!owner.IsDisposed && owner.IsHandleCreated)
                owner.BeginInvoke(new Action(menu.Dispose));
        };
        menu.Show(screenLocation, direction);
    }

    /// <summary>Adds an item with a leading icon (or a tick when it is checked).</summary>
    public static ToolStripMenuItem Add(ContextMenuStrip menu, string text, EventHandler onClick, string? glyph = null, bool isChecked = false)
    {
        var item = new ToolStripMenuItem(text, IconSpace, onClick)
        {
            Tag = glyph ?? "",
            Checked = isChecked,
            ImageScaling = ToolStripItemImageScaling.None,
            Padding = new Padding(6, 7, 14, 7),
            AutoSize = true,
        };
        menu.Items.Add(item);
        return item;
    }

    /// <summary>A non-clickable heading line in a menu (e.g. "Cast to").</summary>
    public static ToolStripLabel Heading(string text) => new(text)
    {
        ForeColor = UiColors.TextFaint,
        Font = UiFonts.Body(8.5f, FontStyle.Bold),
        Padding = new Padding(10, 6, 10, 4),
    };

    private sealed class Colors : ProfessionalColorTable
    {
        public Colors() => UseSystemColors = false;
        public override Color ToolStripDropDownBackground => UiColors.Surface;
        public override Color MenuBorder => UiColors.Border;
        public override Color MenuItemBorder => UiColors.Accent;
        public override Color MenuItemSelected => UiColors.Accent;
        public override Color MenuItemSelectedGradientBegin => UiColors.Accent;
        public override Color MenuItemSelectedGradientEnd => UiColors.Accent;
        public override Color ImageMarginGradientBegin => UiColors.Surface;
        public override Color ImageMarginGradientMiddle => UiColors.Surface;
        public override Color ImageMarginGradientEnd => UiColors.Surface;
        public override Color SeparatorDark => UiColors.Border;
        public override Color SeparatorLight => UiColors.Border;
    }

    private sealed class MenuRenderer : ToolStripProfessionalRenderer
    {
        private static readonly bool WindowManagerRounds = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000);

        public MenuRenderer() : base(new Colors()) => RoundedEdges = false;

        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        {
            using var fill = new SolidBrush(UiColors.Surface);
            e.Graphics.FillRectangle(fill, e.AffectedBounds);
        }

        protected override void OnRenderImageMargin(ToolStripRenderEventArgs e)
        {
            using var fill = new SolidBrush(UiColors.Surface);
            e.Graphics.FillRectangle(fill, e.AffectedBounds);
        }

        protected override void OnRenderItemImage(ToolStripItemImageRenderEventArgs e)
        {
            // The glyph is drawn with the text.
        }

        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            // The stock renderer paints a light box with a tick in it; the tick is drawn with the text instead.
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            Color color = !e.Item.Enabled ? UiColors.TextFaint
                : e.Item is ToolStripLabel ? e.Item.ForeColor
                : e.Item.Selected ? UiColors.TextBright : UiColors.Text;

            // Centred in the whole height of the item, as the icon is (the stock layout leaves the text above the middle of the row).
            var area = new Rectangle(e.TextRectangle.X, 0, Math.Max(1, e.Item.Width - e.TextRectangle.X - 8), e.Item.Height);
            TextRenderer.DrawText(e.Graphics, e.Text, e.TextFont, area, color,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

            if (e.Item is ToolStripMenuItem item)
            {
                // A ticked item shows a tick where its icon would be.
                string? glyph = item.Checked ? Glyphs.Check : item.Tag as string;
                if (!string.IsNullOrEmpty(glyph))
                {
                    e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                    Draw.Glyph(e.Graphics, glyph, IconFont, item.Selected ? UiColors.TextBright : item.Checked ? UiColors.Accent : UiColors.TextDim, new RectangleF(6, 0, 24, item.Height));
                }
            }
        }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            if (e.Item is ToolStripMenuItem { Enabled: true, Selected: true })
            {
                using GraphicsPath path = Draw.Rounded(new RectangleF(1, 1, e.Item.Width - 2, e.Item.Height - 2), 8);
                using var fill = new SolidBrush(UiColors.Accent);
                e.Graphics.FillPath(fill, path);
            }
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            if (WindowManagerRounds) return; // Windows draws the rounded border itself
            using var pen = new Pen(UiColors.Border);
            e.Graphics.DrawRectangle(pen, 0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            using var pen = new Pen(UiColors.Border);
            int y = e.Item.Height / 2;
            e.Graphics.DrawLine(pen, 10, y, e.Item.Width - 10, y);
        }
    }
}
