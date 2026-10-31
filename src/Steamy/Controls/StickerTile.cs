using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Steamy.Services;

namespace Steamy.Controls;

/// <summary>Sticker links render as a compact image with the decoded name beneath it — never raw markdown or the CDN URL.</summary>
public sealed class StickerTile : Border
{
    public static readonly DependencyProperty StickerUrlProperty = DependencyProperty.Register(nameof(StickerUrl), typeof(string),
        typeof(StickerTile), new PropertyMetadata(null, Changed));

    public string? StickerUrl { get => (string?)GetValue(StickerUrlProperty); set => SetValue(StickerUrlProperty, value); }
    public static readonly DependencyProperty StickerNameProperty = DependencyProperty.Register(nameof(StickerName), typeof(string),
        typeof(StickerTile), new PropertyMetadata("Sticker", Changed));

    public string StickerName { get => (string)GetValue(StickerNameProperty); set => SetValue(StickerNameProperty, value); }

    private static void Changed(DependencyObject d, DependencyPropertyChangedEventArgs args) => ((StickerTile)d).Reset();

    private static readonly Func<Uri, CancellationToken, int, Task<ChatMedia>> Loader = ChatMediaLoader.LoadAsync;
    private CancellationTokenSource? _request;
    private int _width;

    public StickerTile()
    {
        HorizontalAlignment = HorizontalAlignment.Left;
        Margin = new Thickness(0, 6, 0, 2);
        Width = 168;
        _width = 320;
        Reset();
    }

    private void Reset()
    {
        _request?.Cancel(); _request?.Dispose(); _request = null;
        Child = null;
        var url = StickerUrl;
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || !ChatMediaPolicy.IsStickerUri(uri))
        { Child = Placeholder("Sticker"); return; }
        var name = string.IsNullOrWhiteSpace(StickerName) ? "Sticker" : StickerName.Trim();
        var stack = new StackPanel { MaxWidth = 200 };
        var frame = new Border { Width = 160, Height = 160, CornerRadius = new CornerRadius(12), Child = Placeholder("Loading sticker…") };
        frame.SetResourceReference(Border.BackgroundProperty, "SocialFieldBrush");
        var image = new Image { Stretch = Stretch.Uniform, MaxWidth = 160, MaxHeight = 160 };
        var label = new TextBlock { Text = name, FontSize = 13, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(2, 6, 2, 0), TextAlignment = TextAlignment.Center, Foreground = TryResource("TextPrimaryBrush") };
        stack.Children.Add(frame);
        stack.Children.Add(label);
        Child = stack;
        var request = _request = new CancellationTokenSource();
        _ = LoadAsync(uri, image, frame, request.Token);
    }

    private async Task LoadAsync(Uri uri, Image image, Border frame, CancellationToken token)
    {
        try
        {
            var media = await Loader(uri, token, _width).ConfigureAwait(true);
            if (token.IsCancellationRequested || _request?.Token != token) return;
            image.Source = media.Poster;
            frame.Child = image;
            if (media.Animated) frame.ToolTip = "Animated sticker";
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is IOException or System.Net.Http.HttpRequestException)
        {
            if (token.IsCancellationRequested || _request?.Token != token) return;
            frame.Child = Placeholder("Sticker unavailable");
        }
    }

    private static UIElement Placeholder(string label)
    {
        var text = new TextBlock { Text = label, FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8) };
        text.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        return text;
    }
    private static Brush? TryResource(object key) => Application.Current?.TryFindResource(key) as Brush;
}
