using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

// Inline MSBuild task (compiled by RoslynCodeTaskFactory - see SomaMetalTray.csproj, NOT part of the app).
// Reads local.properties (KEY=VALUE lines, # comments) and writes PrivateConfig.g.cs with the
// wishlist-server credentials as constants; missing file or keys => empty strings => feature off.
public class GeneratePrivateConfig : Task
{
    [Required]
    public string PropertiesFile { get; set; }

    [Required]
    public string OutputFile { get; set; }

    public override bool Execute()
    {
        var values = new Dictionary<string, string>();
        if (File.Exists(PropertiesFile))
        {
            foreach (string raw in File.ReadAllLines(PropertiesFile))
            {
                string line = raw.Trim();
                int eq = line.IndexOf('=');
                if (line.Length == 0 || line[0] == '#' || eq <= 0)
                    continue;
                values[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
            }
        }

        string code =
            "namespace SomaMetalTray;\n" +
            "internal static class PrivateConfig\n{\n" +
            "    public const string WishlistUrl = " + Literal(values, "WISHLIST_URL") + ";\n" +
            "    public const string WishlistClientId = " + Literal(values, "WISHLIST_CF_ACCESS_CLIENT_ID") + ";\n" +
            "    public const string WishlistClientSecret = " + Literal(values, "WISHLIST_CF_ACCESS_CLIENT_SECRET") + ";\n" +
            "    public const string DeathFmProxyUrl = " + Literal(values, "DEATHFM_STREAM_PROXY_URL") + ";\n" +
            "    public static bool WishlistEnabled => WishlistUrl.Length > 0 && WishlistClientId.Length > 0 && WishlistClientSecret.Length > 0;\n" +
            "}\n";

        Directory.CreateDirectory(Path.GetDirectoryName(OutputFile));
        // Only touch the file when it changes, so an unchanged config doesn't force a recompile.
        if (!File.Exists(OutputFile) || File.ReadAllText(OutputFile) != code)
            File.WriteAllText(OutputFile, code);
        return true;
    }

    private static string Literal(Dictionary<string, string> values, string key)
    {
        string value;
        if (!values.TryGetValue(key, out value))
            value = "";
        return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }
}
