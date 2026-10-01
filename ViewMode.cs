namespace SomaMetalTray;

/// <summary>The player window's layouts: the full player, a smaller one without the reflection, and a one-line strip.</summary>
public enum ViewMode
{
    Full,
    Compact,
    Strip,
}

/// <summary>
/// All the pixel positions of one <see cref="ViewMode"/>. The title bar (32px) sits above everything; when the history panel is
/// open above the player, <c>extraTop</c> pushes the whole player down by the panel's height.
/// </summary>
internal sealed record ViewMetrics(
    int Width,
    int Height,
    int ContentTop,
    int ArtSize,
    int ArtLeft,
    int ArtTop,
    int InfoLeft,
    int RightMargin,
    int TitleTop,
    int ArtistTop,
    int AlbumTop,
    int ProgressTop,
    int ProgressHeight,
    int TimeLabelsTop,
    int StatusTop,
    int ControlsTop,
    int PlayButtonSize,
    int VolumeLeft,
    int VolumeOffsetY,
    int DropdownWidth,
    float TitleFont,
    float ArtistFont,
    float AlbumFont,
    bool ShowLivePill,
    bool ShowText,
    bool ShowReflection,
    bool ShowSpeaker)
{
    public int InfoWidth => Width - InfoLeft - RightMargin;

    public int ReflectionTop => ArtTop + ArtSize + 2;

    public int ReflectionHeight => Height - ReflectionTop; // runs flush to the bottom edge of the player

    public static ViewMetrics For(ViewMode mode, int titleBarHeight, int extraTop = 0)
    {
        int ct = titleBarHeight + extraTop; // where the player's own content starts
        return mode switch
        {
            ViewMode.Compact => new ViewMetrics(
                Width: 700, Height: ct + 208, ContentTop: ct, ArtSize: 170, ArtLeft: 20, ArtTop: ct + 12,
                InfoLeft: 214, RightMargin: 20, TitleTop: ct + 10, ArtistTop: ct + 52, AlbumTop: ct + 78,
                ProgressTop: ct + 112, ProgressHeight: 8, TimeLabelsTop: ct + 124, StatusTop: ct + 140,
                ControlsTop: ct + 156, PlayButtonSize: 44, VolumeLeft: 214 + 44 + 40, VolumeOffsetY: 12, DropdownWidth: 150,
                TitleFont: 16f, ArtistFont: 11f, AlbumFont: 9f,
                ShowLivePill: false, ShowText: true, ShowReflection: false, ShowSpeaker: true),

            ViewMode.Strip => new ViewMetrics(
                Width: 560, Height: ct + 66, ContentTop: ct, ArtSize: 56, ArtLeft: 12, ArtTop: ct + 5,
                InfoLeft: 80, RightMargin: 12, TitleTop: 0, ArtistTop: 0, AlbumTop: 0,
                ProgressTop: ct + 66 - 9, ProgressHeight: 4, TimeLabelsTop: 0, StatusTop: 0,
                ControlsTop: ct + 8, PlayButtonSize: 40, VolumeLeft: 80 + 40 + 8 + 40 + 16, VolumeOffsetY: 10, DropdownWidth: 124,
                TitleFont: 12f, ArtistFont: 10f, AlbumFont: 9f,
                ShowLivePill: false, ShowText: false, ShowReflection: false, ShowSpeaker: false),

            _ => new ViewMetrics(
                Width: 900, Height: ct + 380, ContentTop: ct, ArtSize: 300, ArtLeft: 30, ArtTop: ct + 26,
                InfoLeft: 360, RightMargin: 30, TitleTop: ct + 26 + 30, ArtistTop: ct + 26 + 30 + 60, AlbumTop: ct + 26 + 30 + 60 + 28,
                ProgressTop: ct + 26 + 30 + 60 + 28 + 30, ProgressHeight: 10, TimeLabelsTop: ct + 26 + 30 + 60 + 28 + 30 + 14,
                StatusTop: ct + 26 + 30 + 60 + 28 + 30 + 14 + 18, ControlsTop: ct + 26 + 30 + 60 + 28 + 30 + 14 + 18 + 22,
                PlayButtonSize: 80, VolumeLeft: 360 + 80 + 40, VolumeOffsetY: 30, DropdownWidth: 168,
                TitleFont: 20f, ArtistFont: 13f, AlbumFont: 10f,
                ShowLivePill: true, ShowText: true, ShowReflection: true, ShowSpeaker: true),
        };
    }
}
