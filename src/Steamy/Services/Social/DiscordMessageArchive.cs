using System.IO;
using Microsoft.Data.Sqlite;

namespace Steamy.Services;

/// <summary>Local archive of direct messages. Discord hands games only the latest 72 hours of a DM,
/// so everything Steamy sees is kept here to survive restarts and that window.</summary>
public interface IDiscordMessageArchive
{
    Task SaveAsync(IReadOnlyList<DiscordChatMessage> messages, CancellationToken token = default);
    Task<IReadOnlyList<DiscordChatMessage>> LoadAsync(ulong peerId, int limit = 1000, CancellationToken token = default);
    Task<ulong> LatestMessageIdAsync(ulong peerId, CancellationToken token = default);
    Task DeleteAsync(ulong messageId, CancellationToken token = default);
    /// <summary>Drops cached messages inside the fetched window that Discord no longer returns (deleted while Steamy was closed).</summary>
    Task PruneMissingAsync(ulong peerId, IReadOnlyList<DiscordChatMessage> history, CancellationToken token = default);
}

public sealed class DiscordMessageArchive : IDiscordMessageArchive
{
    private readonly string _path;
    private readonly object _initializeGate = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private bool _initialized;

    public DiscordMessageArchive(string? path = null) =>
        _path = path ?? Path.Combine(Steamy.Storage.AppDataPaths.Root, "discord-messages.db");

    public async Task SaveAsync(IReadOnlyList<DiscordChatMessage> messages, CancellationToken token = default)
    {
        var rows = messages.Where(message => message.Id > 0 && message.LobbyId == 0).ToArray();
        if (rows.Length == 0) return;
        await _writeGate.WaitAsync(token);
        try
        {
            EnsureInitialized();
            await using var connection = await OpenAsync(token);
            await using var transaction = await connection.BeginTransactionAsync(token);
            await using var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = """
                INSERT INTO DiscordMessages (MessageId, PeerId, AuthorId, LobbyId, ChannelId, Content, RawContent, SentAtMs,
                    IsOwn, IsEdited, HasAdditionalContent, AdditionalTitle, AdditionalKind, AdditionalCount, AuthorName, AuthorAvatarUrl, IsSystem)
                VALUES ($id, $peer, $author, 0, $channel, $content, $raw, $sent, $own, $edited, $extra, $title, $kind, $count, $name, $avatar, $system)
                ON CONFLICT(MessageId) DO UPDATE SET
                    Content = excluded.Content, RawContent = excluded.RawContent, IsEdited = excluded.IsEdited,
                    HasAdditionalContent = excluded.HasAdditionalContent, AdditionalTitle = excluded.AdditionalTitle,
                    AdditionalKind = excluded.AdditionalKind, AdditionalCount = excluded.AdditionalCount,
                    AuthorName = excluded.AuthorName, AuthorAvatarUrl = excluded.AuthorAvatarUrl, IsSystem = excluded.IsSystem
                """;
            var parameters = new (string Name, Func<DiscordChatMessage, object?> Value)[]
            {
                ("$id", message => (long)message.Id), ("$peer", message => (long)message.PeerId),
                ("$author", message => (long)message.AuthorId), ("$channel", message => (long)message.ChannelId),
                ("$content", message => message.Content), ("$raw", message => message.RawContent),
                ("$sent", message => message.SentAt.ToUnixTimeMilliseconds()),
                ("$own", message => message.IsOwn ? 1 : 0), ("$edited", message => message.IsEdited ? 1 : 0),
                ("$extra", message => message.HasAdditionalContent ? 1 : 0),
                ("$title", message => message.AdditionalTitle), ("$kind", message => message.AdditionalKind),
                ("$count", message => message.AdditionalCount), ("$name", message => message.AuthorName),
                ("$avatar", message => (object?)message.AuthorAvatarUrl ?? DBNull.Value),
                ("$system", message => message.IsSystem ? 1 : 0)
            };
            foreach (var (name, _) in parameters) command.Parameters.Add(name, SqliteType.Text);
            foreach (var message in rows)
            {
                foreach (var (name, value) in parameters) command.Parameters[name].Value = value(message) ?? DBNull.Value;
                await command.ExecuteNonQueryAsync(token);
            }
            await transaction.CommitAsync(token);
        }
        catch (Exception error) when (error is SqliteException or IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException) { }
        finally { _writeGate.Release(); }
    }

    public async Task<IReadOnlyList<DiscordChatMessage>> LoadAsync(ulong peerId, int limit = 1000, CancellationToken token = default)
    {
        if (peerId == 0 || limit <= 0) return [];
        try
        {
            EnsureInitialized();
            await using var connection = await OpenAsync(token);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT MessageId, AuthorId, ChannelId, Content, RawContent, SentAtMs, IsOwn, IsEdited, HasAdditionalContent,
                    AdditionalTitle, AdditionalKind, AdditionalCount, AuthorName, AuthorAvatarUrl, IsSystem
                FROM DiscordMessages WHERE PeerId = $peer ORDER BY SentAtMs DESC, MessageId DESC LIMIT $limit
                """;
            command.Parameters.AddWithValue("$peer", (long)peerId);
            command.Parameters.AddWithValue("$limit", limit);
            var rows = new List<DiscordChatMessage>();
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                var avatar = reader.IsDBNull(13) ? null : reader.GetString(13);
                rows.Add(new DiscordChatMessage((ulong)reader.GetInt64(0), (ulong)reader.GetInt64(1), peerId, 0,
                    (ulong)reader.GetInt64(2), reader.GetString(3), DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(5)),
                    reader.GetInt64(6) != 0, reader.GetInt64(8) != 0, reader.GetInt64(7) != 0,
                    reader.GetString(12), reader.GetString(9), reader.GetString(10), reader.GetInt32(11), avatar)
                { RawContent = reader.GetString(4), IsSystem = reader.GetInt64(14) != 0 });
            }
            rows.Reverse();
            return rows;
        }
        catch (Exception error) when (error is SqliteException or IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        { return []; }
    }

    public async Task<ulong> LatestMessageIdAsync(ulong peerId, CancellationToken token = default)
    {
        if (peerId == 0) return 0;
        try
        {
            EnsureInitialized();
            await using var connection = await OpenAsync(token);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COALESCE(MAX(MessageId), 0) FROM DiscordMessages WHERE PeerId = $peer";
            command.Parameters.AddWithValue("$peer", (long)peerId);
            var value = await command.ExecuteScalarAsync(token);
            return value is long id && id > 0 ? (ulong)id : 0;
        }
        catch (Exception error) when (error is SqliteException or IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        { return 0; }
    }

    public async Task DeleteAsync(ulong messageId, CancellationToken token = default)
    {
        if (messageId == 0) return;
        await _writeGate.WaitAsync(token);
        try
        {
            EnsureInitialized();
            await using var connection = await OpenAsync(token);
            await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM DiscordMessages WHERE MessageId = $id";
            command.Parameters.AddWithValue("$id", (long)messageId);
            await command.ExecuteNonQueryAsync(token);
        }
        catch (Exception error) when (error is SqliteException or IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException) { }
        finally { _writeGate.Release(); }
    }

    public async Task PruneMissingAsync(ulong peerId, IReadOnlyList<DiscordChatMessage> history, CancellationToken token = default)
    {
        if (peerId == 0 || history.Count == 0) return;
        var present = history.Select(message => (long)message.Id).ToHashSet();
        var oldest = present.Min();
        await _writeGate.WaitAsync(token);
        try
        {
            EnsureInitialized();
            await using var connection = await OpenAsync(token);
            await using var select = connection.CreateCommand();
            select.CommandText = "SELECT MessageId FROM DiscordMessages WHERE PeerId = $peer AND MessageId >= $oldest";
            select.Parameters.AddWithValue("$peer", (long)peerId);
            select.Parameters.AddWithValue("$oldest", oldest);
            var missing = new List<long>();
            await using (var reader = await select.ExecuteReaderAsync(token))
                while (await reader.ReadAsync(token))
                {
                    var id = reader.GetInt64(0);
                    if (!present.Contains(id)) missing.Add(id);
                }
            if (missing.Count == 0) return;
            await using var delete = connection.CreateCommand();
            delete.CommandText = $"DELETE FROM DiscordMessages WHERE MessageId IN ({string.Join(',', missing.Select((_, index) => "$m" + index))})";
            for (var index = 0; index < missing.Count; index++) delete.Parameters.AddWithValue("$m" + index, missing[index]);
            await delete.ExecuteNonQueryAsync(token);
        }
        catch (Exception error) when (error is SqliteException or IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException) { }
        finally { _writeGate.Release(); }
    }

    private void EnsureInitialized()
    {
        if (_initialized) return;
        lock (_initializeGate)
        {
            if (_initialized) return;
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _path, Mode = SqliteOpenMode.ReadWriteCreate }.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                PRAGMA journal_mode = WAL;
                CREATE TABLE IF NOT EXISTS DiscordMessages (
                    MessageId INTEGER PRIMARY KEY, PeerId INTEGER NOT NULL, AuthorId INTEGER NOT NULL, LobbyId INTEGER NOT NULL DEFAULT 0,
                    ChannelId INTEGER NOT NULL DEFAULT 0, Content TEXT NOT NULL, RawContent TEXT NOT NULL, SentAtMs INTEGER NOT NULL,
                    IsOwn INTEGER NOT NULL, IsEdited INTEGER NOT NULL, HasAdditionalContent INTEGER NOT NULL,
                    AdditionalTitle TEXT NOT NULL DEFAULT '', AdditionalKind TEXT NOT NULL DEFAULT '', AdditionalCount INTEGER NOT NULL DEFAULT 0,
                    AuthorName TEXT NOT NULL DEFAULT '', AuthorAvatarUrl TEXT NULL, IsSystem INTEGER NOT NULL DEFAULT 0);
                CREATE INDEX IF NOT EXISTS IX_DiscordMessages_Peer ON DiscordMessages(PeerId, SentAtMs);
                """;
            command.ExecuteNonQuery();
            _initialized = true;
        }
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken token)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _path, Mode = SqliteOpenMode.ReadWriteCreate }.ToString());
        await connection.OpenAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA busy_timeout = 5000;";
        await command.ExecuteNonQueryAsync(token);
        return connection;
    }
}
