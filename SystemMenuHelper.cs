using System;
using System.Runtime.InteropServices;

namespace SomaMetalTray;

/// <summary>
/// Appends custom items to a window's native system menu - the menu shown by
/// clicking the app icon at the top-left of the titlebar, right-clicking the
/// titlebar, or pressing Alt+Space. Gives PlayerForm a second way to reach
/// the tray icon's most useful actions without needing to find and
/// right-click the tray icon itself.
/// </summary>
internal static class SystemMenuHelper
{
    private const uint MF_STRING = 0x00000000;
    private const uint MF_SEPARATOR = 0x00000800;
    private const uint MF_BYCOMMAND = 0x00000000;
    private const uint MF_CHECKED = 0x00000008;
    private const uint MF_UNCHECKED = 0x00000000;

    [DllImport("user32.dll")]
    private static extern IntPtr GetSystemMenu(IntPtr hWnd, [MarshalAs(UnmanagedType.Bool)] bool bRevert);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenu(IntPtr hMenu, uint uFlags, UIntPtr uIDNewItem, string? lpNewItem);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool ModifyMenu(IntPtr hMenu, uint uPosition, uint uFlags, UIntPtr uIDNewItem, string lpNewItem);

    [DllImport("user32.dll")]
    private static extern uint CheckMenuItem(IntPtr hMenu, uint uIDCheckItem, uint uCheck);

    public static void AddSeparator(IntPtr hWnd) =>
        AppendMenu(GetSystemMenu(hWnd, false), MF_SEPARATOR, UIntPtr.Zero, null);

    public static void AddItem(IntPtr hWnd, int commandId, string text) =>
        AppendMenu(GetSystemMenu(hWnd, false), MF_STRING, (UIntPtr)commandId, text);

    public static void SetText(IntPtr hWnd, int commandId, string text) =>
        ModifyMenu(GetSystemMenu(hWnd, false), (uint)commandId, MF_BYCOMMAND | MF_STRING, (UIntPtr)commandId, text);

    public static void SetChecked(IntPtr hWnd, int commandId, bool isChecked) =>
        CheckMenuItem(GetSystemMenu(hWnd, false), (uint)commandId, MF_BYCOMMAND | (isChecked ? MF_CHECKED : MF_UNCHECKED));
}
