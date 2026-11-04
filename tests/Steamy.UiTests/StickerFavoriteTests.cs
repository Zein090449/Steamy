using Steamy.Models;
using Steamy.Services;
using Steamy.ViewModels;

namespace Steamy.UiTests;

public sealed class StickerFavoriteTests
{
    private const string Sticker = "https://media.discordapp.net/stickers/1518319876907733022.png?size=160&name=GATURUJA+EBA&lossless=true";
    private const string Second = "https://cdn.discordapp.com/stickers/1518319876907733023.gif?size=160&name=Party+Cat";

    [Fact]
    public async Task SavingAStickerLinkKeepsItAndNamesItFromTheQuery()
    {
        var settings = new Settings(); var model = new StickerFavoritesViewModel(settings);
        model.Link = "  " + Sticker + "  ";
        Assert.True(model.AddCommand.CanExecute(null)); await model.AddCommand.ExecuteAsync(null);

        var favorite = Assert.Single(model.Favorites);
        Assert.Equal(Sticker, favorite.Url); Assert.Equal("GATURUJA EBA", favorite.Name);
        Assert.Equal("", model.Link);
        Assert.Contains("saved", model.Status);
        Assert.Equal(Sticker, Assert.Single(settings.Value.StickerFavoriteUrls));
    }

    [Theory]
    [InlineData("https://media.discordapp.net/attachments/1/2/image.png")]
    [InlineData("https://example.com/stickers/1.png")]
    [InlineData("http://media.discordapp.net/stickers/1.png")]
    [InlineData("https://media.discordapp.net/stickers/1.json?size=160")]
    public async Task LinksThatAreNotRenderableDiscordStickersAreRejected(string link)
    {
        var settings = new Settings(); var model = new StickerFavoritesViewModel(settings);
        model.Link = link; await model.AddCommand.ExecuteAsync(null);
        Assert.Empty(model.Favorites); Assert.Empty(settings.Value.StickerFavoriteUrls);
    }

    [Fact]
    public async Task LottieStickersGetAnExplanationInsteadOfASilentFailure()
    {
        var model = new StickerFavoritesViewModel(new Settings());
        model.Link = "https://media.discordapp.net/stickers/1.json?size=160";
        await model.AddCommand.ExecuteAsync(null);
        Assert.Contains("Lottie", model.Status);
    }

    [Fact]
    public async Task DuplicateLinksAreNotSavedTwice()
    {
        var settings = new Settings(); var model = new StickerFavoritesViewModel(settings);
        model.Link = Sticker; await model.AddCommand.ExecuteAsync(null);
        model.Link = Sticker; await model.AddCommand.ExecuteAsync(null);
        Assert.Single(model.Favorites); Assert.Contains("already", model.Status);
        Assert.Equal(Sticker, Assert.Single(settings.Value.StickerFavoriteUrls));
    }

    [Fact]
    public async Task NewestFavoritesComeFirstAndPickingRaisesTheLink()
    {
        var settings = new Settings(); var model = new StickerFavoritesViewModel(settings);
        model.Link = Sticker; await model.AddCommand.ExecuteAsync(null);
        model.Link = Second; await model.AddCommand.ExecuteAsync(null);
        Assert.Equal([Second, Sticker], model.Favorites.Select(item => item.Url));

        var picked = ""; model.Picked += (_, link) => picked = link;
        model.PickCommand.Execute(model.Favorites[1]);
        Assert.Equal(Sticker, picked);
    }

    [Fact]
    public async Task RemovingAndRestartingKeepsTheStoredListInSync()
    {
        var settings = new Settings(); var model = new StickerFavoritesViewModel(settings);
        model.Link = Sticker; await model.AddCommand.ExecuteAsync(null);
        model.Link = Second; await model.AddCommand.ExecuteAsync(null);
        await model.RemoveCommand.ExecuteAsync(model.Favorites[0]);
        Assert.Equal(Sticker, Assert.Single(model.Favorites).Url);

        var restarted = new StickerFavoritesViewModel(settings);
        Assert.Equal(Sticker, Assert.Single(restarted.Favorites).Url);
        Assert.Equal(Sticker, Assert.Single(settings.Value.StickerFavoriteUrls));
    }

    private sealed class Settings : ISettingsService
    {
        public AppSettings Value = new();
        public AppSettings Load() => Value;
        public Task SaveAsync(AppSettings settings, CancellationToken token = default) => Task.CompletedTask;
        public Task ResetAsync(CancellationToken token = default) => Task.CompletedTask;
    }
}
