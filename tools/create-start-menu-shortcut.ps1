<#
    .SYNOPSIS
    Creates a Start Menu shortcut for SomaFM Metal Detector Player with its
    AppUserModelID (AUMID) property set, so Windows can show "SomaFM Metal
    Detector Player" (instead of "Unknown app") as the source in the volume
    mixer / media flyout, notifications, etc.

    .WHY
    Program.cs already calls SetCurrentProcessExplicitAppUserModelID at
    startup, which tells Windows "group this process under AUMID X" - but for
    an unpackaged .exe with no installer, Windows still needs a Start Menu
    shortcut carrying that same AUMID as a property before it has a friendly
    name to display for AUMID X. This script creates that shortcut, once.

    This only needs to be run ONCE per machine (per exe location). After that,
    Windows resolves the AUMID to this shortcut's name regardless of how the
    app is actually launched (double-click, "Start with Windows", etc), and
    you can delete this shortcut from the Start Menu without breaking that -
    Windows caches the resolution.

    .EXAMPLE
    .\create-start-menu-shortcut.ps1 -ExePath "C:\Tools\SomaMetalTray\SomaMetalTray.exe"
#>

param(
    [Parameter(Mandatory = $true)]
    [string]$ExePath,

    # Must match AppUserModelId in Program.cs - change both together if you rename the app.
    [string]$AppId = "TerraEclectic.SomaMetalTray.v1",
    [string]$AppName = "SomaFM Metal Detector Player"
)

if (-not (Test-Path $ExePath)) {
    Write-Error "Could not find exe at '$ExePath'"
    exit 1
}
$ExePath = (Resolve-Path $ExePath).Path

$shortcutDir = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs"
$shortcutPath = Join-Path $shortcutDir "$AppName.lnk"

# --- Step 1: create the .lnk itself (plain COM shortcut object handles this part) ---
$wshShell = New-Object -ComObject WScript.Shell
$shortcut = $wshShell.CreateShortcut($shortcutPath)
$shortcut.TargetPath = $ExePath
$shortcut.WorkingDirectory = Split-Path $ExePath
$shortcut.IconLocation = $ExePath
$shortcut.Save()

# --- Step 2: stamp the AppUserModelID property onto it ---
# WScript.Shell's Shortcut object can't set this - it needs IPropertyStore,
# which isn't exposed to plain PowerShell, so a tiny inline C# helper does it
# via the same COM object (IShellLink also implements IPersistFile and
# IPropertyStore under the hood).
Add-Type @"
using System;
using System.Runtime.InteropServices;

public static class AumidShortcut
{
    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink { }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("0000010b-0000-0000-C000-000000000046")]
    private interface IPersistFile
    {
        void GetClassID(out Guid pClassID);
        int IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, [MarshalAs(UnmanagedType.Bool)] bool fRemember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string ppszFileName);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey
    {
        public Guid fmtid;
        public int pid;
        public PropertyKey(Guid fmtid, int pid) { this.fmtid = fmtid; this.pid = pid; }
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort vt;
        [FieldOffset(8)] public IntPtr pointerValue;
    }

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant pvar);

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        int GetCount(out uint cProps);
        int GetAt(uint iProp, out PropertyKey pkey);
        int GetValue(ref PropertyKey key, out PropVariant pv);
        int SetValue(ref PropertyKey key, ref PropVariant pv);
        int Commit();
    }

    public static void SetAppUserModelId(string shortcutPath, string appId)
    {
        var link = (IPersistFile)new ShellLink();
        link.Load(shortcutPath, 2); // STGM_READWRITE - required for Commit() below to actually persist

        var store = (IPropertyStore)link;
        var key = new PropertyKey(new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), 5); // PKEY_AppUserModel_ID

        var pv = new PropVariant { vt = 31, pointerValue = Marshal.StringToCoTaskMemUni(appId) }; // 31 = VT_LPWSTR
        try
        {
            Marshal.ThrowExceptionForHR(store.SetValue(ref key, ref pv));
            Marshal.ThrowExceptionForHR(store.Commit());
        }
        finally
        {
            PropVariantClear(ref pv);
        }

        link.Save(shortcutPath, true);
    }
}
"@

try {
    [AumidShortcut]::SetAppUserModelId($shortcutPath, $AppId)
    Write-Host "Created '$shortcutPath' with AppUserModelID '$AppId'."
    Write-Host "If Windows doesn't pick up the new name immediately, sign out/in (or reboot)."
}
catch {
    Write-Error "Failed to set the AppUserModelID property: $($_.Exception.Message)"
    exit 1
}
