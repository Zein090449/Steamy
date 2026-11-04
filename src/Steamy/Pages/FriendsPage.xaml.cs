using System.Windows.Controls;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Globalization;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows.Media;
using Steamy.Controls;
using Microsoft.Extensions.DependencyInjection;
using Steamy.ViewModels;
using Steamy.Services;

namespace Steamy.Pages;

public partial class FriendsPage : Page
{
    private ScrollViewer? _messageScroll;
    private bool _followLatest = true;
    private bool _tailQueued;
    private string? _conversationKey;
    private bool _scrollingToLatest;
    private bool _userScrolling;
    private bool _restorePending, _restoreQueued, _restoring;
    private int _restoreGeneration;
    public FriendsPage() : this(App.Services.GetRequiredService<DiscordFriendsViewModel>()) { }
    internal FriendsPage(DiscordFriendsViewModel model, bool docked = false)
    {
        InitializeComponent(); DataContext = model;
        QuickSwitcherBox.TextChanged += (_, _) => FilterQuickSwitcher();
        InitializeDock(docked);
        SizeChanged += (_, _) =>
        {
            if (IsDocked) { UpdateDockLayout(); return; }
            var contacts = Math.Clamp(ActualWidth * .25, 240, 300);
            SocialWorkspace.ColumnDefinitions[0].Width = new GridLength(contacts);
            PartyColumn.Width = PartyGap.Width = new GridLength(0);
            PartyPane.Visibility = Visibility.Collapsed;
            Resources["SocialMessageMaxWidth"] = Math.Clamp((ActualWidth - contacts - 80) * .72, 260, 600);
        };
        Loaded += (_, _) =>
        {
            UpdateChatExposure(); model.Messages.CollectionChanged += MessagesChanged; model.PropertyChanged += ConversationChanged; model.ConversationChanging += BeforeConversationChanges;
            ((GifPickerViewModel)ConversationGifPicker.DataContext).Picked += GifPicked;
            ((StickerFavoritesViewModel)ConversationStickerPicker.DataContext).Picked += StickerPicked;
            ConversationEmojiPicker.Picked += EmojiPicked;
            ChatMessages.ApplyTemplate(); _messageScroll = FindScroll(ChatMessages);
            if (_messageScroll is not null)
            {
                SmoothScroll.SetEnabled(_messageScroll, true); _messageScroll.ScrollChanged += ChatScrolled;
                _messageScroll.AddHandler(Mouse.PreviewMouseWheelEvent, new MouseWheelEventHandler(UserWheel), true);
                _messageScroll.AddHandler(Keyboard.PreviewKeyDownEvent, new KeyEventHandler(UserScrollKey), true);
                _messageScroll.AddHandler(Mouse.PreviewMouseDownEvent, new MouseButtonEventHandler(UserScrollbar), true);
            }
            _conversationKey = model.ConversationKey; PrepareReadPosition(model);
        };
        Unloaded += (_, _) =>
        {
            SaveReadPosition(model); _restoreGeneration++; _restorePending = false;
            model.SetChatVisible(false); model.Messages.CollectionChanged -= MessagesChanged; model.PropertyChanged -= ConversationChanged; model.ConversationChanging -= BeforeConversationChanges;
            ((GifPickerViewModel)ConversationGifPicker.DataContext).Picked -= GifPicked;
            ((StickerFavoritesViewModel)ConversationStickerPicker.DataContext).Picked -= StickerPicked;
            ConversationEmojiPicker.Picked -= EmojiPicked;
            GifPickerPopup.IsOpen = false; StickerFavoritesPopup.IsOpen = false; EmojiPickerPopup.IsOpen = false; ProfilePopup.IsOpen = false; QuickSwitcherPopup.IsOpen = false;
            if (_messageScroll is not null)
            {
                _messageScroll.ScrollChanged -= ChatScrolled;
                _messageScroll.RemoveHandler(Mouse.PreviewMouseWheelEvent, new MouseWheelEventHandler(UserWheel));
                _messageScroll.RemoveHandler(Keyboard.PreviewKeyDownEvent, new KeyEventHandler(UserScrollKey));
                _messageScroll.RemoveHandler(Mouse.PreviewMouseDownEvent, new MouseButtonEventHandler(UserScrollbar));
            }
            _messageScroll = null; CompactPartyPopup.IsOpen = false;
        };
    }
    private void MessagesChanged(object? sender, NotifyCollectionChangedEventArgs args)
    { if (_restorePending) QueueReadPosition(); else if (_followLatest) QueueLatest(); else JumpLatestButton.Visibility = Visibility.Visible; }
    private void ConversationChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (sender is not DiscordFriendsViewModel model) return;
        if (IsDocked && args.PropertyName == nameof(model.ConnectedVisibility)) UpdateDockLayout();
        if (args.PropertyName == nameof(model.ConversationKey) && model.ConversationKey != _conversationKey)
        { _conversationKey = model.ConversationKey; PrepareReadPosition(model); }
        else if (args.PropertyName == nameof(model.LoadingChatVisibility) && _restorePending) QueueReadPosition();
    }
    private void BeforeConversationChanges(object? sender, EventArgs args)
    {
        if (sender is not DiscordFriendsViewModel model || _restoring || _restorePending) return;
        SaveReadPosition(model); _restorePending = true; _restoreGeneration++;
    }
    private void SaveReadPosition(DiscordFriendsViewModel model)
    {
        if (_messageScroll is null || _conversationKey is null || _restorePending || _restoring) return;
        var anchor = VisibleMessages(ChatMessages).Select(item => (Item: item, Top: item.TranslatePoint(new Point(), _messageScroll).Y))
            .Where(item => item.Item.ActualHeight > 0 && item.Top + item.Item.ActualHeight > 1 && item.Top < _messageScroll.ViewportHeight)
            .OrderBy(item => item.Top).FirstOrDefault();
        model.RememberReadPosition(_conversationKey, new(_followLatest, _messageScroll.VerticalOffset,
            (anchor.Item?.DataContext as DiscordChatMessage)?.Id ?? 0, anchor.Top));
    }
    private void PrepareReadPosition(DiscordFriendsViewModel model)
    {
        _restoreGeneration++; _restorePending = true; _userScrolling = false;
        _followLatest = model.ReadPosition(model.ConversationKey)?.FollowLatest ?? true; QueueReadPosition();
    }
    private void QueueReadPosition()
    {
        if (_restoreQueued || !_restorePending) return; _restoreQueued = true;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() =>
        {
            _restoreQueued = false;
            if (!_restorePending || _messageScroll is null || DataContext is not DiscordFriendsViewModel model || model.LoadingChatVisibility == Visibility.Visible) return;
            var position = model.ReadPosition(model.ConversationKey);
            if (position is null || position.FollowLatest) { _restorePending = false; _followLatest = true; QueueLatest(); return; }
            var generation = _restoreGeneration; _restoring = true; _followLatest = false;
            var anchor = model.Messages.FirstOrDefault(message => message.Id == position.AnchorId);
            if (anchor is not null) { ChatMessages.ScrollIntoView(anchor); ChatMessages.UpdateLayout(); }
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() =>
            {
                if (generation != _restoreGeneration || _messageScroll is null) { _restoring = false; return; }
                var item = VisibleMessages(ChatMessages).FirstOrDefault(item => item.DataContext is DiscordChatMessage message && message.Id == position.AnchorId);
                var offset = item is null ? position.Offset : _messageScroll.VerticalOffset + item.TranslatePoint(new Point(), _messageScroll).Y - position.AnchorTop;
                _messageScroll.ScrollToVerticalOffset(Math.Clamp(offset, 0, _messageScroll.ScrollableHeight));
                _restorePending = false; _restoring = false; JumpLatestButton.Visibility = Visibility.Visible;
            }));
        }));
    }
    private static IEnumerable<ListBoxItem> VisibleMessages(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        { var child = VisualTreeHelper.GetChild(parent, i); if (child is ListBoxItem item) yield return item; else foreach (var nested in VisibleMessages(child)) yield return nested; }
    }
    private void QueueLatest()
    {
        if (_tailQueued) return; _tailQueued = true;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() =>
        {
            _tailQueued = false;
            if (!_followLatest || _restorePending || _messageScroll is null) return;
            _scrollingToLatest = true;
            _userScrolling = false;
            _messageScroll.ScrollToEnd(); JumpLatestButton.Visibility = Visibility.Collapsed;
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ContextIdle, new Action(() => _scrollingToLatest = false));
        }));
    }
    private void ChatScrolled(object sender, ScrollChangedEventArgs args)
    {
        if (_messageScroll is null || _restoring || _restorePending) return;
        if (args.ExtentHeightChange != 0 || args.ViewportHeightChange != 0) { if (_followLatest) QueueLatest(); return; }
        if (_scrollingToLatest) return;
        if (!_userScrolling) { if (_followLatest && _messageScroll.ScrollableHeight - _messageScroll.VerticalOffset > 5) QueueLatest(); return; }
        _followLatest = _messageScroll.ScrollableHeight - _messageScroll.VerticalOffset <= 60;
        if (_followLatest) _userScrolling = false;
        JumpLatestButton.Visibility = _followLatest ? Visibility.Collapsed : Visibility.Visible;
    }
    private void UserWheel(object sender, MouseWheelEventArgs args)
    { CancelReadRestoration(); _userScrolling = true; if (args.Delta > 0) _followLatest = false; }
    private void CancelReadRestoration() { _restoreGeneration++; _restorePending = _restoring = false; }
    private void UserScrollKey(object sender, KeyEventArgs args)
    {
        if (args.Key is Key.Up or Key.Down or Key.PageUp or Key.PageDown or Key.Home or Key.End) CancelReadRestoration();
        if (args.Key is Key.Up or Key.PageUp or Key.Home) { _userScrolling = true; _followLatest = false; }
        else if (args.Key is Key.Down or Key.PageDown or Key.End) _userScrolling = true;
    }
    private void UserScrollbar(object sender, MouseButtonEventArgs args)
    {
        var source = args.OriginalSource as DependencyObject;
        while (source is not null && source != _messageScroll)
        { if (source is System.Windows.Controls.Primitives.ScrollBar) { CancelReadRestoration(); _userScrolling = true; _followLatest = false; break; } source = source is Visual ? VisualTreeHelper.GetParent(source) : LogicalTreeHelper.GetParent(source); }
    }
    private void JumpLatest_Click(object sender, RoutedEventArgs args) { CancelReadRestoration(); _followLatest = true; QueueLatest(); }
    private static ScrollViewer? FindScroll(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        { var child = VisualTreeHelper.GetChild(parent, i); if (child is ScrollViewer viewer) return viewer; if (FindScroll(child) is { } nested) return nested; }
        return null;
    }
    private void Composer_PreviewKeyDown(object sender, KeyEventArgs args)
    {
        if (args.Key == Key.Escape && DataContext is DiscordFriendsViewModel { IsEditing: true } editing)
        { editing.CancelMessageEditCommand.Execute(null); args.Handled = true; return; }
        if (args.Key != Key.Enter || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) return;
        if (DataContext is DiscordFriendsViewModel model && model.SendMessageCommand.CanExecute(null)) model.SendMessageCommand.Execute(null);
        args.Handled = true;
    }
    private void EditMessage_Click(object sender, RoutedEventArgs args)
    {
        if (sender is not FrameworkElement { DataContext: DiscordChatMessage message } || DataContext is not DiscordFriendsViewModel model) return;
        model.BeginEditMessageCommand.Execute(message);
        Dispatcher.BeginInvoke(() => { MessageComposer.Focus(); MessageComposer.CaretIndex = MessageComposer.Text.Length; });
    }
    private void DeleteMessage_Click(object sender, RoutedEventArgs args)
    {
        if (sender is not FrameworkElement { DataContext: DiscordChatMessage message } || DataContext is not DiscordFriendsViewModel model) return;
        var answer = MessageBox.Show(Window.GetWindow(this), "Delete this message for everyone?", "Delete message",
            MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (answer == MessageBoxResult.Yes) _ = model.DeleteMessageCommand.ExecuteAsync(message);
    }
    private void CancelMessageEdit_Click(object sender, RoutedEventArgs args)
    {
        (DataContext as DiscordFriendsViewModel)?.CancelMessageEditCommand.Execute(null);
        MessageComposer.Focus();
        MessageComposer.CaretIndex = MessageComposer.Text.Length;
    }
    private void CompactParty_Click(object sender, RoutedEventArgs args)
    { if (sender is UIElement target) CompactPartyPopup.PlacementTarget = target; CompactPartyPopup.IsOpen = !CompactPartyPopup.IsOpen; }
    private void GifPicker_Click(object sender, RoutedEventArgs args) => GifPickerPopup.IsOpen = !GifPickerPopup.IsOpen;
    private void StickerFavorites_Click(object sender, RoutedEventArgs args)
    {
        if (StickerFavoritesPopup.IsOpen) { StickerFavoritesPopup.IsOpen = false; return; }
        ((StickerFavoritesViewModel)ConversationStickerPicker.DataContext).Prepare();
        StickerFavoritesPopup.IsOpen = true;
        ConversationStickerPicker.FocusInput();
    }
    private void EmojiPicker_Click(object sender, RoutedEventArgs args)
    {
        if (DataContext is DiscordFriendsViewModel model) ConversationEmojiPicker.Recents = model.RecentEmojis;
        EmojiPickerPopup.IsOpen = !EmojiPickerPopup.IsOpen;
    }
    private bool _composerPlusWasOpen;
    private void ComposerPlus_PreviewMouseDown(object sender, MouseButtonEventArgs args) => _composerPlusWasOpen = ComposerPlusPopup.IsOpen;
    private void ComposerPlus_Click(object sender, RoutedEventArgs args)
    {
        if (_composerPlusWasOpen) { ComposerPlusPopup.IsOpen = false; return; }
        ComposerPlusPopup.IsOpen = true;
    }
    private void ComposerUpload_Click(object sender, RoutedEventArgs args)
    {
        ComposerPlusPopup.IsOpen = false;
        AttachFile_Click(sender, args);
    }
    private void ComposerGif_Click(object sender, RoutedEventArgs args)
    {
        ComposerPlusPopup.IsOpen = false;
        GifPicker_Click(GifPickerButton, args);
    }
    private void ComposerSticker_Click(object sender, RoutedEventArgs args)
    {
        ComposerPlusPopup.IsOpen = false;
        StickerFavorites_Click(StickerFavoritesButton, args);
    }
    private void ComposerEmoji_Click(object sender, RoutedEventArgs args)
    {
        ComposerPlusPopup.IsOpen = false;
        EmojiPicker_Click(EmojiPickerButton, args);
    }
    private void EmojiPicked(object? sender, string emoji)
    {
        if (DataContext is not DiscordFriendsViewModel model || emoji.Length == 0) return;
        var draft = model.MessageDraft;
        if (draft.Length + emoji.Length > 2000) return;
        var insertAt = Math.Clamp(MessageComposer.CaretIndex, 0, draft.Length);
        model.MessageDraft = draft[..insertAt] + emoji + draft[insertAt..];
        MessageComposer.CaretIndex = insertAt + emoji.Length;
        MessageComposer.Focus();
        model.RememberEmoji(emoji);
        ConversationEmojiPicker.Recents = model.RecentEmojis;
    }
    private void ProfilePanel_Click(object sender, RoutedEventArgs args)
    {
        if (DataContext is not DiscordFriendsViewModel model || model.SelectedFriend is null) { ProfilePopup.IsOpen = false; return; }
        ProfilePopup.IsOpen = !ProfilePopup.IsOpen;
    }
    private void ProfileMessage_Click(object sender, RoutedEventArgs args)
    {
        ProfilePopup.IsOpen = false;
        MessageComposer.Focus();
        MessageComposer.CaretIndex = (DataContext as DiscordFriendsViewModel)?.MessageDraft.Length ?? 0;
    }
    private void CopyRowMessage_Click(object sender, RoutedEventArgs args)
    {
        if (sender is not Button { CommandParameter: string text } || text.Length == 0) return;
        try { Clipboard.SetText(text); }
        catch (System.Runtime.InteropServices.ExternalException) { MessageBox.Show(Window.GetWindow(this), "The clipboard is busy. Try copying again.", "Copy message", MessageBoxButton.OK, MessageBoxImage.Information); }
    }
    protected override void OnPreviewKeyDown(KeyEventArgs args)
    {
        base.OnPreviewKeyDown(args);
        if (args.Key == Key.K && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            if (!QuickSwitcherPopup.IsOpen) OpenQuickSwitcher();
            args.Handled = true;
        }
        else if (args.Key == Key.Escape && QuickSwitcherPopup.IsOpen) { QuickSwitcherPopup.IsOpen = false; args.Handled = true; }
    }
    private void OpenQuickSwitcher()
    {
        if (DataContext is not DiscordFriendsViewModel model) return;
        QuickSwitcherBox.Text = "";
        QuickSwitcherList.ItemsSource = model.Friends;
        QuickSwitcherPopup.IsOpen = true;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, new Action(() => { QuickSwitcherBox.Focus(); QuickSwitcherBox.SelectAll(); }));
    }
    private void FilterQuickSwitcher()
    {
        if (DataContext is not DiscordFriendsViewModel model) return;
        var query = QuickSwitcherBox.Text.Trim();
        QuickSwitcherList.ItemsSource = query.Length == 0 ? model.Friends
            : model.Friends.Where(friend => friend.DisplayName.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                || friend.Username.Contains(query, StringComparison.OrdinalIgnoreCase) || friend.Id.ToString().Contains(query, StringComparison.Ordinal)).ToArray();
        if (QuickSwitcherList.Items.Count > 0) QuickSwitcherList.SelectedIndex = 0;
    }
    private void QuickSwitcherPick()
    {
        if (DataContext is DiscordFriendsViewModel model && QuickSwitcherList.SelectedItem is DiscordFriend friend) model.SelectedFriend = friend;
        QuickSwitcherPopup.IsOpen = false;
    }
    private void QuickSwitcherList_KeyDown(object sender, KeyEventArgs args)
    {
        if (args.Key == Key.Enter) { QuickSwitcherPick(); args.Handled = true; }
        else if (args.Key == Key.Escape) { QuickSwitcherPopup.IsOpen = false; args.Handled = true; }
    }
    private void QuickSwitcherList_DoubleClick(object sender, MouseButtonEventArgs args) => QuickSwitcherPick();
    internal bool CloseTransientPanels()
    {
        if (QuickSwitcherPopup.IsOpen) { QuickSwitcherPopup.IsOpen = false; return true; }
        if (ComposerPlusPopup.IsOpen) { ComposerPlusPopup.IsOpen = false; return true; }
        if (EmojiPickerPopup.IsOpen) { EmojiPickerPopup.IsOpen = false; return true; }
        if (GifPickerPopup.IsOpen) { GifPickerPopup.IsOpen = false; return true; }
        if (StickerFavoritesPopup.IsOpen) { StickerFavoritesPopup.IsOpen = false; return true; }
        if (ProfilePopup.IsOpen) { ProfilePopup.IsOpen = false; return true; }
        if (CompactPartyPopup.IsOpen) { CompactPartyPopup.IsOpen = false; return true; }
        return false;
    }
    private void GifPicked(object? sender, string link)
    {
        if (DataContext is DiscordFriendsViewModel model) model.AttachMedia("GIF", link);
        GifPickerPopup.IsOpen = false; MessageComposer.Focus();
    }
    private void StickerPicked(object? sender, string link)
    {
        if (DataContext is DiscordFriendsViewModel model) model.AttachMedia("Sticker", link);
        StickerFavoritesPopup.IsOpen = false; MessageComposer.Focus();
    }
    private void AttachFile_Click(object sender, RoutedEventArgs args)
    {
        if (DataContext is not DiscordFriendsViewModel model || model.IsEditing) return;
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Attach a file",
            Filter = "Images|*.png;*.jpg;*.jpeg;*.gif;*.webp;*.bmp;*.ico|All files|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        model.AttachFile(dialog.FileName);
        MessageComposer.Focus();
        MessageComposer.CaretIndex = MessageComposer.Text.Length;
    }
    internal void RevealParty() => Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() =>
    {
        if (PartyPane.Visibility == Visibility.Collapsed) CompactPartyPopup.IsOpen = true;
        else if (PartyPane.Child is SocialPartyPanel panel) panel.ShowInvitations();
    }));
    private void AccountMenu_Click(object sender, RoutedEventArgs args)
    {
        if (DataContext is not DiscordFriendsViewModel model || sender is not FrameworkElement button) return;
        var menu = new ContextMenu { PlacementTarget = button };
        menu.Items.Add(new MenuItem { Header = "Disconnect Discord", Command = model.DisconnectCommand });
        menu.IsOpen = true;
    }
    private void ConversationMenu_Click(object sender, RoutedEventArgs args)
    {
        if (DataContext is not DiscordFriendsViewModel model || sender is not FrameworkElement button) return;
        var menu = new ContextMenu();
        menu.Items.Add(new MenuItem { Header = "Reload conversation", Command = model.ReloadChatCommand });
        if (model.Party is not null) menu.Items.Add(new MenuItem { Header = "Leave party", Command = model.LeavePartyCommand });
        if (model.SelectedFriend is { } friend)
        {
            menu.Items.Add(new MenuItem { Header = "Open in Discord", Command = model.OpenDiscordMessageCommand, CommandParameter = model.Messages.LastOrDefault() });
            menu.Items.Add(new Separator());
            menu.Items.Add(new MenuItem { Header = "Remove friend", Command = model.RemoveCommand, CommandParameter = friend });
            menu.Items.Add(new MenuItem { Header = "Block on Discord", Command = model.BlockCommand, CommandParameter = friend });
        }
        menu.PlacementTarget = button; menu.IsOpen = true;
    }
    private void CopyMessage_Click(object sender, RoutedEventArgs args)
    {
        if (sender is not MenuItem { CommandParameter: string text } || text.Length == 0) return;
        try { Clipboard.SetText(text); }
        catch (System.Runtime.InteropServices.ExternalException) { MessageBox.Show(Window.GetWindow(this), "The clipboard is busy. Try copying again.", "Copy message", MessageBoxButton.OK, MessageBoxImage.Information); }
    }
    private void OnlineFriendChip_Click(object sender, RoutedEventArgs args)
    {
        if (sender is System.Windows.Controls.Button { DataContext: DiscordFriend friend } && DataContext is DiscordFriendsViewModel model) model.SelectedFriend = friend;
    }
}

public sealed class SocialFilterConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => Equals(value, parameter);
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is true ? parameter : Binding.DoNothing;
}
public sealed class SocialDayConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is DateTime day ? Services.SocialTime.Day(day) : "";
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
public sealed class SocialUnreadConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    { var count = values.Length >= 2 && values[0] is ulong id && values[1] is DiscordFriendsViewModel model ? model.UnreadMessages(id) : 0; return count == 0 ? "" : count > 99 ? "99+" : count.ToString(); }
    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => targetTypes.Select(_ => Binding.DoNothing).ToArray();
}
public sealed class SocialFilterCountConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        var filter = values.Length > 0 ? values[0]?.ToString() ?? "" : "";
        var count = values.Length > 1 && values[1] is DiscordFriendsViewModel model ? model.FilterCount(filter) : 0;
        return count > 0 ? $"{filter} {count}" : filter;
    }
    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => targetTypes.Select(_ => Binding.DoNothing).ToArray();
}
public sealed class SocialConversationPreviewConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 4 || values[0] is not ulong id || values[1] is not DiscordFriendsViewModel model) return "";
        var entry = model.Conversation(id);
        // Summaries carry only the last message ID, so a synced conversation without a local
        // preview falls back to the friend's detail line instead of showing an empty row.
        if (entry is null || entry.LastMessageId == 0 || entry.Preview.Length == 0) return values[3] as string ?? "";
        return (entry.FromMe ? "You: " : "") + entry.Preview;
    }
    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => targetTypes.Select(_ => Binding.DoNothing).ToArray();
}
public sealed class SocialConversationTimeConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 2 || values[0] is not ulong id || values[1] is not DiscordFriendsViewModel model) return "";
        var entry = model.Conversation(id);
        return entry is null || entry.LastMessageAt <= 0 ? "" : Services.SocialTime.Relative(DateTimeOffset.FromUnixTimeMilliseconds(entry.LastMessageAt));
    }
    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => targetTypes.Select(_ => Binding.DoNothing).ToArray();
}
public sealed class SocialUnreadWeightConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        var unread = values.Length >= 2 && values[0] is ulong id && values[1] is DiscordFriendsViewModel model ? model.UnreadMessages(id) : 0;
        return unread > 0 ? FontWeights.SemiBold : FontWeights.Normal;
    }
    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => targetTypes.Select(_ => Binding.DoNothing).ToArray();
}
public sealed class SocialEqualToVisibilityConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) => values.Length >= 2 && Equals(values[0], values[1]) ? Visibility.Visible : Visibility.Collapsed;
    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => targetTypes.Select(_ => Binding.DoNothing).ToArray();
}
public sealed class SocialMemberSinceConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is ulong id && id > 0 ? Services.SocialTime.MemberSince(id) : "";
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
public sealed class SocialPresenceColorConverter : IValueConverter
{
    private static readonly Brush Online = Frozen(0x23, 0xa5, 0x5a), Idle = Frozen(0xf0, 0xb2, 0x32), Dnd = Frozen(0xf2, 0x3f, 0x43), Streaming = Frozen(0x59, 0x36, 0x95), Offline = Frozen(0x80, 0x84, 0x8e);
    private static SolidColorBrush Frozen(byte r, byte g, byte b) { var brush = new SolidColorBrush(Color.FromRgb(r, g, b)); brush.Freeze(); return brush; }
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is Services.DiscordPresence presence ? presence switch
    {
        Services.DiscordPresence.Online => Online, Services.DiscordPresence.Idle => Idle, Services.DiscordPresence.Dnd => Dnd,
        Services.DiscordPresence.Streaming => Streaming, _ => Offline
    } : Offline;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
public sealed class SocialInverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is true ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
