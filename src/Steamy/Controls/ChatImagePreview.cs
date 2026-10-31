using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Steamy.Services;
using XamlAnimatedGif;

namespace Steamy.Controls;

/// <summary>Visible-only media, bounded animated GIFs, manual reduced-motion playback and an animated viewer.</summary>
public sealed class ChatImagePreview : Border
{
    public static readonly DependencyProperty UrlProperty = DependencyProperty.Register(nameof(Url), typeof(string), typeof(ChatImagePreview), new PropertyMetadata(null, (d, _) => ((ChatImagePreview)d).Reset()));
    public string? Url { get => (string?)GetValue(UrlProperty); set => SetValue(UrlProperty, value); }
    public bool IsViewer { get; set; }
    internal static Func<Uri, CancellationToken, Task<ChatMedia>> MediaLoader = ChatMediaLoader.LoadAsync;
    internal static Func<Window, bool> WindowIsActive = static window => window.IsActive;
    private CancellationTokenSource? _request;
    private ChatMedia? _media;
    private Image? _image;
    private MemoryStream? _animation;
    private Button? _play;
    private bool _attempted, _manual, _playing, _pausedByUser;
    private int _offscreenTicks;
    private static readonly HashSet<ChatImagePreview> Active = [];
    private static DispatcherTimer? _clock;
    internal bool IsPlaying => _playing;
    internal bool HasMedia => _media is not null;
    private System.Windows.Controls.MediaElement? _video;
    public ChatImagePreview()
    {
        Visibility = Visibility.Collapsed; CornerRadius = new(10); Height = 200; MinWidth = 180; MaxWidth = 320;
        ClipToBounds = true; Margin = new(0, 8, 0, 4);
        Loaded += (_, _) => { Active.Add(this); EnsureClock(); Reset(); };
        Unloaded += (_, _) => { Active.Remove(this); Release(); if (Active.Count == 0) _clock?.Stop(); };
        IsVisibleChanged += (_, _) => { if (!IsVisible) SetPlayback(false); };
        MouseLeftButtonUp += (_, args) => { if (_media is not null && !IsViewer) { OpenViewer(); args.Handled = true; } };
    }
    private static void EnsureClock()
    {
        if (_clock is null)
        {
            _clock = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(180) };
            _clock.Tick += (_, _) => RefreshVisibleMedia();
            MotionPreferences.Changed += (_, _) => { foreach (var view in Active) view._manual = false; RefreshVisibleMedia(); };
        }
        _clock.Start();
    }
    internal static void RefreshVisibleMedia()
    {
        var playing = 0;
        foreach (var view in Active.ToArray())
        {
            var visible = view.InViewport();
            if (visible) { view._offscreenTicks = 0; view.EnsureMedia(); }
            else if (view._media is null && view._request is not null || ++view._offscreenTicks > 12 && view._media is not null) { view.Release(); view._attempted = false; }
            var shouldPlay = visible && !view._pausedByUser && view._media is { } media && (media.Animated || media.IsVideo)
                && (MotionPreferences.AnimationsEnabled || view._manual) && playing < 4;
            if (shouldPlay) playing++;
            view.SetPlayback(shouldPlay);
        }
    }
    private bool InViewport()
    {
        var window = Window.GetWindow(this) ?? Application.Current?.MainWindow;
        if (!IsLoaded || !IsVisible || ActualWidth <= 0 || ActualHeight <= 0 || window?.WindowState == WindowState.Minimized || window is not null && !WindowIsActive(window) || PresentationSource.FromVisual(this) is null) return false;
        var bounds = new Rect(RenderSize); DependencyObject current = this;
        while (VisualTreeHelper.GetParent(current) is { } parent)
        {
            if (parent is FrameworkElement element && (element is ScrollViewer || element == window))
            {
                try { if (!TransformToAncestor(element).TransformBounds(bounds).IntersectsWith(new Rect(element.RenderSize))) return false; }
                catch (InvalidOperationException) { return false; }
            }
            current = parent;
        }
        return true;
    }
    private void Reset()
    {
        Release(); _attempted = false; _manual = false; _pausedByUser = false;
        Visibility = string.IsNullOrWhiteSpace(Url) ? Visibility.Collapsed : Visibility.Visible;
        Child = Placeholder("Image");
        if (InViewport()) EnsureMedia();
    }
    private async void EnsureMedia()
    {
        if (_attempted || !Uri.TryCreate(Url, UriKind.Absolute, out var uri) || !ChatMediaPolicy.IsMediaReference(uri)) return;
        _attempted = true; var request = _request = new CancellationTokenSource(); Child = Placeholder("Loading media…");
        try
        {
            var media = await MediaLoader(uri, request.Token);
            if (request.IsCancellationRequested || _request != request) return;
            _media = media; var image = _image = new Image { Source = media.Poster, Stretch = Stretch.Uniform };
            var surface = new Grid(); surface.Children.Add(image); Child = surface; Cursor = Cursors.Hand;
            var expand = ActionButton("↗", "Open media viewer", HorizontalAlignment.Right);
            if (!IsViewer) { expand.Click += (_, args) => { OpenViewer(); args.Handled = true; }; surface.Children.Add(expand); }
            if (media.IsVideo && media.VideoUrl is { } source)
            {
                _video = new System.Windows.Controls.MediaElement
                {
                    Source = source, LoadedBehavior = System.Windows.Controls.MediaState.Manual,
                    UnloadedBehavior = System.Windows.Controls.MediaState.Close, IsMuted = true, Stretch = Stretch.Uniform,
                    ScrubbingEnabled = false, Volume = 0
                };
                _video.MediaEnded += (_, _) => { if (_video is not null) { _video.Position = TimeSpan.Zero; _video.Play(); } };
                _video.MediaFailed += (_, _) => { if (_video is not null) _video.Visibility = Visibility.Collapsed; };
                surface.Children.Insert(0, _video);
                image.Visibility = Visibility.Collapsed;
                _play = ActionButton("Play", "Play or pause the clip", HorizontalAlignment.Left);
                _play.Click += (_, args) => { _pausedByUser = _playing; _manual = !_playing; SetPlayback(_manual && InViewport()); args.Handled = true; };
                surface.Children.Add(_play);
            }
            else if (media.Animated)
            {
                _play = ActionButton("GIF · Play", "Play or pause GIF", HorizontalAlignment.Left);
                _play.Click += (_, args) => { _pausedByUser = _playing; _manual = !_playing; SetPlayback(_manual && InViewport()); args.Handled = true; };
                surface.Children.Add(_play);
                // AutoStart avoids the library's ShowFirstFrame pause, which lands after our first Play call and freezes the loop.
                AnimationBehavior.SetAutoStart(image, true); AnimationBehavior.SetCacheFramesInMemory(image, false);
                AnimationBehavior.SetRepeatBehavior(image, System.Windows.Media.Animation.RepeatBehavior.Forever);
                AnimationBehavior.AddLoadedHandler(image, AnimationLoaded);
                AnimationBehavior.AddErrorHandler(image, AnimationFailed);
                AnimationBehavior.AddAnimationCompletedHandler(image, AnimationCompleted);
                _animation = new MemoryStream(media.Bytes, false); AnimationBehavior.SetSourceStream(image, _animation);
            }
            RefreshVisibleMedia();
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (_request != request || request.IsCancellationRequested) return;
            var retry = ActionButton(error is IOException ? error.Message : "Image unavailable · Retry", "Retry loading media", HorizontalAlignment.Center);
            retry.VerticalAlignment = VerticalAlignment.Center;
            retry.Click += (_, args) => { Reset(); args.Handled = true; }; Child = retry;
        }
    }
    private void SetPlayback(bool play)
    {
        if (_video is not null)
        {
            if (play && !_playing) { try { _video.Play(); } catch (InvalidOperationException) { } }
            else if (!play && _playing) _video.Pause();
            _playing = play;
            if (_play is not null) _play.Content = _playing ? "Pause" : "Play";
            return;
        }
        if (_image is null || AnimationBehavior.GetAnimator(_image) is not { } animator) { _playing = false; return; }
        // Play/Pause are idempotent; repeating Play each tick also recovers a loop the library paused after we started it.
        if (play) animator.Play(); else animator.Pause();
        if (_playing != play) { _playing = play; if (_play is not null) _play.Content = _playing ? "GIF · Pause" : "GIF · Play"; }
    }
    private void AnimationLoaded(object sender, RoutedEventArgs args)
    { if (ReferenceEquals(sender, _image)) RefreshVisibleMedia(); }
    private void AnimationCompleted(object sender, XamlAnimatedGif.AnimationCompletedEventArgs args)
    {
        if (!ReferenceEquals(sender, _image) || _image is null || !_playing) return;
        // Some GIF files carry a finite repetition count; chat playback stays looping regardless.
        AnimationBehavior.GetAnimator(_image)?.Play();
    }
    private void AnimationFailed(object sender, AnimationErrorEventArgs args)
    {
        if (!ReferenceEquals(sender, _image) || _image is null) return;
        SetPlayback(false); _image.Source = _media?.Poster;
        if (_play is not null) { _play.Content = "GIF unavailable"; _play.IsEnabled = false; }
    }
    private void Release()
    {
        _request?.Cancel(); _request?.Dispose(); _request = null;
        var image = _image; _image = null; _media = null; _play = null; _playing = false;
        if (_video is not null) { _video.Close(); _video = null; }
        if (image is not null)
        {
            AnimationBehavior.RemoveLoadedHandler(image, AnimationLoaded);
            AnimationBehavior.RemoveErrorHandler(image, AnimationFailed);
            AnimationBehavior.RemoveAnimationCompletedHandler(image, AnimationCompleted);
            AnimationBehavior.GetAnimator(image)?.Pause(); AnimationBehavior.SetSourceStream(image, null!);
            image.Source = null;
        }
        _animation?.Dispose(); _animation = null;
        // The preview itself can remain realized while scrolled away. Remove the
        // entire old media surface so it cannot retain pixels or GIF byte streams.
        Child = null; Cursor = null;
    }
    private void OpenViewer()
    {
        if (Url is null) return;
        var media = new ChatImagePreview { Url = Url, IsViewer = true, Height = double.NaN, MinWidth = 0, MaxWidth = double.PositiveInfinity, Margin = new(20) };
        var viewer = new Window { Title = "Media preview", Width = 900, Height = 650, MinWidth = 320, MinHeight = 240,
            Owner = Window.GetWindow(this), WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var chrome = new DockPanel();
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new(10, 8, 10, 0) };
        if (Uri.TryCreate(Url, UriKind.Absolute, out var original) && ChatMediaPolicy.IsMediaReference(original))
        {
            var openOriginal = new System.Windows.Controls.Button
            { Content = "Open original", Padding = new(10, 4, 10, 4), Cursor = Cursors.Hand, ToolTip = "Open the source in your browser" };
            openOriginal.SetResourceReference(System.Windows.Controls.Button.BackgroundProperty, "SocialFieldBrush");
            openOriginal.Click += (_, _) =>
            {
                try { using var opened = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(original.AbsoluteUri) { UseShellExecute = true }); }
                catch (System.ComponentModel.Win32Exception) { }
            };
            actions.Children.Add(openOriginal);
        }
        var close = new System.Windows.Controls.Button { Content = "Close", Padding = new(10, 4, 10, 4), Margin = new(8, 0, 0, 0), Cursor = Cursors.Hand };
        close.Click += (_, _) => viewer.Close();
        actions.Children.Add(close);
        DockPanel.SetDock(actions, Dock.Top);
        chrome.Children.Add(actions);
        chrome.Children.Add(media);
        viewer.Content = chrome;
        viewer.SetResourceReference(Window.BackgroundProperty, "AppBackgroundBrush");
        viewer.KeyDown += (_, args) => { if (args.Key == Key.Escape) viewer.Close(); };
        viewer.Show();
    }
    private static Button ActionButton(string label, string tooltip, HorizontalAlignment alignment) => new()
    {
        Content = label, ToolTip = tooltip, HorizontalAlignment = alignment, VerticalAlignment = VerticalAlignment.Bottom,
        Margin = new(6), Padding = new(9, 4, 9, 4), FontSize = 11, Foreground = Brushes.White, Background = new SolidColorBrush(Color.FromArgb(210, 24, 28, 36)), BorderThickness = new(0), Cursor = Cursors.Hand
    };
    private static TextBlock Placeholder(string label)
    { var text = new TextBlock { Text = label, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }; text.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush"); return text; }
}
