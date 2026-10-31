using System.Net;
using System.Net.Http;
using System.Text;
using Steamy.Models;
using Steamy.Services;
using Steamy.ViewModels;

namespace Steamy.UiTests;

public sealed class GifSearchTests
{
    private const string Response = """
        {"result":true,"data":{"data":[{"slug":"hello-hi-662","title":"Hello","file":{"xs":{"gif":{"url":"https://static.klipy.com/ii/abc/preview.gif?quality=small"}},"md":{"gif":{"url":"https://static2.klipy.com/ii/abc/hello.gif?tracking=keep"}}}}],"has_next":true}}
        """;
    [Fact]
    public async Task KlipySearchPreservesTheQueryProviderOrderAndUnmodifiedMediaUrls()
    {
        var handler = new Handler(Response); var settings = new AppSettings { GifProvider = "KLIPY", GifApiKey = "test-key" };
        var service = new GifSearchService(settings, new HttpClient(handler));
        var results = await service.SearchAsync("hi & bye!", 2, default);
        Assert.Equal("api.klipy.com", handler.Url!.Host);
        Assert.Contains("q=hi%20%26%20bye%21", handler.Url.AbsoluteUri);
        Assert.Contains("page=2", handler.Url.Query);
        var gif = Assert.Single(results.Items);
        Assert.Equal("hello-hi-662", gif.Id); Assert.EndsWith("?tracking=keep", gif.ShareUrl); Assert.True(results.HasMore);
    }
    [Fact]
    public async Task RateLimitsDoNotTriggerAnAutomaticRequestLoop()
    {
        var handler = new Handler("{}", HttpStatusCode.TooManyRequests);
        var service = new GifSearchService(new AppSettings { GifProvider = "KLIPY", GifApiKey = "test-key" }, new HttpClient(handler));
        await Assert.ThrowsAsync<GifProviderException>(() => service.SearchAsync("", 1, default));
        await Assert.ThrowsAsync<GifProviderException>(() => service.SearchAsync("different", 1, default));
        Assert.Equal(1, handler.Calls);
    }
    [Fact]
    public void ProviderLinksCannotEscapeTheTrustedMediaDomains()
    {
        Assert.Throws<GifProviderException>(() => GifSearchService.Parse(Encoding.UTF8.GetBytes(Response.Replace("static2.klipy.com", "static2.klipy.com.evil.example")), "KLIPY"));
        Assert.Throws<GifProviderException>(() => GifSearchService.Parse("{\"result\":false}"u8.ToArray(), "KLIPY"));
    }
    [Fact]
    public async Task NoKeyStillAllowsDirectGifLinksAndFavoritesStoreIdsOnly()
    {
        var settings = new Settings { Value = new AppSettings { GifProvider = "KLIPY" } };
        var service = new GifSearchService(settings.Load(), new HttpClient(new Handler(Response)));
        using var model = new GifPickerViewModel(service, settings);
        await model.OpenAsync(); Assert.False(model.Busy); Assert.Contains("Paste", model.Status);
        var picked = ""; model.Picked += (_, link) => picked = link;
        model.Link = "https://static.klipy.com/ii/abc/hello.gif";
        Assert.True(model.UseLinkCommand.CanExecute(null)); model.UseLinkCommand.Execute(null); Assert.Equal(model.Link, picked);
        var gif = Assert.Single(GifSearchService.Parse(Encoding.UTF8.GetBytes(Response), "KLIPY").Items);
        await model.FavoriteCommand.ExecuteAsync(gif);
        Assert.Equal("KLIPY:hello-hi-662", Assert.Single(settings.Value.GifFavoriteIds));
        Assert.DoesNotContain(settings.Value.GifFavoriteIds, favorite => favorite.Contains("http"));
    }
    [Fact]
    public async Task ClosingThePickerCancelsAQueryAndDiscardsLateResults()
    {
        var service = new SlowService(); using var model = new GifPickerViewModel(service, new Settings());
        var pending = model.OpenAsync(); model.Suspend(); service.Pending.SetResult(new([new("KLIPY", "one", "One", "https://static.klipy.com/one.gif", "https://static.klipy.com/one.gif")], false));
        await pending; Assert.Empty(model.Items); Assert.False(model.Busy); Assert.True(service.Token.IsCancellationRequested);
    }
    [Fact]
    public async Task TenorSearchesWithoutAKeyAndUsesItsPublicCatalog()
    {
        var handler = new Handler(TenorPage("42"));
        var service = new GifSearchService(new AppSettings(), new HttpClient(handler));
        Assert.True(service.IsConfigured); Assert.Equal("TENOR", service.Provider);
        var results = await service.SearchAsync("hi there", 1, default);
        Assert.Equal("tenor.com", handler.Url!.Host);
        Assert.Equal("/search/hi+there-gifs", handler.Url.AbsolutePath);
        var gif = Assert.Single(results.Items);
        Assert.Equal("TENOR", gif.Provider); Assert.Equal("42", gif.Id); Assert.Equal("a cat waves", gif.Title);
        Assert.Equal("https://media.tenor.com/abc/tiny.gif", gif.PreviewUrl);
        Assert.Equal("https://tenor.com/view/hello-gif-42", gif.ShareUrl);
        Assert.False(results.HasMore);
    }
    [Fact]
    public async Task TenorFavoritesResolveThroughThePublicViewPage()
    {
        var handler = new Handler("<html><script id=\"store-cache\" type=\"text/x-cache\">{\"gifs\":{\"byId\":{\"42\":{\"results\":[{\"id\":\"42\",\"content_description\":\"a cat waves\",\"itemurl\":\"https://tenor.com/view/hello-gif-42\",\"media_formats\":{\"tinygif\":{\"url\":\"https://media.tenor.com/abc/tiny.gif\"}}}]}}}}</script></html>");
        var service = new GifSearchService(new AppSettings(), new HttpClient(handler));
        var results = await service.FavoritesAsync(["42"], default);
        var gif = Assert.Single(results.Items);
        Assert.Equal("42", gif.Id); Assert.Equal("https://tenor.com/view/hello-gif-42", gif.ShareUrl);
        Assert.Equal("/view/42", handler.Url!.AbsolutePath);
    }
    [Fact]
    public async Task TenorPagesWithoutCatalogDataShowNoResultsInsteadOfFailing()
    {
        var service = new GifSearchService(new AppSettings(), new HttpClient(new Handler("<html>no catalog</html>")));
        Assert.Empty((await service.SearchAsync("", 1, default)).Items);
    }
    private static string TenorPage(string id) =>
        "<html><script id=\"store-cache\" type=\"text/x-cache\">{\"universal\":{\"search\":{\"hi there-low-all\":{\"results\":[{\"id\":\""
        + id + "\",\"content_description\":\"a cat waves\",\"itemurl\":\"https://tenor.com/view/hello-gif-42\",\"media_formats\":{\"tinygif\":{\"url\":\"https://media.tenor.com/abc/tiny.gif\"},\"gif\":{\"url\":\"https://media1.tenor.com/m/big.gif\"}}}]}}}}</script></html>";
    private sealed class Handler(string body, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public Uri? Url; public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { Calls++; Url = request.RequestUri; return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") }); }
    }
    private sealed class SlowService : IGifSearchService
    {
        public string Provider => "KLIPY"; public bool IsConfigured => true;
        public TaskCompletionSource<GifSearchPage> Pending = new(); public CancellationToken Token;
        public Task<GifSearchPage> SearchAsync(string query, int page, CancellationToken token) { Token = token; return Pending.Task; }
        public Task<GifSearchPage> FavoritesAsync(IReadOnlyList<string> ids, CancellationToken token) => SearchAsync("", 1, token);
    }
    private sealed class Settings : ISettingsService
    {
        public AppSettings Value = new(); public AppSettings Load() => Value;
        public Task SaveAsync(AppSettings settings, CancellationToken token = default) => Task.CompletedTask;
        public Task ResetAsync(CancellationToken token = default) => Task.CompletedTask;
    }
}
