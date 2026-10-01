using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace SomaMetalTray;

/// <summary>One track in a station's upcoming queue.</summary>
public sealed record UpcomingItem(int Rank, string Artist, string Title, string Album, string Duration, string? ThumbUrl);

/// <summary>
/// Death.FM's player page keeps its Queue/Played tabs behind <c>player.php?ajax_action=get_db_info&amp;station=&lt;id&gt;&amp;asin=&lt;now playing&gt;</c>,
/// returning JSON whose <c>queue_html</c> / <c>played_html</c> are bare sequences of table rows:
/// <c>&lt;tr&gt;&lt;td&gt;&amp;#8595; 1&lt;br&gt;&lt;span class='player-row-duration'&gt;2:53&lt;/span&gt;&lt;/td&gt;&lt;td&gt;&lt;img src='/images/cover/040/X.jpg'&gt;&lt;/td&gt;
/// &lt;td&gt;&lt;strong&gt;Artist&lt;/strong&gt; - &lt;span&gt;Title&lt;/span&gt;&lt;br&gt;&lt;span class='dim-text'&gt;Album&lt;/span&gt;&lt;/td&gt;&lt;/tr&gt;</c>.
/// </summary>
internal static class DeathFmQueue
{
    private const string SiteRoot = "https://death.fm";
    private const string PlaceholderThumb = "icon_album.png"; // death.fm's "no cover" image

    private static readonly Regex Row = new(@"<tr\b.*?</tr>", RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase);
    private static readonly Regex Rank = new(@"&#8[59]\d\d;\s*(\d+)", RegexOptions.Compiled);
    private static readonly Regex Duration = new(@"player-row-duration'?""?>\s*([0-9:]+)\s*<", RegexOptions.Compiled);
    private static readonly Regex Thumb = new(@"<img\b[^>]*?\bsrc=['""]([^'""]+)['""]", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex Artist = new(@"<strong>(.*?)</strong>", RegexOptions.Compiled | RegexOptions.Singleline);
    private static readonly Regex Title = new(@"</strong>\s*-\s*<span>(.*?)</span>", RegexOptions.Compiled | RegexOptions.Singleline);
    private static readonly Regex Album = new(@"class=['""]dim-text['""][^>]*>(.*?)</span>", RegexOptions.Compiled | RegexOptions.Singleline);

    /// <summary>The upcoming queue for the track with this ASIN (empty on any failure).</summary>
    public static async Task<IReadOnlyList<UpcomingItem>> FetchQueueAsync(HttpClient http, string stationId, string asin)
    {
        string json = await http.GetStringAsync($"{SiteRoot}/player.php?ajax_action=get_db_info&station={Uri.EscapeDataString(stationId)}&asin={Uri.EscapeDataString(asin)}");
        return Parse(json, "queue_html");
    }

    /// <summary>Parses one of the HTML-fragment fields (<c>queue_html</c> or <c>played_html</c>) of a get_db_info response.</summary>
    internal static IReadOnlyList<UpcomingItem> Parse(string json, string field)
    {
        var items = new List<UpcomingItem>();
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty(field, out JsonElement el) || el.ValueKind != JsonValueKind.String)
                return items;

            foreach (Match row in Row.Matches(el.GetString() ?? ""))
            {
                string html = row.Value;
                Match artist = Artist.Match(html);
                Match title = Title.Match(html);
                if (!artist.Success || !title.Success)
                    continue;

                string? thumb = Thumb.Match(html) is { Success: true } t ? t.Groups[1].Value : null;
                if (thumb is not null)
                {
                    thumb = thumb.Contains(PlaceholderThumb, StringComparison.OrdinalIgnoreCase) ? null
                        : thumb.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? thumb
                        : SiteRoot + (thumb.StartsWith('/') ? thumb : "/" + thumb);
                }

                items.Add(new UpcomingItem(
                    Rank.Match(html) is { Success: true } r && int.TryParse(r.Groups[1].Value, out int rank) ? rank : items.Count + 1,
                    Decode(artist.Groups[1].Value),
                    Decode(title.Groups[1].Value),
                    Album.Match(html) is { Success: true } a ? Decode(a.Groups[1].Value) : "",
                    Duration.Match(html) is { Success: true } d ? d.Groups[1].Value : "",
                    thumb));
            }
        }
        catch (JsonException)
        {
            // Not the JSON we expected (site hiccup) - an empty list, same as a failed fetch.
        }

        return items;
    }

    private static string Decode(string html) => WebUtility.HtmlDecode(Regex.Replace(html, "<.*?>", "")).Trim();

    /// <summary>The ASIN in a now-playing <c>SiteLink</c> such as <c>https://death.fm/modules.php?name=Album&amp;asin=B00004L8BJ</c>.</summary>
    internal static string? AsinFromSiteLink(string? siteLink)
    {
        if (string.IsNullOrEmpty(siteLink))
            return null;

        Match m = Regex.Match(siteLink, @"[?&]asin=([A-Za-z0-9]+)");
        return m.Success ? m.Groups[1].Value : null;
    }
}
