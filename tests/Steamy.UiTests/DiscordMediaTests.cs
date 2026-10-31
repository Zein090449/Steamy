using Steamy.Services;

namespace Steamy.UiTests;

public sealed class DiscordMediaTests
{
    [Theory]
    [InlineData("https://cdn.discordapp.com/attachments/1/photo.png?ex=123", true)]
    [InlineData("https://media.discordapp.net/attachments/1/photo.jpg", true)]
    [InlineData("https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/1/header.jpg", true)]
    [InlineData("https://shared.fastly.steamstatic.com/store_item_assets/steam/apps/1/screenshot.jpg", true)]
    [InlineData("https://shared.fastly.steamstatic.com.evil.example/screenshot.jpg", false)]
    [InlineData("http://cdn.discordapp.com/photo.png", false)]
    [InlineData("https://cdn.discordapp.com.evil.example/photo.png", false)]
    [InlineData("https://evil.example/photo.png", false)]
    [InlineData("https://cdn.discordapp.com:8443/photo.png", false)]
    [InlineData("https://user@cdn.discordapp.com/photo.png", false)]
    [InlineData("https://cdn.discordapp.com/file.exe", false)]
    public void PreviewOnlyUsesTrustedImageLinks(string url, bool expected)
        => Assert.Equal(expected, ChatMediaPolicy.IsImageUri(new Uri(url)));

    [Fact]
    public void ImageOnlyMessageSuppressesRedundantUrlButPreservesCaptions()
    {
        const string url = "https://cdn.discordapp.com/attachments/1/photo.png";
        var message = new DiscordChatMessage(1, 2, 3, 0, 4, url, DateTimeOffset.Now, false, false, false);
        Assert.Equal(url, message.ImageUrl); Assert.Empty(message.DisplayContent);
        Assert.Equal("Look! " + url, (message with { Content = "Look! " + url }).DisplayContent);
        Assert.Equal(url, ChatMediaPolicy.ImageUrl("Look! " + url + ")"));
    }

    [Fact]
    public void PastedDiscordStickerLinksRenderAsOneStickerTileNamedFromTheLink()
    {
        const string url = "https://media.discordapp.net/stickers/1474592151655547125.png?size=160&name=im+in&lossless=true";
        var message = new DiscordChatMessage(1, 2, 3, 0, 4, url, DateTimeOffset.Now, false, false, false);
        Assert.True(message.HasSticker);
        Assert.Equal(url, message.StickerLink);
        // The sticker tile owns the image; the generic preview must not duplicate it.
        Assert.Null(message.ImageUrl);
        Assert.Empty(message.DisplayContent);
        Assert.Equal("im in", message.StickerName);
        Assert.Equal("Sticker", message.PreviewLabel);

        var markdown = message with { Content = "[im in](" + url + ")" };
        Assert.Equal("im in", markdown.StickerName);
        Assert.Null(markdown.ImageUrl);
        // A message that is just a sticker link renders as the tile alone: no blue label above it.
        Assert.Empty(markdown.DisplayContent);
        Assert.False((markdown with { HasAdditionalContent = true, AdditionalKind = "Sticker", AdditionalCount = 1 }).ShowMediaCard);
    }

    [Fact]
    public void StickerOnlyMessagesAreLabelledAsStickersInsteadOfGenericContent()
    {
        // What the Social SDK actually delivers for a real Discord sticker message.
        var sticker = new DiscordChatMessage(1, 2, 3, 0, 4, "", DateTimeOffset.Now, false, true, false,
            "Mika", "", "Other", 1);
        Assert.True(sticker.IsStickerLike);
        Assert.True(sticker.ShowMediaCard);
        Assert.Equal("Discord sticker", sticker.MediaLabel);
        Assert.Equal("Sticker", sticker.PreviewLabel);

        var attachment = sticker with { AdditionalTitle = "photo.png", AdditionalKind = "Attachment" };
        Assert.False(attachment.IsStickerLike);
        Assert.Equal("photo.png", attachment.MediaLabel);

        var unknown = sticker with { Content = "hello", AdditionalKind = "Other" };
        Assert.False(unknown.IsStickerLike);
        Assert.Equal("Discord content", unknown.MediaLabel);
        Assert.Equal("hello", unknown.PreviewLabel);
    }
}
