using System.Globalization;

namespace Steamy.Services;

public enum DiscordDeliveryState { Sent, Sending, Failed }
public enum DiscordPresence { Online, Idle, Dnd, Streaming, Offline, Invisible, Unknown }
public static class DiscordPresenceExtensions
{
    public static string Label(this DiscordPresence presence) => presence switch
    {
        DiscordPresence.Online => "Online", DiscordPresence.Idle => "Idle", DiscordPresence.Dnd => "Do not disturb",
        DiscordPresence.Streaming => "Streaming", DiscordPresence.Invisible => "Invisible", _ => "Offline"
    };
    public static bool CountsOnline(this DiscordPresence presence) => presence is DiscordPresence.Online or DiscordPresence.Idle or DiscordPresence.Dnd or DiscordPresence.Streaming;
}
public interface IDiscordMessageDelivery
{
    Task<DiscordChatMessage> SendWithNonceAsync(ulong peerId, bool party, string content, string nonce, CancellationToken token = default);
}
public sealed class DiscordSendRejectedException(string message) : DiscordSocialException(message);
public sealed record DiscordMessageSummary(ulong UserId, ulong LastMessageId);

public sealed record DiscordChatMessage(ulong Id, ulong AuthorId, ulong PeerId, ulong LobbyId, ulong ChannelId,
    string Content, DateTimeOffset SentAt, bool IsOwn, bool HasAdditionalContent, bool IsEdited, string AuthorName = "", string AdditionalTitle = "", string AdditionalKind = "", int AdditionalCount = 0,
    string? AuthorAvatarUrl = null)
{
    public bool IsSystem { get; init; }
    // Header visibility is recomputed by the conversation (grouping consecutive messages from one author).
    public bool ShowHeader { get; init; } = true;
    // The avatar rides with the header, so consecutive messages only show the profile every five minutes.
    public bool ShowAuthorAvatar => ShowHeader && !IsOwn && !IsSystem;
    public string ClientNonce { get; init; } = "";
    public DiscordDeliveryState DeliveryState { get; init; } = DiscordDeliveryState.Sent;
    public string DeliveryError { get; init; } = "";
    public bool CanResend { get; init; }
    public DiscordVoiceInvite? VoiceInvite { get; init; }
    public bool HasVoiceInvite => VoiceInvite is not null;
    public bool NeedsDeliveryAction => DeliveryState == DiscordDeliveryState.Failed;
    public string DeliveryAction => CanResend ? "Retry" : "Check delivery";
    public string DayLabel => SocialTime.Day(SentAt.LocalDateTime.Date);
    public DateTime DayKey => SentAt.LocalDateTime.Date;
    public string RawContent { get; init; } = "";
    public string? ImageUrl => ChatMediaPolicy.ImageUrl(Content) ?? ChatMediaPolicy.ImageUrl(RawContent);
    public string DisplayContent
    {
        get
        {
            var text = Content.Trim();
            if (text.Length == 0) return Content;
            if (text == ImageUrl || text == StickerLink) return "";
            // A message that is only a sticker link is fully represented by the sticker tile.
            if (StickerLink is { } sticker && ChatMediaPolicy.IsOnlyMarkdownLinkTo(text, sticker)) return "";
            return Content;
        }
    }
    public bool HasText => DisplayContent.Length > 0;
    public string? StickerLink => ChatMediaPolicy.StickerUrl(Content) ?? ChatMediaPolicy.StickerUrl(RawContent);
    public bool HasSticker => StickerLink is not null;
    public string StickerName
    {
        get
        {
            var link = StickerLink;
            if (link is null) return "Sticker";
            var text = ChatMediaPolicy.MarkdownText(Content, link) ?? ChatMediaPolicy.StickerQueryName(link);
            if (string.IsNullOrWhiteSpace(text)) return "Sticker";
            text = text.Replace('+', ' ');
            try { text = Uri.UnescapeDataString(text); } catch (UriFormatException) { }
            return text.Length > 80 ? text[..80] : text;
        }
    }
    // The Social SDK hands real Discord stickers over as an empty message whose only trace is
    // AdditionalContent { Type = Other, Count = 1 } — no sticker id, name or asset is exposed.
    public bool IsStickerLike => HasAdditionalContent && (AdditionalKind == "Sticker"
        || AdditionalKind == "Other" && AdditionalTitle.Length == 0 && AdditionalCount <= 1
           && Content.Trim().Length == 0 && RawContent.Trim().Length == 0);
    public string MediaLabel => AdditionalTitle.Length > 0 ? AdditionalTitle : IsStickerLike ? "Discord sticker" : AdditionalCount > 1 ? $"{AdditionalCount} attachments" : AdditionalKind switch
    { "Poll" => "Discord poll", "VoiceMessage" => "Voice message", "Thread" => "Discord thread", "Embed" => "Shared content", "Sticker" => "Discord sticker", "Attachment" => "Attachment", _ => "Discord content" };
    // Discord attaches a link-preview embed to GIF/media links; when that same link renders inline the card would duplicate it.
    public bool ShowMediaCard => HasAdditionalContent && !HasSticker && !(ImageUrl is not null && AdditionalKind is "Embed" or "");
    public string AuthorLabel => IsOwn ? "You" : AuthorName.Length > 0 ? AuthorName : "Discord user";
    public string AuthorInitial => char.ToUpperInvariant(AuthorLabel[0]).ToString();
    // Only own, confirmed direct messages can be changed: the Social SDK exposes no lobby edit or delete.
    public bool CanManage => IsOwn && !IsSystem && LobbyId == 0 && !HasVoiceInvite && DeliveryState == DiscordDeliveryState.Sent && Id > 0;
    public string TimeLabel => SocialTime.Clock(SentAt) + (IsEdited ? " (edited)" : "")
        + (IsOwn ? DeliveryState switch { DiscordDeliveryState.Sending => " · Sending…", DiscordDeliveryState.Failed => CanResend ? " · Failed" : " · Not confirmed", _ => " · Sent" } : "");
    public string HoverTime => SocialTime.Clock(SentAt) + " · " + SocialTime.DayLong(SentAt.LocalDateTime);
    public string PreviewLabel
    {
        get
        {
            if (IsSystem) return Content;
            if (Content.Trim().Equals(ImageUrl, StringComparison.OrdinalIgnoreCase)) return "GIF";
            if (IsStickerLike || HasSticker) return "Sticker";
            if (ImageUrl is not null) return "Photo";
            if (HasAdditionalContent && AdditionalKind is not ("" or "Other")) return MediaLabel;
            var text = Content.Replace('\n', ' ').Trim();
            return text.Length == 0 ? "Message" : text.Length > 64 ? text[..64].TrimEnd() + "…" : text;
        }
    }
}
public interface IDiscordMessageNavigation
{
    Task OpenMessageAsync(ulong messageId, CancellationToken token = default);
}
public sealed record DiscordMessageChange(DiscordChatMessage? Message, ulong DeletedId = 0);
public sealed record DiscordPartyGame(int AppId, string Name);
public sealed record DiscordParty(ulong Id, DiscordPartyGame Game, IReadOnlyList<DiscordProfile> Members);
public sealed record DiscordPartyInvite(Guid Id, ulong SenderId, string SenderName, string Label);

public interface IDiscordChatService
{
    bool HasCommunicationPermission { get; }
    DiscordParty? Party { get; }
    IReadOnlyList<DiscordPartyInvite> Invites { get; }
    event EventHandler<DiscordMessageChange>? MessageChanged;
    Task<IReadOnlyList<DiscordChatMessage>> GetMessagesAsync(ulong peerId, bool party, CancellationToken token = default);
    Task<IReadOnlyList<DiscordMessageSummary>> GetMessageSummariesAsync(CancellationToken token = default);
    Task<DiscordChatMessage> SendMessageAsync(ulong peerId, bool party, string content, CancellationToken token = default);
    Task EditMessageAsync(ulong peerId, ulong messageId, string content, CancellationToken token = default);
    Task DeleteMessageAsync(ulong peerId, ulong messageId, CancellationToken token = default);
    void SetShowingChat(bool showing);
    Task CreatePartyAsync(DiscordPartyGame game, CancellationToken token = default);
    Task InviteToPartyAsync(ulong friendId, CancellationToken token = default);
    Task AcceptPartyInviteAsync(Guid inviteId, CancellationToken token = default);
    void DismissPartyInvite(Guid inviteId);
    Task LeavePartyAsync(CancellationToken token = default);
}

internal interface IDiscordCommunicationClient
{
    DiscordParty? Party { get; }
    IReadOnlyList<DiscordPartyInvite> Invites { get; }
    event EventHandler<DiscordMessageChange>? MessageChanged;
    Task<IReadOnlyList<DiscordChatMessage>> GetMessagesAsync(ulong peerId, bool party, CancellationToken token);
    Task<IReadOnlyList<DiscordMessageSummary>> GetMessageSummariesAsync(CancellationToken token);
    Task<DiscordChatMessage> SendMessageAsync(ulong peerId, bool party, string content, CancellationToken token);
    Task EditMessageAsync(ulong peerId, ulong messageId, string content, CancellationToken token);
    Task DeleteMessageAsync(ulong peerId, ulong messageId, CancellationToken token);
    void SetShowingChat(bool showing);
    Task CreatePartyAsync(DiscordPartyGame game, CancellationToken token);
    Task InviteToPartyAsync(ulong friendId, CancellationToken token);
    Task AcceptPartyInviteAsync(Guid inviteId, CancellationToken token);
    void DismissPartyInvite(Guid inviteId);
    Task LeavePartyAsync(CancellationToken token);
    DiscordProfile? GetUser(ulong userId);
}

/// <summary>Resolves DM peers Discord knows about that aren't on the friends list.</summary>
internal interface IDiscordUserDirectory
{
    DiscordProfile? GetUser(ulong userId);
}

public sealed partial class DiscordSocialService : IDiscordChatService, IDiscordMessageNavigation, IDiscordMessageDelivery, IDiscordUserDirectory
{
    public Task<DiscordChatMessage> SendWithNonceAsync(ulong peerId, bool party, string content, string nonce, CancellationToken token = default)
    {
        if (!Guid.TryParseExact(nonce, "N", out _) || content.Trim().Length is 0 or > 2000) throw new ArgumentException("Invalid outgoing message.");
        return CommunicationOperationAsync((client, ct) =>
        {
            ValidateConversation(peerId, party);
            return client is IDiscordMessageDelivery delivery ? delivery.SendWithNonceAsync(peerId, party, content.Trim(), nonce, ct)
                : client.SendMessageAsync(peerId, party, content.Trim(), ct);
        }, token);
    }
    public Task OpenMessageAsync(ulong messageId, CancellationToken token = default) => CommunicationOperationAsync(async (client, ct) =>
    {
        if (client is not IDiscordMessageNavigation navigation) throw new DiscordSocialException("This Discord SDK cannot open the message.");
        await navigation.OpenMessageAsync(messageId, ct); return true;
    }, token);
    public bool HasCommunicationPermission => Snapshot.IsConnected && _session?.Scopes?.Split(' ', StringSplitOptions.RemoveEmptyEntries)
        .Contains("sdk.social_layer", StringComparer.Ordinal) == true;
    public DiscordParty? Party => (_client as IDiscordCommunicationClient)?.Party;
    public IReadOnlyList<DiscordPartyInvite> Invites => (_client as IDiscordCommunicationClient)?.Invites ?? [];
    public event EventHandler<DiscordMessageChange>? MessageChanged;

    public async Task<IReadOnlyList<DiscordChatMessage>> GetMessagesAsync(ulong peerId, bool party, CancellationToken token = default)
    {
        var history = await CommunicationOperationAsync((client, ct) =>
        {
            ValidateConversation(peerId, party);
            return client.GetMessagesAsync(peerId, party, ct);
        }, token);
        if (!party && _archive is not null && history.Count > 0)
        {
            // Archiving outlives the caller's request so a cancelled chat view still keeps the fetched page.
            await _archive.SaveAsync(history, CancellationToken.None);
            await _archive.PruneMissingAsync(peerId, history, CancellationToken.None);
        }
        return history;
    }
    public Task<IReadOnlyList<DiscordMessageSummary>> GetMessageSummariesAsync(CancellationToken token = default) =>
        CommunicationOperationAsync((client, ct) => client.GetMessageSummariesAsync(ct), token);
    public async Task<DiscordChatMessage> SendMessageAsync(ulong peerId, bool party, string content, CancellationToken token = default)
    {
        var text = content.Trim();
        if (text.Length is 0 or > 2000) throw new ArgumentException("Write a message of up to 2,000 characters.");
        var message = await CommunicationOperationAsync((client, ct) => { ValidateConversation(peerId, party); return client.SendMessageAsync(peerId, party, text, ct); }, token);
        if (!party && _archive is not null && message is { LobbyId: 0, Id: > 0 }) await _archive.SaveAsync([message], CancellationToken.None);
        return message;
    }
    public Task EditMessageAsync(ulong peerId, ulong messageId, string content, CancellationToken token = default)
    {
        var text = content.Trim();
        if (messageId == 0 || text.Length is 0 or > 2000) throw new ArgumentException("Write a message of up to 2,000 characters.");
        return CommunicationOperationAsync(async (client, ct) =>
        {
            ValidateConversation(peerId, false);
            await client.EditMessageAsync(peerId, messageId, text, ct); return true;
        }, token);
    }
    public Task DeleteMessageAsync(ulong peerId, ulong messageId, CancellationToken token = default)
    {
        if (messageId == 0) throw new ArgumentException("This message can't be deleted.");
        return CommunicationOperationAsync(async (client, ct) =>
        {
            ValidateConversation(peerId, false);
            await client.DeleteMessageAsync(peerId, messageId, ct); return true;
        }, token);
    }
    public Task CreatePartyAsync(DiscordPartyGame game, CancellationToken token = default)
    {
        if (game.AppId <= 0 || string.IsNullOrWhiteSpace(game.Name) || game.Name.Length > 200)
            throw new ArgumentException("Choose a game from your library.");
        return CommunicationOperationAsync(async (client, ct) => { await client.CreatePartyAsync(game, ct); return true; }, token);
    }
    public Task InviteToPartyAsync(ulong friendId, CancellationToken token = default) => CommunicationOperationAsync(async (client, ct) =>
    {
        ValidateConversation(friendId, false);
        if (Party is null) throw new InvalidOperationException("Create a party before inviting friends.");
        await client.InviteToPartyAsync(friendId, ct); return true;
    }, token);
    public Task AcceptPartyInviteAsync(Guid inviteId, CancellationToken token = default) => CommunicationOperationAsync(async (client, ct) =>
    {
        if (!Invites.Any(invite => invite.Id == inviteId)) throw new ArgumentException("This invitation is no longer available.");
        await client.AcceptPartyInviteAsync(inviteId, ct); return true;
    }, token);
    public Task LeavePartyAsync(CancellationToken token = default) => CommunicationOperationAsync(async (client, ct) =>
    { await client.LeavePartyAsync(ct); return true; }, token);
    public void DismissPartyInvite(Guid inviteId) { (_client as IDiscordCommunicationClient)?.DismissPartyInvite(inviteId); Changed?.Invoke(this, EventArgs.Empty); }
    public void SetShowingChat(bool showing) { if (_client is IDiscordCommunicationClient client) client.SetShowingChat(showing && HasCommunicationPermission); }

    private void ValidateConversation(ulong peerId, bool party)
    {
        // Non-friend DM peers stay valid while a conversation with them exists locally.
        if (party ? Party?.Id != peerId : !Snapshot.Relationships.Any(friend => friend.Id == peerId && friend.IsFriend)
            && _conversations?.Get(peerId) is null)
            throw new InvalidOperationException(party ? "This party has ended. Join or create another party." : "Choose a current Discord friend to chat with.");
    }
    public DiscordProfile? GetUser(ulong userId) => userId > 0 && _client is IDiscordCommunicationClient communication && Snapshot.IsConnected
        ? communication.GetUser(userId) : null;
    private Task<T> CommunicationOperationAsync<T>(Func<IDiscordCommunicationClient, CancellationToken, Task<T>> action, CancellationToken token) =>
        ClientOperationAsync((client, ct) => client is IDiscordCommunicationClient communication ? action(communication, ct)
            : throw new InvalidOperationException("Reconnect Discord to enable chat and parties."), true, token);
    private void ClientMessageChanged(object? sender, DiscordMessageChange change)
    {
        if (!ReferenceEquals(sender, _client) || !HasCommunicationPermission) return;
        if (_archive is not null)
        {
            if (change.DeletedId > 0) _ = _archive.DeleteAsync(change.DeletedId);
            else if (change.Message is { LobbyId: 0, Id: > 0 } message) _ = _archive.SaveAsync([message]);
        }
        MessageChanged?.Invoke(this, change);
    }
}
