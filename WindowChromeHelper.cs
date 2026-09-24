using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace SomaMetalTray;

/// <summary>
/// DWM/Win32 interop backing the fully custom, borderless title bar (see
/// PlayerForm's DrawTitleBar/OnMouseDown) - a standard FormBorderStyle.FixedSingle
/// native caption turned out to always show a faint 1px seam where it met our
/// own gradient (confirmed via a magenta DWMWA_BORDER_COLOR diagnostic that
/// DeathFmTray, which has no seam, reports the identical native frame
/// thickness, so no DWM attribute here was ever going to remove it). Going
/// fully borderless removes the native frame entirely instead of trying to
/// colour-match it.
/// </summary>
internal static class WindowChromeHelper
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaCaptionColor = 35;
    private const int DwmwaTextColor = 36;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwcpRound = 2;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr GetSystemMenu(IntPtr hWnd, bool bRevert);

    [DllImport("user32.dll")]
    private static extern bool TrackPopupMenu(IntPtr hMenu, uint uFlags, int x, int y, int nReserved, IntPtr hWnd, IntPtr prcRect);

    private const int WmNclbuttondown = 0x00A1;
    private const int HtCaption = 0x0002;
    private const uint TpmRightbutton = 0x0002;

    /// <summary>
    /// For the main, now-borderless PlayerForm: still worth applying even
    /// with no native caption left to colour - dark mode affects the system
    /// menu popup and other DWM-owned chrome (Alt-Tab preview border,
    /// minimize/restore animations), and rounded corners are otherwise lost
    /// once FormBorderStyle is None.
    /// </summary>
    public static void ApplyDarkChrome(Form form)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763))
            return;

        IntPtr hwnd = form.Handle;

        int enableDarkMode = 1;
        DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref enableDarkMode, sizeof(int));

        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            int cornerPreference = DwmwcpRound;
            DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref cornerPreference, sizeof(int));
        }
    }

    /// <summary>
    /// For secondary dialogs (SettingsForm) that still use a normal native
    /// title bar - unaffected by the seam investigation above, since a small
    /// dialog's caption never showed the same visible artifact.
    /// </summary>
    public static void ApplyDarkTitleBar(Form form, Color captionColor, Color textColor)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763))
            return;

        IntPtr hwnd = form.Handle;

        int enableDarkMode = 1;
        DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref enableDarkMode, sizeof(int));

        int captionColorRef = ToColorRef(captionColor);
        DwmSetWindowAttribute(hwnd, DwmwaCaptionColor, ref captionColorRef, sizeof(int));

        int textColorRef = ToColorRef(textColor);
        DwmSetWindowAttribute(hwnd, DwmwaTextColor, ref textColorRef, sizeof(int));
    }

    /// <summary>
    /// Hands an in-progress left-button drag on our own custom title bar off
    /// to the OS exactly as if the user had grabbed a native caption - gives
    /// normal move behaviour (including edge snapping) for free. Call from a
    /// MouseDown handler on the title bar area, with the mouse button still down.
    /// </summary>
    public static void BeginDrag(IntPtr hwnd)
    {
        ReleaseCapture();
        SendMessage(hwnd, WmNclbuttondown, (IntPtr)HtCaption, IntPtr.Zero);
    }

    /// <summary>
    /// Shows the window's existing system menu (the same HMENU SystemMenuHelper
    /// populates - see PlayerForm.BuildSystemMenu) at a screen position, since a
    /// borderless window has no native caption for Windows to attach its usual
    /// right-click/Alt+Space triggers to. Selecting an item posts WM_SYSCOMMAND
    /// to hwnd on its own (standard behaviour for a system HMENU via TrackPopupMenu),
    /// so PlayerForm's existing WM_SYSCOMMAND handling needs no changes.
    /// </summary>
    public static void ShowSystemMenu(IntPtr hwnd, Point screenLocation)
    {
        IntPtr hMenu = GetSystemMenu(hwnd, false);
        if (hMenu == IntPtr.Zero)
            return;

        TrackPopupMenu(hMenu, TpmRightbutton, screenLocation.X, screenLocation.Y, 0, hwnd, IntPtr.Zero);
    }

    // Win32 COLORREF is 0x00BBGGRR.
    private static int ToColorRef(Color c) => c.R | (c.G << 8) | (c.B << 16);
}
