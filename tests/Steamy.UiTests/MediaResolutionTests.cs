using System.Text;
using Steamy.Services;

namespace Steamy.UiTests;

public sealed class MediaResolutionTests
{
    [Theory]
    [InlineData("https://giphy.com/gifs/funny-cat-3o7btPCcdNniyf0ArS", "https://media.giphy.com/media/3o7btPCcdNniyf0ArS/giphy.gif")]
    [InlineData("https://giphy.com/stickers/party-l0HlvtIPzPdt2usKs", "https://media.giphy.com/media/l0HlvtIPzPdt2usKs/giphy.gif")]
    [InlineData("https://tenor.com/NkREwRhR13c/some-slug", "https://media.tenor.com/NkREwRhR13c/some-slug.gif")]
    public void SharePagesResolveToProviderMedia(string page, string expected) =>
        Assert.Equal(expected, Assert.Single(ChatMediaLoader.ProviderCandidates(new Uri(page))).AbsoluteUri);

    [Fact]
    public void KlipyItemJsonPrefersTheChatSizedGif()
    {
        const string json = """{"result":true,"data":{"file":{"hd":{"gif":{"url":"https://static.klipy.com/hd.gif"}},"md":{"gif":{"url":"https://static.klipy.com/md.gif"}},"xs":{"gif":{"url":"https://static.klipy.com/xs.gif"}}}}}""";
        Assert.Equal(["https://static.klipy.com/md.gif", "https://static.klipy.com/hd.gif", "https://static.klipy.com/xs.gif"],
            ChatMediaLoader.KlipyCandidates(Encoding.UTF8.GetBytes(json)).Select(media => media.AbsoluteUri));
        Assert.Empty(ChatMediaLoader.KlipyCandidates(Encoding.UTF8.GetBytes(json.Replace("static.klipy.com", "static.klipy.com.evil.example"))));
        Assert.Empty(ChatMediaLoader.KlipyCandidates("{\"result\":false}"u8.ToArray()));
    }

    [Fact]
    public async Task DirectVideoLinksSkipImageDecoding()
    {
        var media = await ChatMediaLoader.LoadAsync(new Uri("https://cdn.discordapp.com/attachments/1/2/clip.mp4"), CancellationToken.None);
        Assert.True(media.IsVideo);
        Assert.Equal("https://cdn.discordapp.com/attachments/1/2/clip.mp4", media.VideoUrl!.AbsoluteUri);
        Assert.False(media.Animated);
        Assert.NotNull(media.Poster);
    }
}
