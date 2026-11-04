using System.IO;
using Steamy.Services;

namespace Steamy.UiTests;

public sealed class DiscordMessageArchiveTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "steamy-archive-" + Guid.NewGuid().ToString("N"));
    private readonly DiscordMessageArchive _archive;

    public DiscordMessageArchiveTests()
    {
        Directory.CreateDirectory(_directory);
        _archive = new DiscordMessageArchive(Path.Combine(_directory, "messages.db"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    private static DiscordChatMessage Message(ulong id, ulong peer, string content, int minute, ulong lobby = 0) =>
        new(id, peer, peer, lobby, 900, content, new DateTimeOffset(2026, 10, 31, 12, minute, 0, TimeSpan.Zero), false, false, false)
        { RawContent = content + " raw" };

    [Fact]
    public async Task DirectMessagesSurviveARestartInChronologicalOrder()
    {
        await _archive.SaveAsync([Message(12, 42, "second", 2), Message(10, 42, "first", 1), Message(11, 42, "party", 1, lobby: 77), Message(7, 99, "other peer", 3)]);

        var reopened = new DiscordMessageArchive(Path.Combine(_directory, "messages.db"));
        var history = await reopened.LoadAsync(42);
        Assert.Equal(["first", "second"], history.Select(message => message.Content));
        Assert.Equal([10UL, 12UL], history.Select(message => message.Id));
        Assert.Equal(12UL, await reopened.LatestMessageIdAsync(42));
        Assert.Single(await reopened.LoadAsync(99));
        Assert.Empty(await reopened.LoadAsync(77));
    }

    [Fact]
    public async Task EditsReplaceTheStoredCopyAndDeletesRemoveIt()
    {
        await _archive.SaveAsync([Message(20, 42, "before edit", 4)]);
        await _archive.SaveAsync([Message(20, 42, "after edit", 4) with { IsEdited = true }]);
        var edited = Assert.Single(await _archive.LoadAsync(42));
        Assert.Equal("after edit", edited.Content);
        Assert.True(edited.IsEdited);

        await _archive.DeleteAsync(20);
        Assert.Empty(await _archive.LoadAsync(42));
        Assert.Equal(0UL, await _archive.LatestMessageIdAsync(42));
    }

    [Fact]
    public async Task PruningDropsOnlyCachedMessagesInsideTheFetchedWindow()
    {
        // 10 is older than the fetched window; 13 was deleted on Discord while Steamy was closed.
        await _archive.SaveAsync([Message(10, 42, "old", 1), Message(11, 42, "kept", 2), Message(12, 42, "kept", 3), Message(13, 42, "deleted", 4)]);
        await _archive.PruneMissingAsync(42, [Message(11, 42, "kept", 2), Message(12, 42, "kept", 3)]);

        var history = await _archive.LoadAsync(42);
        Assert.Equal(["old", "kept", "kept"], history.Select(message => message.Content));
        Assert.DoesNotContain(history, message => message.Id == 13);
    }
}
