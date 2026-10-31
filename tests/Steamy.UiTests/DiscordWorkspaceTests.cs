using System.IO;
using System.Text.Json;
using Steamy.Models;
using Steamy.Services;
using Steamy.ViewModels;

namespace Steamy.UiTests;

public sealed partial class DiscordWorkspaceTests
{
    [Fact]
    public async Task SwitchingContactsDiscardsLateHistoryFromThePreviousConversation()
    {
        var service = new SocialFixture();
        var oldHistory = new TaskCompletionSource<IReadOnlyList<DiscordChatMessage>>();
        service.History = (id, _) => id == 2 ? oldHistory.Task : Task.FromResult<IReadOnlyList<DiscordChatMessage>>([Message(20, 3, "Conversation three")]);
        using var model = new DiscordFriendsViewModel(service, new Settings(), conversations: TestStores.Isolated());
        model.SelectedFriend = model.Friends.First(friend => friend.Id == 2);
        model.SelectedFriend = model.Friends.First(friend => friend.Id == 3);
        oldHistory.SetResult([Message(10, 2, "Old conversation")]);
        await Task.Delay(20);
        Assert.Equal("Conversation three", Assert.Single(model.Messages).Content);
        Assert.Equal(3UL, model.SelectedFriend!.Id);
    }

    [Fact]
    public void OpeningTheWorkspaceLandsOnTheMostRecentConversationWithoutOverridingUserPicks()
    {
        var service = new SocialFixture();
        using var model = new DiscordFriendsViewModel(service, new Settings(), conversations: TestStores.Isolated());
        service.Notify(new(Message(10, 2, "Older") with { SentAt = DateTimeOffset.UtcNow.AddMinutes(-5) }));
        service.Notify(new(Message(20, 3, "Newest")));
        Assert.Null(model.SelectedFriend);
        model.SetChatVisible(true);
        Assert.Equal(3UL, model.SelectedFriend!.Id);
        model.SelectedFriend = model.Friends.First(friend => friend.Id == 2);
        model.SetChatVisible(false); model.SetChatVisible(true);
        Assert.Equal(2UL, model.SelectedFriend!.Id);
    }

    [Fact]
    public void AutomaticSelectionUpgradesToTheNewestDiscordSummaryButUserPicksStay()
    {
        var service = new SocialFixture();
        var store = TestStores.Isolated();
        using var model = new DiscordFriendsViewModel(service, new Settings(), conversations: store);
        service.Notify(new(Message(10, 2, "Local history")));
        model.SetChatVisible(true);
        Assert.Equal(2UL, model.SelectedFriend!.Id);
        // Discord reports a newer DM (peer 3) that Steamy never saw live, e.g. while it was closed.
        store.ApplySummaries([new DiscordMessageSummary(3, RecentSnowflake(1000))]);
        model.SetChatVisible(true);
        Assert.Equal(3UL, model.SelectedFriend!.Id);
        // Once the user picks a conversation, later summaries must not steal it.
        model.SelectedFriend = model.Friends.First(friend => friend.Id == 2);
        store.ApplySummaries([new DiscordMessageSummary(3, RecentSnowflake(2000))]);
        model.SetChatVisible(false); model.SetChatVisible(true);
        Assert.Equal(2UL, model.SelectedFriend!.Id);
    }
    private static ulong RecentSnowflake(int offsetMilliseconds)
        => (ulong)((DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 1420070400000L + offsetMilliseconds) << 22);

    [Fact]
    public void ReadReceiptsAndEmptySummariesNeverCreateDmRows()
    {
        var store = new DiscordConversationStore(Path.Combine(Path.GetTempPath(), "steamy-store-" + Guid.NewGuid().ToString("N") + ".json"));
        store.MarkRead(2, 0);
        store.MarkRead(3, RecentSnowflake(0));
        Assert.Empty(store.Peers);
        Assert.Null(store.Get(2));
        store.ApplySummaries([new DiscordMessageSummary(4, 0)]);
        Assert.Empty(store.Peers);
        store.ApplySummaries([new DiscordMessageSummary(5, RecentSnowflake(0))]);
        Assert.Equal(new[] { 5UL }, store.Peers);
    }

    [Fact]
    public void LegacyEntriesWithoutMessagesArePrunedWhenTheStoreLoads()
    {
        var path = Path.Combine(Path.GetTempPath(), "steamy-store-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, """{"2":{"LastMessageId":0,"LastMessageAt":0,"Preview":"","FromMe":false,"ReadMessageId":2,"UnreadCount":0},"3":{"LastMessageId":50,"LastMessageAt":1000,"Preview":"Hi","FromMe":false,"ReadMessageId":50,"UnreadCount":0}}""");
        var store = new DiscordConversationStore(path);
        Assert.Equal(new[] { 3UL }, store.Peers);
        Assert.Null(store.Get(2));
        Assert.DoesNotContain("\"2\"", File.ReadAllText(path));
    }

    [Fact]
    public void DmFilterCountsOnlyConversationsThatContainMessages()
    {
        var store = TestStores.Isolated();
        using var model = new DiscordFriendsViewModel(new SocialFixture(), new Settings(), conversations: store);
        store.ApplySummaries([new DiscordMessageSummary(2, RecentSnowflake(0)), new DiscordMessageSummary(3, 0)]);
        Assert.Equal(1, model.FilterCount("DMs"));
    }

    [Fact]
    public async Task SummariesThatArriveAfterThePageOpensUpgradeTheAutomaticPick()
    {
        var service = new SocialFixture();
        var summaries = new TaskCompletionSource<IReadOnlyList<DiscordMessageSummary>>();
        service.Summaries = _ => summaries.Task;
        using var model = new DiscordFriendsViewModel(service, new Settings(), conversations: TestStores.Isolated());
        service.Notify(new(Message(10, 2, "Local history")));
        model.SetChatVisible(true);
        Assert.Equal(2UL, model.SelectedFriend!.Id);
        summaries.SetResult([new DiscordMessageSummary(3, RecentSnowflake(1000))]);
        for (var attempt = 0; attempt < 100 && model.SelectedFriend?.Id != 3; attempt++) await Task.Delay(10);
        Assert.Equal(3UL, model.SelectedFriend!.Id);
    }

    [Fact]
    public void ManuallyPickedChatsSurviveNewerSummariesWhileThePageStaysOpen()
    {
        var store = TestStores.Isolated();
        store.ApplySummaries([new DiscordMessageSummary(2, RecentSnowflake(0)), new DiscordMessageSummary(3, RecentSnowflake(1000))]);
        using var model = new DiscordFriendsViewModel(new SocialFixture(), new Settings(), conversations: store);
        model.SelectedFriend = model.Friends.First(friend => friend.Id == 2);
        model.SetChatVisible(true);
        Assert.Equal(2UL, model.SelectedFriend!.Id);
        store.ApplySummaries([new DiscordMessageSummary(3, RecentSnowflake(2000))]);
        Assert.Equal(2UL, model.SelectedFriend!.Id);
    }

    [Fact]
    public void OpeningTheWorkspaceWithoutAnyHistoryKeepsTheInvitationPane()
    {
        using var model = new DiscordFriendsViewModel(new SocialFixture(), new Settings(), conversations: TestStores.Isolated());
        model.SetChatVisible(true);
        Assert.Null(model.SelectedFriend);
        Assert.Equal(System.Windows.Visibility.Visible, model.NoConversationVisibility);
    }

    [Fact]
    public async Task HistoryCannotResurrectDeletedMessagesOrOverwriteALiveEdit()
    {
        var service = new SocialFixture();
        var history = new TaskCompletionSource<IReadOnlyList<DiscordChatMessage>>();
        service.History = (_, _) => history.Task;
        using var model = new DiscordFriendsViewModel(service, new Settings(), conversations: TestStores.Isolated());
        model.SelectedFriend = model.Friends.First(friend => friend.Id == 2);
        service.Notify(new(Message(10, 2, "Edited live") with { IsEdited = true }));
        service.Notify(new(null, 11));
        history.SetResult([Message(10, 2, "Original"), Message(11, 2, "Deleted")]);
        await Task.Delay(20);
        Assert.Equal("Edited live", Assert.Single(model.Messages).Content);
    }

    [Fact]
    public async Task FailedMessagesRemainExplicitlyUnconfirmedAndDoNotReportSuccess()
    {
        var service = new SocialFixture { SendError = new DiscordSocialException("Privacy settings prevented delivery") };
        using var model = new DiscordFriendsViewModel(service, new Settings(), conversations: TestStores.Isolated());
        model.SelectedFriend = model.Friends[0];
        model.MessageDraft = "Ready to play?";
        await model.SendMessageCommand.ExecuteAsync(null);
        Assert.Empty(model.MessageDraft);
        var failed = Assert.Single(model.Messages);
        Assert.Equal(DiscordDeliveryState.Failed, failed.DeliveryState);
        Assert.False(failed.CanResend);
        Assert.Contains("Privacy", model.ChatStatus);
    }

    [Fact]
    public async Task ConfirmedSendsDeduplicateLiveEchoesAndPreserveTextTypedWhileSending()
    {
        var service = new SocialFixture();
        var sending = new TaskCompletionSource<DiscordChatMessage>(); service.SendCompletion = sending;
        using var model = new DiscordFriendsViewModel(service, new Settings(), conversations: TestStores.Isolated());
        model.SelectedFriend = model.Friends.First(friend => friend.Id == 2); model.MessageDraft = "First message";
        var send = model.SendMessageCommand.ExecuteAsync(null);
        Assert.Equal(DiscordDeliveryState.Sending, Assert.Single(model.Messages).DeliveryState);
        model.MessageDraft = "Next message";
        var confirmed = Message(50, 2, "First message") with { IsOwn = true, AuthorId = 1 };
        service.Notify(new(confirmed)); sending.SetResult(confirmed);
        await send;
        Assert.Single(model.Messages);
        Assert.Equal("Next message", model.MessageDraft);
    }

    [Fact]
    public void PresenceUpdatesPreserveTheSelectedConversationAndComposer()
    {
        var service = new SocialFixture();
        using var model = new DiscordFriendsViewModel(service, new Settings(), conversations: TestStores.Isolated());
        model.SelectedFriend = model.Friends.First(friend => friend.Id == 2); model.MessageDraft = "Draft stays here";
        service.Notify(new(Message(1, 2, "Hello")));
        service.ChangePresence(2, DiscordPresence.Idle);
        Assert.Equal(2UL, model.SelectedFriend!.Id);
        Assert.Equal("Idle", model.SelectedFriend.Status);
        Assert.Equal("Draft stays here", model.MessageDraft);
        Assert.Single(model.Messages);
    }

    [Fact]
    public async Task PartyLaunchUsesOnlyAMatchingInstalledLocalGameAndNeverAutostartsOnJoin()
    {
        var service = new SocialFixture(); var store = new AppDataStore(); var launcher = new LaunchFixture();
        store.Games.Add(new Game { AppId = 42, Name = "My installed game", InstallState = GameInstallState.Installed });
        store.Games.Add(new Game { AppId = 99, Name = "Not installed", InstallState = GameInstallState.NotInstalled });
        using var model = new DiscordFriendsViewModel(service, new Settings(), store, launcher, conversations: TestStores.Isolated());
        Assert.Single(model.AvailableGames);
        await model.CreatePartyCommand.ExecuteAsync(null);
        Assert.Equal(0, launcher.Requests);
        Assert.Equal(42, service.Party!.Game.AppId);
        await model.LaunchGameCommand.ExecuteAsync(null);
        Assert.Equal(42, launcher.LastGame!.AppId);
        service.SetParty(new(77, new(99, "Remote game name"), []));
        Assert.False(model.LaunchGameCommand.CanExecute(null));
        Assert.Equal(1, launcher.Requests);
    }

    [Fact]
    public async Task DisconnectClearsChatContentAndDisablesCommunicationActions()
    {
        var service = new SocialFixture();
        using var model = new DiscordFriendsViewModel(service, new Settings(), conversations: TestStores.Isolated());
        model.SelectedFriend = model.Friends[0]; service.Notify(new(Message(1, 2, "Private conversation")));
        model.MessageDraft = "Unsaved text";
        await service.DisconnectAsync();
        Assert.Empty(model.Messages); Assert.Empty(model.MessageDraft);
        Assert.Null(model.SelectedFriend); Assert.False(model.SendMessageCommand.CanExecute(null));
        Assert.DoesNotContain("Private conversation", JsonSerializer.Serialize(new Settings().Load()));
    }

    [Fact]
    public async Task EditingYourOwnMessageSavesItAndClearsTheEditBanner()
    {
        var service = new SocialFixture();
        using var model = new DiscordFriendsViewModel(service, new Settings(), conversations: TestStores.Isolated());
        model.SelectedFriend = model.Friends.First(friend => friend.Id == 2);
        service.Notify(new(Message(10, 2, "Original text") with { IsOwn = true, AuthorId = 1 }));
        Assert.True(Assert.Single(model.Messages).CanManage);
        model.BeginEditMessageCommand.Execute(model.Messages[0]);
        Assert.True(model.IsEditing);
        Assert.Equal("Original text", model.MessageDraft);
        model.MessageDraft = "Rewritten text";
        await model.SendMessageCommand.ExecuteAsync(null);
        Assert.Equal(((ulong)2, (ulong)10, "Rewritten text"), Assert.Single(service.Edits));
        Assert.False(model.IsEditing);
        Assert.Empty(model.MessageDraft);
        var edited = Assert.Single(model.Messages);
        Assert.Equal("Rewritten text", edited.Content);
        Assert.True(edited.IsEdited);
        Assert.Equal(0, service.SendCalls);
    }

    [Fact]
    public void CancellingAnEditRestoresTheDraftItReplaced()
    {
        var service = new SocialFixture();
        using var model = new DiscordFriendsViewModel(service, new Settings(), conversations: TestStores.Isolated());
        model.SelectedFriend = model.Friends.First(friend => friend.Id == 2);
        service.Notify(new(Message(10, 2, "Original") with { IsOwn = true, AuthorId = 1 }));
        model.MessageDraft = "Unsent draft";
        model.BeginEditMessageCommand.Execute(model.Messages[0]);
        Assert.Equal("Original", model.MessageDraft);
        model.MessageDraft = "Rewritten";
        model.CancelMessageEditCommand.Execute(null);
        Assert.False(model.IsEditing);
        Assert.Equal("Unsent draft", model.MessageDraft);
        Assert.Empty(service.Edits);
    }

    [Fact]
    public async Task FailedEditsKeepTheComposerOpenAndTheOriginalMessage()
    {
        var service = new SocialFixture();
        using var model = new DiscordFriendsViewModel(service, new Settings(), conversations: TestStores.Isolated());
        model.SelectedFriend = model.Friends.First(friend => friend.Id == 2);
        service.Notify(new(Message(10, 2, "Original") with { IsOwn = true, AuthorId = 1 }));
        model.BeginEditMessageCommand.Execute(model.Messages[0]);
        model.MessageDraft = "Rewritten";
        service.EditError = new DiscordSocialException("Discord rejected the edit.");
        await model.SendMessageCommand.ExecuteAsync(null);
        Assert.True(model.IsEditing);
        Assert.Equal("Rewritten", model.MessageDraft);
        Assert.Equal("Original", Assert.Single(model.Messages).Content);
        Assert.Contains("rejected", model.ChatStatus);
    }

    [Fact]
    public async Task DeletingYourMessageRemovesItAndFailuresKeepItVisible()
    {
        var service = new SocialFixture();
        using var model = new DiscordFriendsViewModel(service, new Settings(), conversations: TestStores.Isolated());
        model.SelectedFriend = model.Friends.First(friend => friend.Id == 2);
        var own = Message(10, 2, "Delete me") with { IsOwn = true, AuthorId = 1 };
        service.Notify(new(own));
        service.DeleteError = new DiscordSocialException("Discord couldn't delete this message.");
        await model.DeleteMessageCommand.ExecuteAsync(model.Messages[0]);
        Assert.Single(model.Messages);
        Assert.Contains("delete", model.ChatStatus, StringComparison.OrdinalIgnoreCase);
        service.DeleteError = null;
        await model.DeleteMessageCommand.ExecuteAsync(model.Messages[0]);
        Assert.Empty(model.Messages);
        Assert.Equal(((ulong)2, (ulong)10), Assert.Single(service.Deletes));
        service.Notify(new(own)); // A late live update must not resurrect the deleted message.
        Assert.Empty(model.Messages);
    }

    [Fact]
    public async Task DeletingTheMessageBeingEditedLeavesTheComposerUsable()
    {
        var service = new SocialFixture();
        using var model = new DiscordFriendsViewModel(service, new Settings(), conversations: TestStores.Isolated());
        model.SelectedFriend = model.Friends.First(friend => friend.Id == 2);
        var own = Message(10, 2, "Edit me") with { IsOwn = true, AuthorId = 1 };
        service.Notify(new(own));
        model.BeginEditMessageCommand.Execute(model.Messages[0]);
        await model.DeleteMessageCommand.ExecuteAsync(model.Messages[0]);
        Assert.False(model.IsEditing);
        Assert.Empty(model.Messages);
        // The composer keeps the text so it can still be sent as a new message.
        Assert.Equal("Edit me", model.MessageDraft);
    }

    [Fact]
    public void OnlyConfirmedOwnDirectMessagesCanBeEditedOrDeleted()
    {
        var other = Message(10, 2, "Theirs");
        var own = other with { IsOwn = true, AuthorId = 1 };
        Assert.True(own.CanManage);
        Assert.False(other.CanManage);
        Assert.False((own with { LobbyId = 9 }).CanManage);
        Assert.False((own with { DeliveryState = DiscordDeliveryState.Sending }).CanManage);
        Assert.False((own with { DeliveryState = DiscordDeliveryState.Failed }).CanManage);
        Assert.False((own with { IsSystem = true }).CanManage);
    }

    private static DiscordChatMessage Message(ulong id, ulong peer, string text) => new(id, peer, peer, 0, 1, text, DateTimeOffset.UtcNow, false, false, false);
    [Fact]
    public void SwitchingConversationsPreservesEachDraftInMemory()
    {
        using var model = new DiscordFriendsViewModel(new SocialFixture(), new Settings(), conversations: TestStores.Isolated());
        model.SelectedFriend = model.Friends[0]; model.MessageDraft = "Draft for Two";
        model.SelectedFriend = model.Friends[1]; Assert.Empty(model.MessageDraft); model.MessageDraft = "Draft for Three";
        model.SelectedFriend = model.Friends[0]; Assert.Equal("Draft for Two", model.MessageDraft);
        model.SelectedFriend = model.Friends[1]; Assert.Equal("Draft for Three", model.MessageDraft);
    }
    [Fact]
    public async Task PlayTogetherCreatesOnePartyAndInvitesTheSelectedFriendWithoutChangingTheirChat()
    {
        var service = new SocialFixture(); var store = new AppDataStore();
        store.Games.Add(new Game { AppId = 42, Name = "Installed game", InstallState = GameInstallState.Installed });
        using var model = new DiscordFriendsViewModel(service, new Settings(), store, conversations: TestStores.Isolated());
        model.SelectedFriend = model.Friends.First(friend => friend.Id == 2); model.MessageDraft = "A retained draft";
        await model.PlayWithFriendCommand.ExecuteAsync(null);
        Assert.Equal(42, service.Party!.Game.AppId); Assert.Equal(2UL, service.Invited);
        Assert.Equal(2UL, model.SelectedFriend!.Id); Assert.Equal("A retained draft", model.MessageDraft);
        Assert.True(model.InvitePeople.Single(person => person.Friend.Id == 2).WasInvited);
        Assert.Equal("Invite again", model.InvitePeople.Single(person => person.Friend.Id == 2).Label);
    }
    [Fact]
    public async Task FailedPartyInviteIsNotMarkedSentAndPendingInvitesDisableOtherPartyActions()
    {
        var service = new SocialFixture(); service.SetParty(new(9, new(42, "Installed game"), []));
        using var model = new DiscordFriendsViewModel(service, new Settings(), conversations: TestStores.Isolated());
        model.SelectedFriend = model.Friends[0];
        service.InviteCompletion = new TaskCompletionSource();
        var pending = model.InvitePersonCommand.ExecuteAsync(model.SelectedFriend);
        Assert.True(model.IsBusy); Assert.False(model.LeavePartyCommand.CanExecute(null)); Assert.False(model.PlayWithFriendCommand.CanExecute(null));
        service.InviteCompletion.SetException(new DiscordSocialException("Delivery failed")); await pending;
        Assert.True(model.IsError); Assert.False(model.InvitePeople.Single(person => person.Friend.Id == 2).WasInvited);
        Assert.True(model.LeavePartyCommand.CanExecute(null));
    }
    [Fact]
    public async Task SwitchingPartyLeavesThenJoinsWithoutLaunchingAGame()
    {
        var service = new SocialFixture(); service.SetParty(new(9, new(42, "Old party"), []));
        var launcher = new LaunchFixture();
        using var model = new DiscordFriendsViewModel(service, new Settings(), new AppDataStore(), launcher, conversations: TestStores.Isolated());
        Assert.Equal("Switch party", model.JoinInviteLabel);
        await model.JoinInviteCommand.ExecuteAsync(new DiscordPartyInvite(Guid.NewGuid(), 3, "Three", "A new party"));
        Assert.Equal(new[] { "leave", "join" }, service.PartyOperations);
        Assert.Equal(11UL, service.Party!.Id); Assert.Equal("Party chat", model.ChatTitle); Assert.Equal(0, launcher.Requests);
    }
    [Fact]
    public async Task PartyArtworkLoadsWithoutOpeningTheLibraryAndCancelsObsoleteSelections()
    {
        var service = new SocialFixture(); var store = new AppDataStore(); var artwork = new PendingArtwork();
        var first = new Game { AppId = 42, Name = "First game", InstallState = GameInstallState.Installed };
        var second = new Game { AppId = 43, Name = "Second game", InstallState = GameInstallState.Installed };
        store.Games.Add(first); store.Games.Add(second);
        using var model = new DiscordFriendsViewModel(service, new Settings(), store, artwork: artwork, conversations: TestStores.Isolated());
        Assert.Same(first, Assert.Single(artwork.Requests).Game);
        model.SelectedGame = second;
        Assert.True(artwork.Requests[0].Token.IsCancellationRequested);
        Assert.Same(second, model.PartyGame);
        service.SetParty(new(9, new(42, "Party game"), []));
        Assert.True(artwork.Requests[1].Token.IsCancellationRequested);
        Assert.Same(first, model.PartyGame);
        service.ChangePresence(2, DiscordPresence.Idle);
        Assert.Equal(3, artwork.Requests.Count);
        await model.DisconnectCommand.ExecuteAsync(null);
        Assert.True(artwork.Requests[2].Token.IsCancellationRequested);
    }
    private sealed class PendingArtwork : IArtworkService
    {
        public List<(Game Game, CancellationToken Token)> Requests = [];
        public Task LoadHeadersAsync(IEnumerable<Game> games, CancellationToken cancellationToken = default)
        { Requests.AddRange(games.Select(game => (game, cancellationToken))); return Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
        public Task<System.Windows.Media.Imaging.BitmapImage?> LoadHeroAsync(int appId, CancellationToken cancellationToken = default) => Task.FromResult<System.Windows.Media.Imaging.BitmapImage?>(null);
        public Task LoadAsync(Game game, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task LoadManyAsync(IEnumerable<Game> games, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
    private sealed class Settings : ISettingsService
    {
        private readonly AppSettings _settings = new() { DiscordApplicationId = "42" };
        public AppSettings Load() => _settings;
        public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ResetAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
    private sealed class LaunchFixture : IGameLaunchService
    {
        public int Requests; public Game? LastGame;
        public Task<GameLaunchResult> LaunchAsync(Game game, CancellationToken token = default) { Requests++; LastGame = game; return Task.FromResult(new GameLaunchResult(true, "Started")); }
    }
    private sealed partial class SocialFixture : IDiscordSocialService, IDiscordChatService
    {
        public bool IsAvailable => true;
        public bool HasSavedSession => Snapshot.IsConnected;
        public bool HasCommunicationPermission => Snapshot.IsConnected;
        public string AvailabilityMessage => "Ready";
        public DiscordSnapshot Snapshot { get; private set; } = new(true, new(1, "me", "Me", null),
            [new(new(2, "two", "Two", null), DiscordRelationshipKind.Friend, DiscordPresence.Online), new(new(3, "three", "Three", null), DiscordRelationshipKind.Friend, DiscordPresence.Offline)]);
        public event EventHandler? Changed;
        public event EventHandler<DiscordMessageChange>? MessageChanged;
        public DiscordParty? Party { get; private set; }
        public IReadOnlyList<DiscordPartyInvite> Invites => [];
        public Func<ulong, bool, Task<IReadOnlyList<DiscordChatMessage>>> History = (_, _) => Task.FromResult<IReadOnlyList<DiscordChatMessage>>([]);
        public Func<CancellationToken, Task<IReadOnlyList<DiscordMessageSummary>>> Summaries = _ => Task.FromResult<IReadOnlyList<DiscordMessageSummary>>([]);
        public Exception? SendError;
        public int SendCalls;
        public TaskCompletionSource<DiscordChatMessage>? SendCompletion;
        public TaskCompletionSource? InviteCompletion;
        public ulong Invited;
        public List<string> PartyOperations = [];
        public void Notify(DiscordMessageChange change) => MessageChanged?.Invoke(this, change);
        public void ChangePresence(ulong id, DiscordPresence presence) { Snapshot = Snapshot with { Relationships = Snapshot.Relationships.Select(friend => friend.Id == id ? friend with { Presence = presence } : friend).ToArray() }; Changed?.Invoke(this, EventArgs.Empty); }
        public void SetParty(DiscordParty party) { Party = party; Changed?.Invoke(this, EventArgs.Empty); }
        public Task RestoreAsync(CancellationToken token = default) => Task.CompletedTask;
        public Task ConnectAsync(CancellationToken token = default) => Task.CompletedTask;
        public Task DisconnectAsync(CancellationToken token = default) { Snapshot = DiscordSnapshot.Disconnected; Party = null; Changed?.Invoke(this, EventArgs.Empty); return Task.CompletedTask; }
        public Task SendFriendRequestAsync(string usernameOrId, CancellationToken token = default) => Task.CompletedTask;
        public Task UpdateFriendAsync(DiscordFriend friend, DiscordFriendAction action, CancellationToken token = default) => Task.CompletedTask;
        public Task<IReadOnlyList<DiscordMessageSummary>> GetMessageSummariesAsync(CancellationToken token = default) => Summaries(token);
        public Task<IReadOnlyList<DiscordChatMessage>> GetMessagesAsync(ulong peerId, bool party, CancellationToken token = default) => History(peerId, party);
        public Task<DiscordChatMessage> SendMessageAsync(ulong peerId, bool party, string content, CancellationToken token = default)
        { SendCalls++; if (SendError is not null) throw SendError; return SendCompletion?.Task ?? Task.FromResult(Message(500, peerId, content) with { IsOwn = true }); }
        public List<(ulong PeerId, ulong MessageId, string Content)> Edits = [];
        public List<(ulong PeerId, ulong MessageId)> Deletes = [];
        public Exception? EditError;
        public Exception? DeleteError;
        public Task EditMessageAsync(ulong peerId, ulong messageId, string content, CancellationToken token = default)
        { if (EditError is not null) throw EditError; Edits.Add((peerId, messageId, content)); return Task.CompletedTask; }
        public Task DeleteMessageAsync(ulong peerId, ulong messageId, CancellationToken token = default)
        { if (DeleteError is not null) throw DeleteError; Deletes.Add((peerId, messageId)); return Task.CompletedTask; }
        public void SetShowingChat(bool showing) { }
        public Task CreatePartyAsync(DiscordPartyGame game, CancellationToken token = default) { Party = new(10, game, [Snapshot.Profile!]); Changed?.Invoke(this, EventArgs.Empty); return Task.CompletedTask; }
        public async Task InviteToPartyAsync(ulong friendId, CancellationToken token = default) { if (InviteCompletion is not null) await InviteCompletion.Task; Invited = friendId; }
        public Task AcceptPartyInviteAsync(Guid inviteId, CancellationToken token = default) { PartyOperations.Add("join"); Party = new(11, new(43, "New party"), []); Changed?.Invoke(this, EventArgs.Empty); return Task.CompletedTask; }
        public void DismissPartyInvite(Guid inviteId) { }
        public Task LeavePartyAsync(CancellationToken token = default) { PartyOperations.Add("leave"); Party = null; Changed?.Invoke(this, EventArgs.Empty); return Task.CompletedTask; }
    }
}
