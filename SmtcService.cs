using System;
using System.Runtime.InteropServices;
using Windows.Foundation;
using Windows.Media;
using Windows.Storage.Streams;
using WinRT;

namespace SomaMetalTray;

/// <summary>
/// Drives Windows' System Media Transport Controls (the volume-flyout "Now
/// Playing" widget) directly from our own process/window, bound to our own
/// window handle via the classic Win32 interop entry point
/// (ISystemMediaTransportControlsInterop.GetForWindow) so the session
/// registers under this process' own identity/AUMID rather than anything
/// else's.
///
/// Ported from DeathFmTray, where this was needed to work around WebView2's
/// Chromium child process registering its own competing SMTC session.
/// SomaMetalTray has no WebView2 involved at all (playback goes straight
/// through Windows.Media.Playback.MediaPlayer - see AudioPlayerService), but
/// the managed SystemMediaTransportControls.GetForCurrentView() API still
/// only works for UWP apps with a CoreWindow, so this same GetForWindow
/// interop path is still required for a plain desktop app.
/// </summary>
public sealed class SmtcService : IDisposable
{
    private static readonly Guid SmtcInteropIid = new("DDB0472D-C911-4A1F-86D9-DC3D71A95F5A");

    private readonly SystemMediaTransportControls _smtc;
    private readonly SystemMediaTransportControlsDisplayUpdater _updater;
    private readonly TypedEventHandler<SystemMediaTransportControls, SystemMediaTransportControlsButtonPressedEventArgs> _buttonPressedHandler;

    public event Action<SystemMediaTransportControlsButton>? ButtonPressed;

    public SmtcService(IntPtr windowHandle)
    {
        _smtc = GetForWindow(windowHandle);
        _smtc.IsPlayEnabled = true;
        _smtc.IsPauseEnabled = true;
        _smtc.IsStopEnabled = true;
        _smtc.PlaybackStatus = MediaPlaybackStatus.Closed;

        _buttonPressedHandler = (_, e) => ButtonPressed?.Invoke(e.Button);
        _smtc.ButtonPressed += _buttonPressedHandler;

        _updater = _smtc.DisplayUpdater;
        _updater.Type = MediaPlaybackType.Music;
    }

    public void SetPlaybackStatus(MediaPlaybackStatus status)
    {
        _smtc.PlaybackStatus = status;
    }

    public void UpdateMetadata(string title, string artist, string album, string? artUrl)
    {
        _updater.MusicProperties.Title = title;
        _updater.MusicProperties.Artist = artist;
        _updater.MusicProperties.AlbumTitle = album;

        _updater.Thumbnail = !string.IsNullOrEmpty(artUrl) && Uri.TryCreate(artUrl, UriKind.Absolute, out Uri? artUri)
            ? RandomAccessStreamReference.CreateFromUri(artUri)
            : null;

        _updater.Update();
    }

    public void Dispose()
    {
        _smtc.ButtonPressed -= _buttonPressedHandler;
        _smtc.PlaybackStatus = MediaPlaybackStatus.Closed;
    }

    // CoreCLR doesn't support [MarshalAs(UnmanagedType.HString)] on a plain
    // P/Invoke string parameter ("Cannot marshal 'parameter #1': Invalid
    // managed/unmanaged type combination" - that attribute only works through
    // CsWinRT-generated projection stubs, not hand-written DllImports). Build
    // the HSTRING by hand instead.
    [DllImport("combase.dll", PreserveSig = true)]
    private static extern int WindowsCreateString(
        [MarshalAs(UnmanagedType.LPWStr)] string sourceString,
        int length,
        out IntPtr hstring);

    [DllImport("combase.dll", PreserveSig = true)]
    private static extern int WindowsDeleteString(IntPtr hstring);

    [DllImport("combase.dll", PreserveSig = true)]
    private static extern int RoGetActivationFactory(
        IntPtr activatableClassId,
        [In] ref Guid iid,
        out IntPtr factory);

    // RO_INIT_SINGLETHREADED (0) - matches the [STAThread] classic COM apartment
    // WinForms already set up via CoInitializeEx, but that alone doesn't
    // initialize the separate per-thread WinRT state that Ro* APIs need; calling
    // this once before any raw Ro* call is the documented requirement for using
    // WinRT from a plain desktop app. S_OK/S_FALSE (already initialized) are
    // both fine; anything else means the thread is in an incompatible mode.
    [DllImport("combase.dll", PreserveSig = true)]
    private static extern int RoInitialize(int initType);

    // ISystemMediaTransportControlsInterop::GetForWindow(HWND, REFIID, void**) -
    // the classic Win32 entry point desktop apps use to obtain SMTC for a
    // specific HWND (the managed SystemMediaTransportControls.GetForCurrentView()
    // only works for UWP apps with a CoreWindow). Despite being an "interop"
    // shim, the factory this comes from derives from IInspectable, not plain
    // IUnknown (confirmed by probing with Marshal.QueryInterface for
    // IID_IInspectable, which succeeded) - so GetForWindow sits at vtable slot
    // 6, after QueryInterface/AddRef/Release (0-2) and IInspectable's
    // GetIids/GetRuntimeClassName/GetTrustLevel (3-5). Called via a raw vtable
    // dispatch rather than a [ComImport] interface, because going through
    // Marshal.GetObjectForIUnknown + an interface cast crashed with an
    // AccessViolationException (verified by running it).
    private const int GetForWindowVtableSlot = 6;

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetForWindowDelegate(IntPtr self, IntPtr appWindow, ref Guid riid, out IntPtr ppv);

    private static SystemMediaTransportControls GetForWindow(IntPtr hwnd)
    {
        const string ClassId = "Windows.Media.SystemMediaTransportControls";

        int roInitHr = RoInitialize(0);
        if (roInitHr < 0) // negative = a real failure; S_OK/S_FALSE (already initialized) are both fine
        {
            Marshal.ThrowExceptionForHR(roInitHr);
        }

        Marshal.ThrowExceptionForHR(WindowsCreateString(ClassId, ClassId.Length, out IntPtr classIdHandle));
        try
        {
            Guid interopIid = SmtcInteropIid;
            Marshal.ThrowExceptionForHR(RoGetActivationFactory(classIdHandle, ref interopIid, out IntPtr factoryPtr));

            try
            {
                IntPtr vtable = Marshal.ReadIntPtr(factoryPtr);
                IntPtr getForWindowSlot = Marshal.ReadIntPtr(vtable, GetForWindowVtableSlot * IntPtr.Size);
                var getForWindow = Marshal.GetDelegateForFunctionPointer<GetForWindowDelegate>(getForWindowSlot);

                // typeof(SystemMediaTransportControls).GUID (whatever specific
                // ISystemMediaTransportControls[N] interface CsWinRT projects it as)
                // got E_NOINTERFACE back from the native call (verified by running
                // it) - it doesn't match what this Windows version's GetForWindow
                // actually hands back. Request the universally-supported
                // IID_IInspectable instead; MarshalInspectable<T> (unlike
                // MarshalInterface<T>) does its own interface resolution from an
                // IInspectable pointer, so it doesn't need the exact interface IID.
                Guid iidIInspectable = new("AF86E2E0-B12D-4C6A-9C5A-D7AA65101E90");
                Marshal.ThrowExceptionForHR(getForWindow(factoryPtr, hwnd, ref iidIInspectable, out IntPtr smtcPtr));

                try
                {
                    // SystemMediaTransportControls is a projected runtime *class*, not a
                    // plain interface, so wrapping the raw IInspectable pointer needs
                    // MarshalInspectable<T> - MarshalInterface<T> is for interface types
                    // only and throws InvalidCastException here (verified by running it).
                    return MarshalInspectable<SystemMediaTransportControls>.FromAbi(smtcPtr);
                }
                finally
                {
                    Marshal.Release(smtcPtr);
                }
            }
            finally
            {
                Marshal.Release(factoryPtr);
            }
        }
        finally
        {
            WindowsDeleteString(classIdHandle);
        }
    }
}
