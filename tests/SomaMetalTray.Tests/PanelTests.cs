using System.Drawing;
using SomaMetalTray;
using Xunit;

namespace SomaMetalTray.Tests;

public class PanelPlacementTests
{
    private static readonly Rectangle Screen = new(0, 0, 1920, 1040); // work area (taskbar excluded)

    [Fact]
    public void Opens_below_when_there_is_room()
    {
        (bool above, int top) = PlayerForm.PlanPanel(top: 200, height: 412, panelHeight: 340, Screen);
        Assert.False(above);
        Assert.Equal(200, top);
    }

    [Fact]
    public void Opens_above_when_the_window_is_low_on_the_screen()
    {
        (bool above, int top) = PlayerForm.PlanPanel(top: 700, height: 300, panelHeight: 340, Screen);
        Assert.True(above);
        Assert.Equal(360, top); // grows upward by the panel's height, so the player's own position is unchanged
    }

    [Fact]
    public void Slides_up_to_fit_when_there_is_no_room_on_either_side()
    {
        (bool above, int top) = PlayerForm.PlanPanel(top: 150, height: 600, panelHeight: 500, Screen);
        Assert.False(above);
        Assert.Equal(1040 - 600 - 500 < 0 ? 0 : 1040 - 600 - 500, top);
    }

    [Fact]
    public void Exact_fit_below_stays_below()
    {
        (bool above, _) = PlayerForm.PlanPanel(top: 300, height: 400, panelHeight: 340, Screen);
        Assert.False(above);
    }
}

public class DeathFmQueueTests
{
    // Trimmed copy of a real get_db_info response (note the JSON-escaped slashes and the placeholder cover on the station ID row).
    private const string Sample = """
        {"queue_html":"<tr>\n <td align='center' width='30'>&#8595; 1<br><span class='player-row-duration'>0:02<\/span><\/td>\n <td width='45'><img src='\/images\/albuminfo\/icon_album.png' width='40'><\/td>\n <td style='line-height:1.4;'><strong>Jeff Straub<\/strong> - <span>ID10<\/span><br><span class='dim-text' style='font-size:0.95em;'>Death.FM<\/span><\/td>\n <\/tr><tr>\n <td align='center' width='30'>&#8595; 2<br><span class='player-row-duration'>5:31<\/span><\/td>\n <td width='45'><img src='\/images\/cover\/040\/B001VG617I.jpg' width='40'><\/td>\n <td style='line-height:1.4;'><strong>Debauchery [DEU]<\/strong> - <span>There Is Only War<\/span><br><span class='dim-text' style='font-size:0.95em;'>Rockers &amp; War<\/span><\/td>\n <\/tr>","played_html":"<tr><td>&#8593; 1<br><span class='player-row-duration'>4:36<\/span><\/td><td><img src='\/images\/cover\/040\/B000050G44.jpg'><\/td><td><strong>Misanthrope<\/strong> - <span>Diabolical Lamentations<\/span><br><span class='dim-text'>Immortal Misanthrope<\/span><\/td><\/tr>"}
        """;

    [Fact]
    public void Parses_queue_rows()
    {
        IReadOnlyList<UpcomingItem> items = DeathFmQueue.Parse(Sample, "queue_html");

        Assert.Equal(2, items.Count);
        Assert.Equal(new UpcomingItem(1, "Jeff Straub", "ID10", "Death.FM", "0:02", null), items[0]);
        Assert.Equal(new UpcomingItem(2, "Debauchery [DEU]", "There Is Only War", "Rockers & War", "5:31", "https://death.fm/images/cover/040/B001VG617I.jpg"), items[1]);
    }

    [Fact]
    public void Parses_the_played_list_with_the_same_shape()
    {
        IReadOnlyList<UpcomingItem> items = DeathFmQueue.Parse(Sample, "played_html");
        Assert.Single(items);
        Assert.Equal("Misanthrope", items[0].Artist);
        Assert.Equal("Diabolical Lamentations", items[0].Title);
        Assert.Equal(1, items[0].Rank);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("""{"queue_html":""}""")]
    public void Garbage_gives_an_empty_list(string json) => Assert.Empty(DeathFmQueue.Parse(json, "queue_html"));

    [Theory]
    [InlineData("https://death.fm/modules.php?name=Album&asin=B00004L8BJ", "B00004L8BJ")]
    [InlineData("https://death.fm/x?asin=B000NVLEI8&other=1", "B000NVLEI8")]
    [InlineData("https://death.fm/nothing", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Extracts_the_asin(string? link, string? expected) => Assert.Equal(expected, DeathFmQueue.AsinFromSiteLink(link));
}

public class StationStreamTests
{
    [Fact]
    public void With_a_proxy_configured_it_is_tried_first_and_the_direct_stream_is_the_fallback() =>
        Assert.Equal(new[] { "https://proxy.example/live", DeathFmStation.DirectUrl }, DeathFmStation.StreamUrls(" https://proxy.example/live "));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Without_a_proxy_it_plays_directly(string? proxy) =>
        Assert.Equal(new[] { DeathFmStation.DirectUrl }, DeathFmStation.StreamUrls(proxy));

    [Fact]
    public void Casting_always_uses_the_direct_stream_url() =>
        // The receiver reaches the proxy itself; the sender should hand it the station's real URL.
        Assert.Equal(DeathFmStation.DirectUrl, Stations.DeathFm.CastContentUrl);
}
