using System.IO;
using System.Text.Json;

namespace Steamy.Services;

public sealed record DiscordConversationEntry(ulong LastMessageId, long LastMessageAt, string Preview = "", bool FromMe = false, ulong ReadMessageId = 0, int UnreadCount = 0)
{
    public bool HasUnread => UnreadCount > 0;
}

/// <summary>Local DM index driving recency order, previews and read badges. Discord's Social SDK offers no MESSAGE_ACK,
/// so read state is tracked here from message events and never imported from Discord.</summary>
public interface IDiscordConversationStore
{
    DiscordConversationEntry? Get(ulong peerId);
    int Unread(ulong peerId);
    IReadOnlyCollection<ulong> Peers { get; }
    void ApplySummaries(IReadOnlyList<DiscordMessageSummary> summaries);
    void Record(ulong peerId, DiscordChatMessage message);
    void Forget(ulong peerId, ulong messageId);
    void MarkRead(ulong peerId, ulong messageId);
    event EventHandler? Changed;
}

public sealed class DiscordConversationStore : IDiscordConversationStore
{
    private readonly object _gate = new();
    private readonly Dictionary<ulong, DiscordConversationEntry> _entries = [];
    private readonly string _path;
    private readonly Action<Action> _changed;
    public event EventHandler? Changed;

    public DiscordConversationStore(string? path = null, Action<Action>? dispatch = null)
    {
        _path = path ?? Path.Combine(Steamy.Storage.AppDataPaths.Root, "discord-conversations.json");
        _changed = dispatch ?? (action => action());
        Load();
    }

    public DiscordConversationEntry? Get(ulong peerId) { lock (_gate) return _entries.GetValueOrDefault(peerId); }
    public int Unread(ulong peerId) { lock (_gate) return _entries.GetValueOrDefault(peerId)?.UnreadCount ?? 0; }
    public IReadOnlyCollection<ulong> Peers { get { lock (_gate) return _entries.Where(pair => pair.Value.LastMessageId > 0).Select(pair => pair.Key).ToArray(); } }

    public void ApplySummaries(IReadOnlyList<DiscordMessageSummary> summaries)
    {
        var touched = false;
        lock (_gate)
        {
            foreach (var summary in summaries)
            {
                // A summary without a message is not a conversation; accepting it would add an empty DM row.
                if (summary.UserId == 0 || summary.LastMessageId == 0) continue;
                var existing = _entries.GetValueOrDefault(summary.UserId);
                if (existing is not null && summary.LastMessageId <= existing.LastMessageId) continue;
                // A conversation seen for the first time starts fully read so the first sync never floods badges.
                if (existing is null)
                {
                    _entries[summary.UserId] = new(summary.LastMessageId, DiscordSnowflake.Time(summary.LastMessageId).ToUnixTimeMilliseconds());
                }
                else
                {
                    // Summaries carry only the last message ID, so a preview recorded for an older message
                    // can no longer be trusted; the row falls back to the peer's detail line.
                    var unread = summary.LastMessageId > existing.ReadMessageId ? Math.Max(1, existing.UnreadCount) : existing.UnreadCount;
                    _entries[summary.UserId] = existing with { LastMessageId = summary.LastMessageId, LastMessageAt = DiscordSnowflake.Time(summary.LastMessageId).ToUnixTimeMilliseconds(), Preview = "", FromMe = false, UnreadCount = unread };
                }
                touched = true;
            }
        }
        if (touched) Notify();
    }

    public void Record(ulong peerId, DiscordChatMessage message)
    {
        if (peerId == 0 || message.Id == 0 || message.LobbyId > 0) return;
        lock (_gate)
        {
            var existing = _entries.GetValueOrDefault(peerId);
            if (existing is not null && message.Id < existing.LastMessageId) return;
            // An edit keeps the same message ID; refresh the preview without touching recency or unread state.
            if (existing is not null && message.Id == existing.LastMessageId)
                _entries[peerId] = existing with { Preview = message.PreviewLabel, FromMe = message.IsOwn };
            else if (message.IsOwn)
                _entries[peerId] = new(message.Id, message.SentAt.ToUnixTimeMilliseconds(), message.PreviewLabel, FromMe: true, message.Id, 0);
            else
                _entries[peerId] = new(message.Id, message.SentAt.ToUnixTimeMilliseconds(), message.PreviewLabel, FromMe: false,
                    existing?.ReadMessageId ?? 0, Math.Clamp((existing?.UnreadCount ?? 0) + 1, 0, 999));
        }
        Notify();
    }

    public void Forget(ulong peerId, ulong messageId)
    {
        if (peerId == 0 || messageId == 0) return;
        lock (_gate)
        {
            // Only the newest message shapes the row; deleting an older message changes nothing visible.
            if (!_entries.TryGetValue(peerId, out var existing) || existing.LastMessageId != messageId) return;
            _entries[peerId] = existing with { Preview = "", FromMe = false, UnreadCount = 0 };
        }
        Notify();
    }

    public void MarkRead(ulong peerId, ulong messageId)
    {
        if (peerId == 0) return;
        lock (_gate)
        {
            // Read receipts must never create a conversation; only real messages add DM rows.
            if (!_entries.TryGetValue(peerId, out var existing)) return;
            var read = Math.Max(existing.ReadMessageId, messageId);
            if (read == existing.ReadMessageId && existing.UnreadCount == 0) return;
            _entries[peerId] = existing with { ReadMessageId = read, UnreadCount = 0 };
        }
        Notify();
    }

    private void Notify()
    {
        _changed(() => { try { Changed?.Invoke(this, EventArgs.Empty); } catch { } });
        Save();
    }

    private void Save()
    {
        string payload;
        lock (_gate) payload = JsonSerializer.Serialize(_entries, JsonOptions());
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temporary = _path + ".tmp";
            File.WriteAllText(temporary, payload);
            File.Move(temporary, _path, overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var entries = JsonSerializer.Deserialize<Dictionary<ulong, DiscordConversationEntry>>(File.ReadAllText(_path), JsonOptions());
            if (entries is null) return;
            var pruned = false;
            lock (_gate)
                foreach (var (id, entry) in entries)
                {
                    // Older builds stored placeholder entries for read receipts; they are not conversations.
                    if (entry is not null && id > 0 && entry.LastMessageId > 0) _entries[id] = entry;
                    else pruned = true;
                }
            if (pruned) Save();
        }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException) { }
    }

    private static JsonSerializerOptions JsonOptions() => new() { WriteIndented = false };
}
