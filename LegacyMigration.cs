using System;
using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace SomaMetalTray;

/// <summary>
/// One-time carry-over from the apps Blastbeat Player replaces, run on every start but a no-op once done:
/// - settings/likes/history from %AppData%\SomaMetalTray (the previous name of this app), else
/// - Last.fm login, volume, window position and station from DeathFmTray's settings;
/// - the "Start with Windows" entry, moved to the new name (and the new exe path).
/// Old folders are left in place (the old apps may still be installed). Never throws.
/// </summary>
internal static class LegacyMigration
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string LegacyRunValueName = "SomaMetalTray";

    public static void Run()
    {
        try
        {
            string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (MigrateData(roaming))
                Logger.Log("LegacyMigration - carried settings over from a previous app");
        }
        catch (Exception ex)
        {
            Logger.Log($"LegacyMigration - data migration failed: {ex.Message}");
        }

        try
        {
            MigrateRunKey();
        }
        catch (Exception ex)
        {
            Logger.Log($"LegacyMigration - Run key migration failed: {ex.Message}");
        }
    }

    /// <summary>Returns true if anything was carried over. Does nothing if the new settings file already exists.</summary>
    internal static bool MigrateData(string roamingRoot)
    {
        string target = Path.Combine(roamingRoot, AppInfo.DataFolderName);
        string targetSettings = Path.Combine(target, "settings.json");
        if (File.Exists(targetSettings))
            return false;

        string somaDir = Path.Combine(roamingRoot, AppInfo.LegacyDataFolderName);
        if (File.Exists(Path.Combine(somaDir, "settings.json")))
        {
            Directory.CreateDirectory(target);
            foreach (string file in Directory.GetFiles(somaDir, "*.json"))
                File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: false);
            return true;
        }

        string deathFmSettings = Path.Combine(roamingRoot, "DeathFmTray", "settings.json");
        if (File.Exists(deathFmSettings))
        {
            AppSettings? imported = ImportDeathFmTray(File.ReadAllText(deathFmSettings));
            if (imported is not null)
            {
                Directory.CreateDirectory(target);
                File.WriteAllText(targetSettings, JsonSerializer.Serialize(imported, new JsonSerializerOptions { WriteIndented = true }));
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// DeathFmTray's settings.json uses the same property names for the settings that still exist here, so a plain
    /// deserialize picks them up (unknown ones are ignored). Its station is a URL; map it to our station id.
    /// </summary>
    internal static AppSettings? ImportDeathFmTray(string json)
    {
        try
        {
            AppSettings? settings = JsonSerializer.Deserialize<AppSettings>(json);
            if (settings is null)
                return null;

            using JsonDocument doc = JsonDocument.Parse(json);
            string? stationUrl = doc.RootElement.TryGetProperty("StationUrl", out JsonElement el) ? el.GetString() : null;
            settings.StationId = stationUrl is not null && stationUrl.Contains("station=dfm", StringComparison.OrdinalIgnoreCase)
                ? Stations.DeathFm.Id
                : null; // some other death.fm network station - not offered here, fall back to the default

            return settings;
        }
        catch
        {
            return null;
        }
    }

    private static void MigrateRunKey()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        if (key?.GetValue(LegacyRunValueName) is null)
            return;

        // Re-point autostart at *this* exe under the new name - but never from a dev build's bin\ folder.
        string? exe = Environment.ProcessPath;
        bool devBuild = exe is null
            || exe.Contains(@"\bin\Debug\", StringComparison.OrdinalIgnoreCase)
            || exe.Contains(@"\bin\Release\", StringComparison.OrdinalIgnoreCase);
        if (devBuild)
            return;

        StartupManager.SetEnabled(true);
        key.DeleteValue(LegacyRunValueName);
        Logger.Log("LegacyMigration - moved the Start with Windows entry to the new name");
    }
}
