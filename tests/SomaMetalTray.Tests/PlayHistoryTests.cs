using SomaMetalTray;
using Xunit;

namespace SomaMetalTray.Tests;

public class PlayHistoryTests
{
    private sealed class MemoryStorage : ITextStorage
    {
        public string? Text;
        public string? Read() => Text;
        public void Write(string text) => Text = text;
    }

    private static HistoryItem Item(string artist, string title, string station = "dfm", int minute = 0) =>
        new(new DateTimeOffset(2026, 10, 2, 12, minute, 0, TimeSpan.Zero), station, artist, title);

    [Fact]
    public void Newest_first()
    {
        var history = new PlayHistory(new MemoryStorage());
        history.Add(Item("A", "one"));
        history.Add(Item("B", "two"));
        Assert.Equal(new[] { "two", "one" }, history.Items.Select(i => i.Title));
    }

    [Fact]
    public void Repeat_of_the_newest_track_is_ignored_even_if_spelled_differently()
    {
        var history = new PlayHistory(new MemoryStorage());
        Assert.True(history.Add(Item("Sepultura", "Roots Bloody Roots")));
        Assert.False(history.Add(Item("SEPULTURA", "roots, bloody roots!", minute: 5)));
        Assert.Single(history.Items);
    }

    [Fact]
    public void Same_track_on_another_station_or_after_another_song_is_kept()
    {
        var history = new PlayHistory(new MemoryStorage());
        history.Add(Item("A", "one", "dfm"));
        history.Add(Item("A", "one", "metal"));
        history.Add(Item("B", "two"));
        history.Add(Item("A", "one"));
        Assert.Equal(4, history.Items.Count);
    }

    [Theory]
    [InlineData("", "Title")]
    [InlineData("Artist", " ")]
    public void Blanks_are_not_recorded(string artist, string title)
    {
        var history = new PlayHistory(new MemoryStorage());
        Assert.False(history.Add(Item(artist, title)));
        Assert.Empty(history.Items);
    }

    [Fact]
    public void Capped_at_max_items_dropping_the_oldest()
    {
        var history = new PlayHistory(new MemoryStorage());
        for (int i = 0; i < PlayHistory.MaxItems + 25; i++)
            history.Add(Item("Artist", $"song {i}"));

        Assert.Equal(PlayHistory.MaxItems, history.Items.Count);
        Assert.Equal($"song {PlayHistory.MaxItems + 24}", history.Items[0].Title);
        Assert.DoesNotContain(history.Items, i => i.Title == "song 0");
    }

    [Fact]
    public void Survives_a_restart_and_clear_persists()
    {
        var storage = new MemoryStorage();
        var first = new PlayHistory(storage);
        first.Add(Item("A", "one"));
        first.Add(Item("B", "two"));

        var second = new PlayHistory(storage);
        Assert.Equal(new[] { "two", "one" }, second.Items.Select(i => i.Title));

        second.Clear();
        Assert.Empty(new PlayHistory(storage).Items);
    }

    [Fact]
    public void Corrupt_storage_starts_empty() =>
        Assert.Empty(new PlayHistory(new MemoryStorage { Text = "[{oops" }).Items);

    [Fact]
    public void Changed_fires_on_add_but_not_on_ignored_duplicates()
    {
        var history = new PlayHistory(new MemoryStorage());
        int fired = 0;
        history.Changed += () => fired++;
        history.Add(Item("A", "one"));
        history.Add(Item("A", "one"));
        Assert.Equal(1, fired);
    }
}

public class UpdateCheckerTests
{
    [Theory]
    [InlineData("v1.2.3", 1, 2, 3)]
    [InlineData("1.2", 1, 2, 0)]
    [InlineData("V0.4.0-beta.1", 0, 4, 0)]
    [InlineData("v2.0.0+build5", 2, 0, 0)]
    public void Parses_release_tags(string tag, int major, int minor, int build)
    {
        Assert.True(UpdateChecker.TryParseTag(tag, out Version v));
        Assert.Equal(major, v.Major);
        Assert.Equal(minor, v.Minor);
        Assert.Equal(Math.Max(0, v.Build), build);
    }

    [Theory]
    [InlineData("latest")]
    [InlineData("")]
    [InlineData("v")]
    public void Rejects_non_version_tags(string tag) =>
        Assert.False(UpdateChecker.TryParseTag(tag, out _));
}
