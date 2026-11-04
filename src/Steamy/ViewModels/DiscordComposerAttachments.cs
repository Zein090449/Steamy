using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.Input;
using Steamy.Services;

namespace Steamy.ViewModels;

/// <summary>Something waiting in the composer: a picked GIF/sticker, or a local file uploaded when sent.</summary>
public sealed record ComposerAttachment(string Name, string Detail, string? Url = null, string? LocalPath = null, ImageSource? Preview = null)
{
    public bool HasRemotePreview => Url is not null;
    public bool HasLocalPreview => Preview is not null;
    public bool HasFileIcon => Url is null && Preview is null;
}

public sealed partial class DiscordFriendsViewModel
{
    private readonly IFileUploadService? _uploads;
    private ComposerAttachment? _attachment;
    public ComposerAttachment? Attachment
    {
        get => _attachment;
        set
        {
            if (!SetProperty(ref _attachment, value)) return;
            OnPropertyChanged(nameof(AttachmentVisibility));
            SendMessageCommand?.NotifyCanExecuteChanged();
        }
    }
    public Visibility AttachmentVisibility => _attachment is null ? Visibility.Collapsed : Visibility.Visible;
    public IRelayCommand RemoveAttachmentCommand { get; private set; } = null!;

    private void InitializeComposerAttachments() => RemoveAttachmentCommand = new RelayCommand(() => Attachment = null);

    /// <summary>Shows a composer status line from the page (for example when a pick is too large).</summary>
    public void SetComposerStatus(string message) => ChatStatus = message;

    /// <summary>Queues a local file; it is uploaded to the file host only when the message is sent.</summary>
    public void AttachFile(string path)
    {
        if (IsEditing) return;
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists) { ChatStatus = "That file no longer exists."; return; }
            if (file.Length == 0) { ChatStatus = $"{file.Name} is empty."; return; }
            if (file.Length > CatboxFileUploadService.MaxBytes)
            { ChatStatus = $"{file.Name} is larger than 200 MB, the most the file host accepts."; return; }
            Attachment = new ComposerAttachment(file.Name, $"{FileSize(file.Length)} · shared as a link through catbox.moe", LocalPath: file.FullName, Preview: LoadLocalPreview(file.FullName));
            ChatStatus = "";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        { ChatStatus = error is IOException or UnauthorizedAccessException ? "That file can't be read. Check the folder permissions." : "That file path isn't valid."; }
    }

    /// <summary>Queues a GIF or sticker link and previews it instead of pasting it into the draft.</summary>
    public void AttachMedia(string kind, string url)
    {
        if (IsEditing || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http")) return;
        Attachment = new ComposerAttachment(kind, "Shown inline in Discord · sent as a link", Url: uri.AbsoluteUri);
        ChatStatus = "";
    }

    private static ImageSource? LoadLocalPreview(string path)
    {
        if (!IsImage(path)) return null;
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = new Uri(path);
            image.DecodePixelWidth = 104;
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception error) when (error is IOException or NotSupportedException or ArgumentException or FileFormatException) { return null; }
    }

    private static bool IsImage(string path) => Path.GetExtension(path).ToLowerInvariant()
        is ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".webp" or ".tif" or ".tiff" or ".ico";

    private static string FileSize(long bytes) => bytes >= 1024 * 1024 * 1024 ? $"{bytes / (1024.0 * 1024 * 1024):0.#} GB"
        : bytes >= 1024 * 1024 ? $"{bytes / (1024.0 * 1024):0.#} MB"
        : bytes >= 1024 ? $"{bytes / 1024.0:0.#} KB" : $"{bytes} B";

    private static string AttachmentLink(string draft, ComposerAttachment attachment) =>
        draft.Length == 0 ? attachment.Url ?? "" : attachment.Url is { Length: > 0 } link ? draft + "\n" + link : draft;
}
