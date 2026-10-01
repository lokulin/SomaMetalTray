using SomaMetalTray;
using Xunit;

namespace SomaMetalTray.Tests;

public class WishlistEntryTests
{
    [Theory]
    [InlineData("Mötley Crüe", "Girls, Girls, Girls", "motley crue|girls girls girls")]
    [InlineData("Simon & Garfunkel", "The Boxer", "simon and garfunkel|the boxer")]
    [InlineData("  AC/DC ", "T.N.T.", "ac dc|t n t")]
    public void Key_folds_case_accents_and_punctuation(string artist, string title, string expected) =>
        Assert.Equal(expected, new WishlistEntry(artist, title).Key);

    [Fact]
    public void Same_track_spelled_two_ways_has_one_key() =>
        Assert.Equal(new WishlistEntry("Sepultura", "Roots Bloody Roots").Key, new WishlistEntry("SEPULTURA", "roots, bloody roots!").Key);

    [Theory]
    [InlineData("Death.FM", "Death.FM", false)]
    [InlineData("", "Title", false)]
    [InlineData("Artist", "  ", false)]
    [InlineData("Dying Fetus", "Raping The System", true)]
    public void IsRealTrack_rejects_placeholder_and_blanks(string artist, string title, bool expected) =>
        Assert.Equal(expected, new WishlistEntry(artist, title).IsRealTrack);
}

public class WishlistRepositoryTests
{
    private sealed class MemoryStorage : ITextStorage
    {
        public string? Text;
        public string? Read() => Text;
        public void Write(string text) => Text = text;
    }

    private sealed class FakeSender(Func<PendingOp, bool> outcome) : IWishlistSender
    {
        public readonly List<PendingOp> Sent = new();
        public Task<bool> SendAsync(PendingOp op)
        {
            Sent.Add(op);
            return Task.FromResult(outcome(op));
        }
    }

    private static readonly WishlistEntry A = new("Artist A", "Song A");
    private static readonly WishlistEntry B = new("Artist B", "Song B");

    [Fact]
    public async Task Toggle_likes_then_unlikes_and_sends_both()
    {
        var sender = new FakeSender(_ => true);
        var repo = new WishlistRepository(new MemoryStorage(), sender);

        Assert.True(await repo.ToggleAsync(A));
        Assert.True(repo.IsLiked(A));
        Assert.False(await repo.ToggleAsync(A));
        Assert.False(repo.IsLiked(A));

        Assert.Equal(new[] { true, false }, sender.Sent.Select(o => o.Liked));
        Assert.Equal(0, repo.PendingCount);
    }

    [Fact]
    public async Task Failed_send_stays_queued_and_flush_retries()
    {
        bool online = false;
        var sender = new FakeSender(_ => online);
        var repo = new WishlistRepository(new MemoryStorage(), sender);

        await repo.ToggleAsync(A);
        Assert.True(repo.IsLiked(A));
        Assert.Equal(1, repo.PendingCount);

        online = true;
        await repo.FlushAsync();
        Assert.Equal(0, repo.PendingCount);
    }

    [Fact]
    public async Task Like_then_unlike_offline_sends_a_single_remove()
    {
        bool online = false;
        var sender = new FakeSender(_ => online);
        var repo = new WishlistRepository(new MemoryStorage(), sender);

        await repo.ToggleAsync(A);
        await repo.ToggleAsync(A);
        Assert.Equal(1, repo.PendingCount);

        sender.Sent.Clear();
        online = true;
        await repo.FlushAsync();
        Assert.Single(sender.Sent);
        Assert.False(sender.Sent[0].Liked);
    }

    [Fact]
    public async Task Flush_stops_at_first_failure_and_keeps_order()
    {
        bool online = false;
        var sender = new FakeSender(_ => online);
        var repo = new WishlistRepository(new MemoryStorage(), sender);
        await repo.ToggleAsync(A);
        await repo.ToggleAsync(B);

        sender.Sent.Clear();
        await repo.FlushAsync();
        Assert.Single(sender.Sent); // stopped after the first failure
        Assert.Equal(A.Key, sender.Sent[0].Entry.Key);
    }

    [Fact]
    public async Task State_survives_a_restart()
    {
        var storage = new MemoryStorage();
        var offline = new FakeSender(_ => false);
        var first = new WishlistRepository(storage, offline);
        await first.ToggleAsync(A);
        await first.ToggleAsync(B);

        var second = new WishlistRepository(storage, new FakeSender(_ => true));
        Assert.True(second.IsLiked(A));
        Assert.True(second.IsLiked(B));
        Assert.Equal(2, second.PendingCount);
        Assert.Equal(new[] { B.Key, A.Key }, second.LikedEntries().Select(e => e.Key)); // most recent first
    }

    [Fact]
    public async Task Without_a_sender_likes_are_local_only()
    {
        var repo = new WishlistRepository(new MemoryStorage(), sender: null);
        Assert.True(await repo.ToggleAsync(A));
        Assert.True(repo.IsLiked(A));
        Assert.Equal(0, repo.PendingCount);
    }

    [Fact]
    public void Corrupt_storage_starts_empty()
    {
        var repo = new WishlistRepository(new MemoryStorage { Text = "{not json" }, sender: null);
        Assert.False(repo.IsLiked(A));
    }
}
