namespace SomaMetalTray;

// This app's own registered Last.fm/Discord/fanart.tv application identifiers
// - these identify "SomaMetalTray" itself to each service (and, for
// fanart.tv, just grant read-only public metadata lookup); they are not
// secrets that grant access to any user's account. Each user still
// authenticates their own Last.fm account separately via the existing
// Connect flow (LastFmScrobbler's auth.getToken/auth.getSession dance),
// which is unaffected by these being baked in here.
public static class AppCredentials
{
    // Same Last.fm developer app (and key/secret) as the sibling DeathFmTray
    // project - reused deliberately, not a mistake.
    public const string LastFmApiKey = "53a48adcaf85d2da5e316b0cd4b2d53c";
    public const string LastFmApiSecret = "07f6d35faa283f3da90ba7ce79c81528";

    // A separate Discord application from DeathFmTray's, registered for this
    // app's own Rich Presence branding.
    public const string DiscordClientId = "1552700589258051764";
    public const string DiscordDefaultImageKey = "logo";

    public const string FanArtTvApiKey = "59bdff2eec5ce0747c1a0daa6ef89ce0";
}
