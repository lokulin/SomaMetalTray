using System;
using System.IO;
using System.Runtime.InteropServices;

namespace SomaMetalTray;

/// <summary>
/// Ensures a Start Menu shortcut exists that carries the process AppUserModelID
/// and an explicit relaunch display name. Windows uses that shortcut to resolve
/// a friendly name for the media flyout / volume mixer / toasts; without it an
/// unpackaged exe shows as "Unknown app". Completely best-effort - any failure
/// is swallowed so the app always starts.
/// </summary>
internal static class AumidShortcutHelper
{
    // Must stay in sync with Program.AppUserModelId.
    private const string AppUserModelId = "TerraEclectic.SomaMetalTray.v1";
    private const string DisplayName = "Metal Detector";
    private const string ShortcutName = "Metal Detector.lnk";

    private static readonly Guid PkeyAppUserModel = new("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3");
    private const int PidAppUserModelId = 5;                       // PKEY_AppUserModel_ID
    private const int PidRelaunchDisplayNameResource = 4;          // PKEY_AppUserModel_RelaunchDisplayNameResource

    public static void EnsureStartMenuShortcut()
    {
        try
        {
            string? exePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
                return;

            string programsDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                @"Microsoft\Windows\Start Menu\Programs");

            Directory.CreateDirectory(programsDir);
            string shortcutPath = Path.Combine(programsDir, ShortcutName);

            CreateBasicShortcut(shortcutPath, exePath);
            TryStampShortcutProperties(shortcutPath, AppUserModelId, DisplayName);
        }
        catch
        {
            // Never prevent the app from launching.
        }
    }

    private static void CreateBasicShortcut(string shortcutPath, string exePath)
    {
        Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType is null)
            return;

        object? shell = Activator.CreateInstance(shellType);
        if (shell is null)
            return;

        try
        {
            object? shortcut = shellType.InvokeMember(
                "CreateShortcut",
                System.Reflection.BindingFlags.InvokeMethod,
                null,
                shell,
                new object[] { shortcutPath });

            if (shortcut is null)
                return;

            Type shortcutType = shortcut.GetType();
            shortcutType.InvokeMember("TargetPath", System.Reflection.BindingFlags.SetProperty, null, shortcut, new object[] { exePath });
            shortcutType.InvokeMember("WorkingDirectory", System.Reflection.BindingFlags.SetProperty, null, shortcut, new object[] { Path.GetDirectoryName(exePath)! });
            shortcutType.InvokeMember("IconLocation", System.Reflection.BindingFlags.SetProperty, null, shortcut, new object[] { exePath });
            shortcutType.InvokeMember("Description", System.Reflection.BindingFlags.SetProperty, null, shortcut, new object[] { DisplayName });
            shortcutType.InvokeMember("Save", System.Reflection.BindingFlags.InvokeMethod, null, shortcut, null);

            Marshal.FinalReleaseComObject(shortcut);
        }
        finally
        {
            Marshal.FinalReleaseComObject(shell);
        }
    }

    private static void TryStampShortcutProperties(string shortcutPath, string appId, string displayName)
    {
        try
        {
            var link = (IShellLinkW)new CShellLink();
            var file = (IPersistFile)link;
            file.Load(shortcutPath, 2); // STGM_READWRITE

            var store = (IPropertyStore)link;

            // 1. AppUserModelID — groups the process with this shortcut.
            SetStringProperty(store, new PropertyKey(PkeyAppUserModel, PidAppUserModelId), appId);

            // 2. Explicit display name Windows can show even when AUMID lookup is fuzzy.
            SetStringProperty(store, new PropertyKey(PkeyAppUserModel, PidRelaunchDisplayNameResource), displayName);

            store.Commit();
            file.Save(shortcutPath, true);
        }
        catch
        {
            // Property stamp failed — the plain shortcut is still better than nothing.
        }
    }

    private static void SetStringProperty(IPropertyStore store, PropertyKey key, string value)
    {
        IntPtr strPtr = Marshal.StringToCoTaskMemUni(value);
        try
        {
            var pv = new PropVariant { vt = 31, pointerValue = strPtr }; // VT_LPWSTR
            store.SetValue(ref key, ref pv);
        }
        finally
        {
            Marshal.FreeCoTaskMem(strPtr);
        }
    }

    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    private class CShellLink { }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszFile, int cch, IntPtr pfd, int fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszName, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszDir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszArgs, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszIconPath, int cch, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, int dwReserved);
        void Resolve(IntPtr hwnd, int fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("0000010b-0000-0000-C000-000000000046")]
    private interface IPersistFile
    {
        void GetClassID(out Guid pClassID);
        [PreserveSig] int IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, [MarshalAs(UnmanagedType.Bool)] bool fRemember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string ppszFileName);
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct PropertyKey
    {
        public Guid fmtid;
        public int pid;
        public PropertyKey(Guid fmtid, int pid) { this.fmtid = fmtid; this.pid = pid; }
    }

    [StructLayout(LayoutKind.Explicit, Size = 16)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort vt;
        [FieldOffset(8)] public IntPtr pointerValue;
    }

    [ComImport]
    [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint cProps);
        [PreserveSig] int GetAt(uint iProp, out PropertyKey pkey);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant pv);
        [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant pv);
        [PreserveSig] int Commit();
    }
}
