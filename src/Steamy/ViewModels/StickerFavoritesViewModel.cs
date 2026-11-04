using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Steamy.Models;
using Steamy.Services;

namespace Steamy.ViewModels;

public sealed record StickerFavoriteItem(string Url, string Name);

/// <summary>Sticker links the user saved by hand; Discord has no API that hands apps guild stickers.</summary>
public sealed class StickerFavoritesViewModel : ObservableObject
{
    public const int MaxFavorites = 100;
    private readonly ISettingsService _settingsService;
    private readonly AppSettings _settings;
    private string _link = "", _status = "";
    public ObservableCollection<StickerFavoriteItem> Favorites { get; } = [];
    public string Link { get => _link; set { if (SetProperty(ref _link, value)) AddCommand.NotifyCanExecuteChanged(); } }
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public IAsyncRelayCommand AddCommand { get; }
    public IAsyncRelayCommand<StickerFavoriteItem> RemoveCommand { get; }
    public IRelayCommand<StickerFavoriteItem> PickCommand { get; }
    public event EventHandler<string>? Picked;
    public StickerFavoritesViewModel(ISettingsService settings)
    {
        _settingsService = settings;
        _settings = settings.Load();
        foreach (var url in _settings.StickerFavoriteUrls)
            if (TryNormalize(url, out var favorite) && Favorites.All(saved => saved.Url != favorite.Url)) Favorites.Add(favorite);
        AddCommand = new AsyncRelayCommand(AddAsync, () => !string.IsNullOrWhiteSpace(Link));
        RemoveCommand = new AsyncRelayCommand<StickerFavoriteItem>(RemoveAsync);
        PickCommand = new RelayCommand<StickerFavoriteItem>(item => { if (item is not null) Picked?.Invoke(this, item.Url); });
    }

    public void Prepare() { Link = ""; Status = ""; }

    private async Task AddAsync()
    {
        if (!TryNormalize(Link, out var favorite))
        {
            Status = Uri.TryCreate(Link.Trim(), UriKind.Absolute, out var uri) && ChatMediaPolicy.IsStickerUri(uri)
                ? "Steamy can show PNG, GIF and WebP stickers, not Lottie (.json) stickers. Save the PNG version instead."
                : "Paste a Discord sticker link like https://media.discordapp.net/stickers/…png?size=160";
            return;
        }
        if (Favorites.Any(item => item.Url == favorite.Url)) { Status = "That sticker is already saved."; return; }
        if (Favorites.Count >= MaxFavorites) Favorites.RemoveAt(Favorites.Count - 1);
        Favorites.Insert(0, favorite);
        Link = "";
        await PersistAsync("Sticker saved. Pick it to paste it into your message.");
    }

    private async Task RemoveAsync(StickerFavoriteItem? item)
    {
        if (item is null || !Favorites.Remove(item)) return;
        await PersistAsync("Sticker removed.");
    }

    private async Task PersistAsync(string success)
    {
        _settings.StickerFavoriteUrls = Favorites.Select(item => item.Url).ToArray();
        try { await _settingsService.SaveAsync(_settings); Status = success; }
        catch (Exception) { Status = "Couldn't save your sticker favorites. Check that Steamy can write to its settings folder."; }
    }

    private static bool TryNormalize(string? link, out StickerFavoriteItem favorite)
    {
        favorite = null!;
        if (!Uri.TryCreate((link ?? "").Trim(), UriKind.Absolute, out var uri) || !ChatMediaPolicy.IsStickerUri(uri) || !ChatMediaPolicy.IsImageUri(uri)) return false;
        var url = uri.AbsoluteUri;
        favorite = new(url, ChatMediaPolicy.StickerQueryName(url) ?? "Sticker");
        return true;
    }
}
