using System.Windows;
using System.Windows.Media;

namespace Steamy.Controls;

/// <summary>Discord-shaped presence glyph: filled dot, idle crescent, DND bar, offline ring, streaming purple.</summary>
public sealed class SocialStatusBadge : FrameworkElement
{
    public static readonly DependencyProperty PresenceProperty = DependencyProperty.Register(nameof(Presence), typeof(Steamy.Services.DiscordPresence),
        typeof(SocialStatusBadge), new FrameworkPropertyMetadata(Steamy.Services.DiscordPresence.Offline, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty DiameterProperty = DependencyProperty.Register(nameof(Diameter), typeof(double),
        typeof(SocialStatusBadge), new FrameworkPropertyMetadata(10.0, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.AffectsMeasure));
    public static readonly DependencyProperty SurfaceProperty = DependencyProperty.Register(nameof(Surface), typeof(Brush),
        typeof(SocialStatusBadge), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public Steamy.Services.DiscordPresence Presence { get => (Steamy.Services.DiscordPresence)GetValue(PresenceProperty); set => SetValue(PresenceProperty, value); }
    public double Diameter { get => (double)GetValue(DiameterProperty); set => SetValue(DiameterProperty, value); }
    // Surface is the ring/cookie-cutter color, matching whatever the avatar sits on.
    public Brush? Surface { get => (Brush?)GetValue(SurfaceProperty); set => SetValue(SurfaceProperty, value); }

    private static readonly Brush OnlineBrush = new SolidColorBrush(Color.FromRgb(0x23, 0xa5, 0x5a));
    private static readonly Brush IdleBrush = new SolidColorBrush(Color.FromRgb(0xf0, 0xb2, 0x32));
    private static readonly Brush DndBrush = new SolidColorBrush(Color.FromRgb(0xf2, 0x3f, 0x43));
    private static readonly Brush StreamingBrush = new SolidColorBrush(Color.FromRgb(0x59, 0x36, 0x95));
    private static readonly Brush OfflineBrush = new SolidColorBrush(Color.FromRgb(0x80, 0x84, 0x8e));
    private static readonly Brush WhiteBrush = Brushes.White;

    static SocialStatusBadge()
    {
        OnlineBrush.Freeze(); IdleBrush.Freeze(); DndBrush.Freeze(); StreamingBrush.Freeze(); OfflineBrush.Freeze(); WhiteBrush.Freeze();
        SnapsToDevicePixelsProperty.OverrideMetadata(typeof(SocialStatusBadge), new FrameworkPropertyMetadata(true));
    }

    // Without a measure override the element reports 0x0, so Right/Bottom alignment pins its top-left
    // corner to the avatar's corner and the glyph paints outside the avatar entirely.
    protected override Size MeasureOverride(Size availableSize)
    {
        var size = Math.Clamp(Diameter, 4, 64);
        var pad = Cookie(size);
        return new Size(size + pad * 2, size + pad * 2);
    }

    internal static double Cookie(double size) => Math.Max(1.5, size * 0.15);

    protected override void OnRender(DrawingContext drawing)
    {
        var size = Math.Clamp(Diameter, 4, 64);
        var pad = Cookie(size);
        var radius = size / 2;
        var center = new Point(pad + radius, pad + radius);
        var ring = Surface ?? (Application.Current?.TryFindResource("SocialSidebarBrush") as Brush ?? Brushes.Transparent);
        drawing.DrawEllipse(ring, null, center, radius + pad, radius + pad);
        switch (Presence)
        {
            case Steamy.Services.DiscordPresence.Online:
                drawing.DrawEllipse(OnlineBrush, null, center, radius, radius);
                break;
            case Steamy.Services.DiscordPresence.Idle:
            {
                var body = new EllipseGeometry(center, radius, radius);
                var bite = new EllipseGeometry(new Point(center.X + radius * 0.62, center.Y - radius * 0.62), radius, radius);
                drawing.DrawGeometry(IdleBrush, null, Geometry.Combine(body, bite, GeometryCombineMode.Exclude, null));
                break;
            }
            case Steamy.Services.DiscordPresence.Dnd:
            {
                drawing.DrawEllipse(DndBrush, null, center, radius, radius);
                var barHeight = Math.Max(1.6, size * 0.17);
                var barWidth = size * 0.52;
                var bar = new Rect(center.X - barWidth / 2, center.Y - barHeight / 2, barWidth, barHeight);
                drawing.DrawRoundedRectangle(WhiteBrush, null, bar, barHeight / 2, barHeight / 2);
                break;
            }
            case Steamy.Services.DiscordPresence.Streaming:
                drawing.DrawEllipse(StreamingBrush, null, center, radius, radius);
                break;
            default:
                drawing.DrawEllipse(null, new Pen(OfflineBrush, Math.Max(1.2, size * 0.16)), center, radius - Math.Max(0.6, size * 0.08), radius - Math.Max(0.6, size * 0.08));
                break;
        }
    }
}
