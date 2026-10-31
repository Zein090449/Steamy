using CommunityToolkit.Mvvm.Input;
using Steamy.Services;

namespace Steamy.ViewModels;

public sealed partial class DiscordFriendsViewModel
{
    private DiscordChatMessage? _editingMessage;
    private string _draftBeforeEdit = "";
    private bool _savingEdit;
    public bool IsEditing => _editingMessage is not null;
    public string ComposerPlaceholder => IsEditing ? "Edit your message…" : "Write a message…";
    public string ComposerActionTooltip => IsEditing ? "Save changes · Enter. Esc cancels." : "Send · Enter. Shift + Enter for a new line.";
    public IRelayCommand<DiscordChatMessage> BeginEditMessageCommand { get; private set; } = null!;
    public IRelayCommand CancelMessageEditCommand { get; private set; } = null!;
    public IAsyncRelayCommand<DiscordChatMessage> DeleteMessageCommand { get; private set; } = null!;

    private void InitializeMessageEditing()
    {
        BeginEditMessageCommand = new RelayCommand<DiscordChatMessage>(message => BeginEditMessage(message),
            message => message is { CanManage: true } && HasCommunicationPermission);
        CancelMessageEditCommand = new RelayCommand(CancelMessageEdit, () => IsEditing);
        DeleteMessageCommand = new AsyncRelayCommand<DiscordChatMessage>(DeleteMessageAsync,
            message => message is { CanManage: true } && HasCommunicationPermission, AsyncRelayCommandOptions.AllowConcurrentExecutions);
    }

    private void BeginEditMessage(DiscordChatMessage? message)
    {
        if (message is not { CanManage: true } || !HasCommunicationPermission || ConversationId == 0
            || MessageConversation(message) != ConversationKey) return;
        if (_editingMessage?.Id == message.Id) return;
        _draftBeforeEdit = MessageDraft;
        _editingMessage = message;
        MessageDraft = message.Content;
        ChatStatus = "Editing your message. Press Esc to cancel.";
        EditingChanged();
    }

    private void CancelMessageEdit()
    {
        if (_editingMessage is null) return;
        var previous = _draftBeforeEdit;
        ResetMessageEdit();
        MessageDraft = previous;
        ChatStatus = "";
    }

    // Conversation switches discard the edit without restoring the draft the composer already replaced.
    private void ResetMessageEdit()
    {
        if (_editingMessage is null) return;
        _editingMessage = null; _draftBeforeEdit = "";
        EditingChanged();
    }

    private void EditingChanged()
    {
        OnPropertyChanged(nameof(IsEditing)); OnPropertyChanged(nameof(ComposerPlaceholder)); OnPropertyChanged(nameof(ComposerActionTooltip));
        BeginEditMessageCommand?.NotifyCanExecuteChanged(); CancelMessageEditCommand?.NotifyCanExecuteChanged(); SendMessageCommand?.NotifyCanExecuteChanged();
    }

    private async Task SaveEditAsync(DiscordChatMessage editing)
    {
        if (_chat is null || _savingEdit) return;
        var text = MessageDraft.Trim();
        if (text.Length is 0 or > 2000) return;
        if (text == editing.Content) { CancelMessageEdit(); ChatStatus = ""; return; }
        _savingEdit = true;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_outboxLifetime.Token); deadline.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            await _chat.EditMessageAsync(editing.PeerId, editing.Id, text, deadline.Token);
            var existing = Messages.FirstOrDefault(item => item.Id == editing.Id);
            if (existing is not null)
            {
                var updated = existing with { Content = text, IsEdited = true };
                UpsertMessage(updated);
                if (updated.LobbyId == 0) _conversations.Record(updated.PeerId, updated);
            }
            _editingMessage = null; _draftBeforeEdit = ""; MessageDraft = ""; _conversationDrafts.Remove(ConversationKey); ChatStatus = "";
            EditingChanged();
        }
        catch (Exception error)
        {
            ChatStatus = error is DiscordSocialException or InvalidOperationException or ArgumentException ? error.Message : "The edit wasn't saved. Check your connection and try again.";
        }
        finally { _savingEdit = false; RefreshWorkspaceCommands(); }
    }

    private async Task DeleteMessageAsync(DiscordChatMessage? message)
    {
        if (message is not { CanManage: true } || _chat is null || ConversationId == 0 || MessageConversation(message) != ConversationKey) return;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_outboxLifetime.Token); deadline.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            await _chat.DeleteMessageAsync(message.PeerId, message.Id, deadline.Token);
            RemoveDeletedMessage(message);
            ChatStatus = "";
        }
        catch (Exception error)
        {
            ChatStatus = error is DiscordSocialException or InvalidOperationException or ArgumentException ? error.Message : "The message couldn't be deleted. Try again.";
        }
    }

    private void RemoveDeletedMessage(DiscordChatMessage message)
    {
        RememberDeletion(message.Id);
        var existing = Messages.FirstOrDefault(item => item.Id == message.Id);
        if (existing is not null) Messages.Remove(existing);
        if (message.LobbyId == 0) _conversations.Forget(message.PeerId, message.Id);
        if (_editingMessage?.Id == message.Id) ResetMessageEdit();
        OnPropertyChanged(nameof(ChatEmptyVisibility)); OnPropertyChanged(nameof(ProfileCardVisibility)); OnPropertyChanged(nameof(FirstUnreadId));
    }

    private void RememberDeletion(ulong messageId)
    {
        _deletedMessages.Add(messageId);
        if (_deletedMessages.Count > 2048) _deletedMessages.Remove(_deletedMessages.Min());
    }
}
