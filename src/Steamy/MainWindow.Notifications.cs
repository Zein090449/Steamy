using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Steamy.ViewModels;
using Steamy.Services;

namespace Steamy;

public partial class MainWindow
{
    private NotificationCenterViewModel _notifications = null!;
    private DiscordFriendsViewModel _socialModel = null!;
    private DispatcherTimer _toastClock = null!;
    private Pages.FriendsPage? _friendsPage;
    private void InitializeNotificationWorkspace()
    {
        _notifications = App.Services.GetRequiredService<NotificationCenterViewModel>();
        if (DiscordIntegration.Enabled)
        {
            _socialModel = App.Services.GetRequiredService<DiscordFriendsViewModel>();
            VoiceCallHost.Content = new Controls.VoiceCallBar();
        }
        NotificationBell.DataContext = NotificationPanel.DataContext = NotificationToasts.DataContext = _notifications;
        _toastClock = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _toastClock.Tick += (_, _) => _notifications.ExpireToasts(DateTimeOffset.UtcNow);
        _notifications.Toasts.CollectionChanged += ToastsChanged;
        _notifications.Changed += NotificationStateChanged;
        _notifications.OpenRequested += OpenNotification;
        Activated += (_, _) => _socialModel?.SetWindowActive(true);
        Deactivated += (_, _) => _socialModel?.SetWindowActive(false);
        StateChanged += (_, _) => _socialModel?.SetWindowActive(IsActive && WindowState != WindowState.Minimized);
        SizeChanged += (_, _) => { NotificationCenterBorder.MaxHeight = Math.Clamp(ActualHeight - 80, 200, 540); };
        Closed += (_, _) =>
        {
            _toastClock.Stop(); NotificationsPopup.IsOpen = false;
            _notifications.Toasts.CollectionChanged -= ToastsChanged;
            _notifications.Changed -= NotificationStateChanged; _notifications.OpenRequested -= OpenNotification;
            _socialModel?.SetWindowActive(false);
            _friendsPage = null;
        };
        NotificationStateChanged(this, EventArgs.Empty);
        InitializeEasterEggs();
    }
    private static readonly System.Windows.Input.Key[] KonamiSequence = [System.Windows.Input.Key.Up, System.Windows.Input.Key.Up, System.Windows.Input.Key.Down, System.Windows.Input.Key.Down, System.Windows.Input.Key.Left, System.Windows.Input.Key.Right, System.Windows.Input.Key.Left, System.Windows.Input.Key.Right, System.Windows.Input.Key.B, System.Windows.Input.Key.A];
    private int _konamiProgress, _bellClicks;
    private DateTimeOffset _bellLastClick;
    private ISettingsService _eggSettingsService = null!;
    private Models.AppSettings _eggSettings = null!;
    public static event EventHandler? EasterEggsChanged;
    private bool IsEggFound(string key) => _eggSettings.EasterEggs.Contains(key, StringComparer.Ordinal);
    private bool RememberEgg(string key)
    {
        if (IsEggFound(key)) return false;
        _eggSettings.EasterEggs = [.. _eggSettings.EasterEggs, key];
        _ = _eggSettingsService.SaveAsync(_eggSettings);
        EasterEggsChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }
    public static int EasterEggCount => (App.Services.GetService<ISettingsService>()?.Load().EasterEggs.Length ?? 0);
    private void InitializeEasterEggs()
    {
        _eggSettingsService = App.Services.GetRequiredService<ISettingsService>();
        _eggSettings = _eggSettingsService.Load();
        PreviewKeyDown += (_, args) =>
        {
            if (args.IsRepeat || Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase) return;
            _konamiProgress = args.Key == KonamiSequence[_konamiProgress] ? _konamiProgress + 1
                : args.Key == KonamiSequence[0] ? 1 : 0;
            if (_konamiProgress != KonamiSequence.Length) return;
            _konamiProgress = 0;
            RememberEgg("konami");
            _notifications.Publish(new("egg:konami", SteamyNotificationKind.Information, "🎮 +30 lives unlocked", "The classic cheat code still works. This stays between us."), showToast: true);
            ThrowConfetti();
        };
        NotificationBell.MouseLeftButtonUp += (_, _) =>
        {
            var now = DateTimeOffset.UtcNow;
            _bellClicks = now - _bellLastClick < TimeSpan.FromSeconds(2) ? _bellClicks + 1 : 1;
            _bellLastClick = now;
            if (_bellClicks < 5) return;
            _bellClicks = 0;
            RememberEgg("bell");
            _notifications.Publish(new("egg:bell", SteamyNotificationKind.Information, "🔔 You found me!", "Five rings in two seconds — the bell never stood a chance."), showToast: true);
            WiggleBell();
        };
        NotificationBell.MouseEnter += (_, _) => { if (IsEggFound("konami")) SparkleBell(); };
    }
    private void WiggleBell()
    {
        var transform = new RotateTransform();
        NotificationBell.RenderTransform = transform;
        NotificationBell.RenderTransformOrigin = new Point(0.5, 0.5);
        var wiggle = new System.Windows.Media.Animation.DoubleAnimationUsingKeyFrames
        {
            Duration = TimeSpan.FromSeconds(0.9),
            KeyFrames =
            {
                new System.Windows.Media.Animation.LinearDoubleKeyFrame(0, TimeSpan.Zero),
                new System.Windows.Media.Animation.LinearDoubleKeyFrame(-14, TimeSpan.FromMilliseconds(120)),
                new System.Windows.Media.Animation.LinearDoubleKeyFrame(12, TimeSpan.FromMilliseconds(260)),
                new System.Windows.Media.Animation.LinearDoubleKeyFrame(-9, TimeSpan.FromMilliseconds(400)),
                new System.Windows.Media.Animation.LinearDoubleKeyFrame(7, TimeSpan.FromMilliseconds(540)),
                new System.Windows.Media.Animation.LinearDoubleKeyFrame(-4, TimeSpan.FromMilliseconds(680)),
                new System.Windows.Media.Animation.LinearDoubleKeyFrame(0, TimeSpan.FromMilliseconds(820))
            }
        };
        transform.BeginAnimation(RotateTransform.AngleProperty, wiggle);
    }
    private void SparkleBell()
    {
        var sparkle = new System.Windows.Media.Animation.DoubleAnimationUsingKeyFrames
        {
            Duration = TimeSpan.FromSeconds(0.7),
            KeyFrames =
            {
                new System.Windows.Media.Animation.LinearDoubleKeyFrame(1, TimeSpan.Zero),
                new System.Windows.Media.Animation.LinearDoubleKeyFrame(0.55, TimeSpan.FromMilliseconds(160)),
                new System.Windows.Media.Animation.LinearDoubleKeyFrame(1, TimeSpan.FromMilliseconds(340)),
                new System.Windows.Media.Animation.LinearDoubleKeyFrame(0.7, TimeSpan.FromMilliseconds(500)),
                new System.Windows.Media.Animation.LinearDoubleKeyFrame(1, TimeSpan.FromMilliseconds(680))
            }
        };
        NotificationBell.BeginAnimation(OpacityProperty, sparkle);
    }
    private void ThrowConfetti()
    {
        if (_eggSettings.ReduceEffects) return;
        var random = new Random();
        var colors = new Brush[] { Frozen("#f23f43"), Frozen("#23a55a"), Frozen("#f0b232"), Frozen("#5865f2"), Frozen("#00a8fc"), Frozen(Colors.White.ToString()) };
        for (var index = 0; index < 70; index++)
        {
            var piece = new System.Windows.Shapes.Rectangle
            {
                Width = 5 + random.Next(6), Height = 7 + random.Next(8), RadiusX = 1.5, RadiusY = 1.5,
                Fill = colors[random.Next(colors.Length)],
                RenderTransform = new RotateTransform(random.Next(360))
            };
            Canvas.SetLeft(piece, random.NextDouble() * Math.Max(1, ActualWidth));
            Canvas.SetTop(piece, -40 - random.Next(140));
            ConfettiLayer.Children.Add(piece);
            var duration = TimeSpan.FromSeconds(1.6 + random.NextDouble() * 1.4);
            var fall = new System.Windows.Media.Animation.DoubleAnimation(-40, ActualHeight + 60, duration) { EasingFunction = new System.Windows.Media.Animation.QuadraticEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseIn } };
            var spin = new System.Windows.Media.Animation.DoubleAnimation(0, 360 + random.Next(540), duration);
            fall.Completed += (_, _) => ConfettiLayer.Children.Remove(piece);
            piece.BeginAnimation(Canvas.TopProperty, fall);
            ((RotateTransform)piece.RenderTransform).BeginAnimation(RotateTransform.AngleProperty, spin);
        }
    }
    private static Brush Frozen(string color) { var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)); brush.Freeze(); return brush; }
    private void ToastsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs args)
    { if (_notifications.Toasts.Count > 0) _toastClock.Start(); else _toastClock.Stop(); }
    private void Notifications_Click(object sender, RoutedEventArgs args) => NotificationsPopup.IsOpen = !NotificationsPopup.IsOpen;
    private void NotificationStateChanged(object? sender, EventArgs args)
    {
        TaskbarItemInfo ??= new TaskbarItemInfo();
        if (_notifications.UnreadCount == 0) { TaskbarItemInfo.Overlay = null; return; }
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawEllipse(new SolidColorBrush(Color.FromRgb(0xb3, 0x26, 0x2a)), null, new Point(8, 8), 8, 8);
            drawing.DrawEllipse(null, new Pen(new SolidColorBrush(Color.FromRgb(0xf2, 0x3f, 0x43)), 1.4), new Point(8, 8), 8.7, 8.7);
            var text = new FormattedText(_notifications.UnreadCount > 9 ? "9+" : _notifications.UnreadCount.ToString(), CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI"), 9, Brushes.White, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            drawing.DrawText(text, new Point((16 - text.Width) / 2, (16 - text.Height) / 2));
        }
        var image = new DrawingImage(visual.Drawing); image.Freeze(); TaskbarItemInfo.Overlay = image;
    }
    private void OpenNotification(object? sender, SteamyNotification notification)
    {
        NotificationsPopup.IsOpen = false;
        if (notification.Content.Kind == SteamyNotificationKind.Download) { RootNavigation.Navigate(typeof(Pages.DownloadsPage)); return; }
        if (notification.Content.Kind == SteamyNotificationKind.Information) return;
        if (!DiscordIntegration.Enabled) return;
        OpenSocialDock();
        if (notification.Content.Kind == SteamyNotificationKind.FriendRequest) { _socialModel.OpenFriendRequests(); _friendsPage?.RevealFriends(); }
        else if (notification.Content.Kind == SteamyNotificationKind.PartyInvite) _friendsPage?.RevealParty();
        else if (notification.Content.LobbyId > 0)
        { if (_socialModel.Party?.Id == notification.Content.LobbyId) { _socialModel.OpenPartyChatCommand.Execute(null); _friendsPage?.RevealConversation(); } }
        else
        {
            _socialModel.SelectedFilter = "All"; _socialModel.SearchText = "";
            var friend = _socialModel.Friends.FirstOrDefault(person => person.Id == notification.Content.PeerId);
            if (friend is not null) _socialModel.SelectedFriend = friend;
            _friendsPage?.RevealConversation();
        }
    }
}
