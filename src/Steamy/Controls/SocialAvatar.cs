using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Steamy.Services;

namespace Steamy.Controls;

/// <summary>One avatar control everywhere: async CDN image over a letter fallback, optional presence badge.</summary>
public sealed class SocialAvatar : Grid
{
    public static readonly DependencyProperty UrlProperty = DependencyProperty.Register(nameof(Url), typeof(object), typeof(SocialAvatar), new PropertyMetadata(null, LayoutChanged));
    public static readonly DependencyProperty InitialProperty = DependencyProperty.Register(nameof(Initial), typeof(string), typeof(SocialAvatar), new PropertyMetadata("S", LayoutChanged));
    public static readonly DependencyProperty PresenceProperty = DependencyProperty.Register(nameof(Presence), typeof(DiscordPresence), typeof(SocialAvatar), new PropertyMetadata(DiscordPresence.Offline, LayoutChanged));
    public static readonly DependencyProperty ShowPresenceProperty = DependencyProperty.Register(nameof(ShowPresence), typeof(bool), typeof(SocialAvatar), new PropertyMetadata(false, LayoutChanged));
    public static readonly DependencyProperty DiameterProperty = DependencyProperty.Register(nameof(Diameter), typeof(double), typeof(SocialAvatar), new PropertyMetadata(40.0, LayoutChanged));
    public static readonly DependencyProperty BadgeSurfaceProperty = DependencyProperty.Register(nameof(BadgeSurface), typeof(Brush), typeof(SocialAvatar), new PropertyMetadata(null, LayoutChanged));

    public object? Url { get => GetValue(UrlProperty); set => SetValue(UrlProperty, value); }
    public string Initial { get => (string)GetValue(InitialProperty); set => SetValue(InitialProperty, value); }
    public DiscordPresence Presence { get => (DiscordPresence)GetValue(PresenceProperty); set => SetValue(PresenceProperty, value); }
    public bool ShowPresence { get => (bool)GetValue(ShowPresenceProperty); set => SetValue(ShowPresenceProperty, value); }
    public double Diameter { get => (double)GetValue(DiameterProperty); set => SetValue(DiameterProperty, value); }
    // Cookie-cutter color for the presence badge when the avatar sits on a non-standard surface.
    public Brush? BadgeSurface { get => (Brush?)GetValue(BadgeSurfaceProperty); set => SetValue(BadgeSurfaceProperty, value); }

    private readonly Grid _circle;
    private readonly Border _letter;
    private readonly Image _image;
    private readonly SocialStatusBadge _badge;

    public SocialAvatar()
    {
        _letter = new Border();
        var label = new TextBlock { FontSize = 14, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        label.SetResourceReference(TextBlock.ForegroundProperty, "SocialAccentBrush");
        _letter.Child = label;
        _letter.SetResourceReference(Border.BackgroundProperty, "SocialAccentSoftBrush");
        _image = new Image { Stretch = Stretch.UniformToFill };
        _circle = new Grid { ClipToBounds = false };
        _circle.Children.Add(_letter);
        _circle.Children.Add(_image);
        _badge = new SocialStatusBadge();
        Children.Add(_circle);
        Children.Add(_badge);
        LayoutChanged(this, default);
    }

    private static void LayoutChanged(DependencyObject owner, DependencyPropertyChangedEventArgs args)
    {
        if (owner is not SocialAvatar avatar) return;
        var size = Math.Clamp(avatar.Diameter, 12, 128);
        avatar.Width = avatar.Height = size;
        avatar._circle.Width = avatar._circle.Height = size;
        var radius = size / 2;
        avatar._circle.Clip = new EllipseGeometry(new Point(radius, radius), radius, radius);
        avatar._letter.CornerRadius = new CornerRadius(radius);
        if (avatar._letter.Child is TextBlock label) label.FontSize = Math.Clamp(size * 0.38, 9, 26);
        AsyncArtwork.SetPrivate(avatar._image, true);
        AsyncArtwork.SetWidth(avatar._image, Math.Clamp((int)(size * 2), 32, 256));
        AsyncArtwork.SetHeight(avatar._image, Math.Clamp((int)(size * 2), 32, 256));
        var artworkUrl = avatar.Url?.ToString();
        AsyncArtwork.SetUrl(avatar._image, string.IsNullOrWhiteSpace(artworkUrl) ? null : artworkUrl);
        avatar._image.Visibility = string.IsNullOrWhiteSpace(artworkUrl) ? Visibility.Collapsed : Visibility.Visible;
        var badgeSize = Math.Max(10, size * 0.36);
        avatar._badge.Visibility = avatar.ShowPresence ? Visibility.Visible : Visibility.Collapsed;
        avatar._badge.Presence = avatar.Presence;
        avatar._badge.Diameter = badgeSize;
        avatar._badge.Surface = avatar.BadgeSurface;
        avatar._badge.HorizontalAlignment = HorizontalAlignment.Right;
        avatar._badge.VerticalAlignment = VerticalAlignment.Bottom;
        var pad = SocialStatusBadge.Cookie(badgeSize);
        avatar._badge.Margin = new Thickness(0, 0, -pad, -pad);
    }
}
