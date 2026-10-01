using SomaMetalTray;
using Xunit;

namespace SomaMetalTray.Tests;

public sealed class LegacyMigrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "blastbeat-migration-" + Guid.NewGuid().ToString("N"));

    public LegacyMigrationTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string Write(string folder, string file, string text)
    {
        string dir = Path.Combine(_root, folder);
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, file);
        File.WriteAllText(path, text);
        return path;
    }

    private string NewFile(string file) => Path.Combine(_root, AppInfo.DataFolderName, file);

    [Fact]
    public void Copies_all_json_from_the_previous_app_name()
    {
        Write("SomaMetalTray", "settings.json", """{"Volume":0.3}""");
        Write("SomaMetalTray", "wishlist.json", "{}");
        Write("SomaMetalTray", "history.json", "[]");

        Assert.True(LegacyMigration.MigrateData(_root));
        Assert.Contains("0.3", File.ReadAllText(NewFile("settings.json")));
        Assert.True(File.Exists(NewFile("wishlist.json")));
        Assert.True(File.Exists(NewFile("history.json")));
        Assert.True(File.Exists(Path.Combine(_root, "SomaMetalTray", "settings.json")), "the old folder is left alone");
    }

    [Fact]
    public void Does_nothing_once_the_new_settings_exist()
    {
        Write("SomaMetalTray", "settings.json", """{"Volume":0.3}""");
        Write(AppInfo.DataFolderName, "settings.json", """{"Volume":0.9}""");

        Assert.False(LegacyMigration.MigrateData(_root));
        Assert.Contains("0.9", File.ReadAllText(NewFile("settings.json")));
    }

    [Fact]
    public void Imports_deathfmtray_settings_when_there_is_no_soma_data()
    {
        Write("DeathFmTray", "settings.json", """
            {"Volume":0.42,"LastFmSessionKey":"abc","LastFmUsername":"someone","StationUrl":"https://death.fm/player.php?station=dfm","WindowSizeLocked":true}
            """);

        Assert.True(LegacyMigration.MigrateData(_root));
        AppSettings? imported = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(NewFile("settings.json")));
        Assert.NotNull(imported);
        Assert.Equal(0.42, imported!.Volume);
        Assert.Equal("abc", imported.LastFmSessionKey);
        Assert.Equal("someone", imported.LastFmUsername);
        Assert.Equal("dfm", imported.StationId);
    }

    [Fact]
    public void Other_deathfm_network_stations_fall_back_to_the_default_station()
    {
        AppSettings? imported = LegacyMigration.ImportDeathFmTray("""{"StationUrl":"https://death.fm/player.php?station=80s"}""");
        Assert.NotNull(imported);
        Assert.Null(imported!.StationId);
    }

    [Fact]
    public void Nothing_to_migrate_is_fine() => Assert.False(LegacyMigration.MigrateData(_root));

    [Fact]
    public void Garbage_deathfmtray_settings_are_ignored()
    {
        Write("DeathFmTray", "settings.json", "{not json");
        Assert.False(LegacyMigration.MigrateData(_root));
        Assert.False(File.Exists(NewFile("settings.json")));
    }
}
