using System.Net;
using System.Text;
using Steamy.Services;

namespace Steamy.Tests;

public sealed class EmulatedCatalogProviderTests
{
    private const string NintendoJson = """
        {"response":{"numFound":123,"docs":[
          {"fs_id":"700000","title":"Mario Kart 8 Deluxe",
           "url":"/en-gb/Games/Nintendo-Switch-download-software/Mario-Kart-8-Deluxe-700000.html",
           "dates_released_dts":["2017-04-28T00:00:00Z"],
           "image_url":"https://www.nintendo.com/eu/media/images/mk8.jpg",
           "system_names_txt":["Nintendo Switch"],"pretty_date_s":"Apr 28, 2017"},
          {"fs_id":"700001","title":"Unreleased Thing",
           "url":"/en-gb/Games/X/Unreleased-Thing-700001.html",
           "image_url":null,"system_names_txt":[],"pretty_date_s":"TBD"}
        ]}}
        """;

    private static string IgdbGames(int count) =>
        "[" + string.Join(",", Enumerable.Range(1, count).Select(index =>
            index == 1
                ? """{"id":1,"name":"Mario Kart 8 Deluxe","slug":"mario-kart-8-deluxe","cover":{"image_id":"co123"},"first_release_date":1493337600,"rating":88.4,"platforms":[{"id":130,"name":"Nintendo Switch"}]}"""
                : $$"""{"id":{{index}},"name":"Game {{index}}","slug":"game-{{index}}","first_release_date":null,"rating":0}""")) + "]";

    [Fact]
    public async Task NintendoPlatformsAreListedWithoutNetwork()
    {
        using var handler = new Handler(_ => throw new InvalidOperationException("Nintendo platforms must not hit the network."));
        using var http = new HttpClient(handler);
        var provider = new NintendoCatalogService(http);

        var platforms = await provider.GetPlatformsAsync();

        Assert.True(provider.IsConfigured);
        Assert.False(provider.NeedsCredentials);
        Assert.Contains("Nintendo Switch", platforms);
        Assert.Contains("Nintendo Switch 2", platforms);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task NintendoSearchFiltersPlatformAndParsesGames()
    {
        using var handler = new Handler(_ => Json(NintendoJson));
        using var http = new HttpClient(handler);
        var provider = new NintendoCatalogService(http);

        var page = await provider.SearchAsync("Nintendo Switch", null, 2);

        Assert.Equal(123, page.Total);
        Assert.True(page.HasMore);
        Assert.Equal(2, page.Items.Count);
        var game = page.Items[0];
        Assert.Equal("Mario Kart 8 Deluxe", game.Name);
        Assert.Equal(2017, game.Year);
        Assert.Equal("Nintendo Switch", game.Platform);
        Assert.Equal("2017 · Nintendo Switch", game.MetaLabel);
        Assert.Equal("https://www.nintendo.com/eu/media/images/mk8.jpg", game.CoverUrl);
        Assert.Equal("https://www.nintendo.com/en-gb/Games/Nintendo-Switch-download-software/Mario-Kart-8-Deluxe-700000.html", game.PageUrl);
        Assert.Null(page.Items[1].Year);

        var uri = handler.Requests.Single();
        Assert.StartsWith(NintendoCatalogService.Endpoint, uri.AbsoluteUri);
        Assert.Contains("q=%2A", uri.Query);
        Assert.Contains("fq=" + Uri.EscapeDataString("system_type:nintendoswitch"), uri.Query);
        Assert.Contains("fq=" + Uri.EscapeDataString("-system_type:nintendoswitch2"), uri.Query);
        Assert.Contains("start=24", uri.Query);
        Assert.Contains("sort=", uri.Query);
    }

    [Fact]
    public async Task NintendoSearchJoinsWordsWithAnd()
    {
        using var handler = new Handler(_ => Json(NintendoJson));
        using var http = new HttpClient(handler);
        var provider = new NintendoCatalogService(http);

        await provider.SearchAsync(null, "Mario Kart 8: Deluxe!", 1);

        var uri = handler.Requests.Single();
        Assert.Contains(Uri.EscapeDataString("Mario AND Kart AND 8 AND Deluxe"), uri.Query);
        Assert.DoesNotContain("sort=", uri.Query);
    }

    [Fact]
    public async Task NintendoUnknownPlatformFailsWithFriendlyMessage()
    {
        var provider = new NintendoCatalogService();
        var error = await Assert.ThrowsAsync<CatalogProviderException>(() => provider.SearchAsync("Dreamcast", null, 1));
        Assert.Contains("Dreamcast", error.Message);
    }

    [Fact]
    public async Task IgdbWithoutCredentialsFailsWithFriendlyMessage()
    {
        var provider = new IgdbCatalogService(() => "  ", () => "");
        Assert.False(provider.IsConfigured);
        var error = await Assert.ThrowsAsync<CatalogProviderException>(() => provider.SearchAsync(null, null, 1));
        Assert.Contains("IGDB", error.Message);
    }

    [Fact]
    public async Task IgdbSearchFetchesTokenOnceAndParsesGames()
    {
        using var handler = new Handler(request =>
        {
            if (request.RequestUri!.Host == "id.twitch.tv") return Json("""{"access_token":"tok","expires_in":3600}""");
            return Json(IgdbGames(24));
        });
        using var http = new HttpClient(handler);
        var provider = new IgdbCatalogService(() => "cid", () => "secret", http);

        var page = await provider.SearchAsync("Nintendo Switch", "mario kart", 1);
        await provider.SearchAsync(null, null, 2);
        await provider.SearchAsync("Nintendo Switch", null, 3);

        Assert.True(provider.IsConfigured);
        Assert.Equal(1, handler.Requests.Count(uri => uri.Host == "id.twitch.tv"));
        Assert.Equal(3, handler.Requests.Count(uri => uri.Host == "api.igdb.com"));
        var game = page.Items[0];
        Assert.Equal("Mario Kart 8 Deluxe", game.Name);
        Assert.Equal(2017, game.Year);
        Assert.Equal("Nintendo Switch", game.Platform);
        Assert.Equal("https://images.igdb.com/igdb/image/upload/t_cover_big/co123.jpg", game.CoverUrl);
        Assert.Equal("https://www.igdb.com/games/mario-kart-8-deluxe", game.PageUrl);
        Assert.Null(page.Total);
        Assert.True(page.HasMore);

        var search = handler.Bodies.First(body => body.Contains("search"));
        Assert.Contains("search \"mario kart\"", search);
        Assert.Contains("where platforms = (130)", search);
        Assert.DoesNotContain("sort", search);
        var browse = handler.Bodies[^2];
        Assert.DoesNotContain("where", browse);
        Assert.Contains("sort total_rating_count desc", browse);
        Assert.Contains("offset 24", browse);
        var platformBrowse = handler.Bodies[^1];
        Assert.Contains("where platforms = (130) & total_rating_count > 0", platformBrowse);
        Assert.Contains("offset 48", platformBrowse);
        Assert.Contains(handler.Headers, header => header.Contains("Client-ID: cid") && header.Contains("Bearer tok"));
    }

    [Fact]
    public async Task IgdbRejectedCredentialsReportTheProblem()
    {
        using var handler = new Handler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        using var http = new HttpClient(handler);
        var provider = new IgdbCatalogService(() => "cid", () => "secret", http);
        var error = await Assert.ThrowsAsync<CatalogProviderException>(() => provider.SearchAsync(null, null, 1));
        Assert.Contains("rejected", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DlpsPlatformsAreListedWithoutNetwork()
    {
        using var handler = new Handler(_ => throw new InvalidOperationException("DLPS platforms must not hit the network."));
        using var http = new HttpClient(handler);
        var provider = new DlpsCatalogService(http);

        var platforms = await provider.GetPlatformsAsync();

        Assert.Equal("dlps_downloader", provider.Name);
        Assert.Equal("https://github.com/jayjun911/dlps_downloader", provider.WebsiteUrl);
        Assert.True(provider.IsConfigured);
        Assert.False(provider.NeedsCredentials);
        Assert.Contains("PlayStation 5", platforms);
        Assert.Contains("PlayStation 4", platforms);
        Assert.Contains("Nintendo Switch", platforms);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task DlpsBrowseParsesListingHtmlAndPaginates()
    {
        const string mockPageJson = """
            [{"id":27016,"slug":"list-all-game-ps4","content":{"rendered":"<ol class=\"display-posts-listing\"><li class=\"listing-item\"><a class=\"title\" href=\"https://dlpsgame.com/killallzombies-ps4-download-free/\">#KILLALLZOMBIES</a></li><li class=\"listing-item\"><a class=\"title\" href=\"https://dlpsgame.com/0-degrees-ps4-pkg/\">0 Degrees</a></li></ol>"}}]
            """;

        using var handler = new Handler(req =>
        {
            if (req.RequestUri!.AbsolutePath.Contains("pages"))
                return Json(mockPageJson);
            return Json("[]");
        });
        using var http = new HttpClient(handler);
        var provider = new DlpsCatalogService(http);

        var page = await provider.SearchAsync("PlayStation 4", null, 1);

        Assert.Equal(2, page.Total);
        Assert.False(page.HasMore);
        Assert.Equal(2, page.Items.Count);
        Assert.Equal("#KILLALLZOMBIES", page.Items[0].Name);
        Assert.Equal("killallzombies-ps4-download-free", page.Items[0].Id);
        Assert.Equal("https://dlpsgame.com/killallzombies-ps4-download-free/", page.Items[0].PageUrl);
        Assert.Equal("PlayStation 4", page.Items[0].Platform);
    }

    [Fact]
    public async Task DlpsSearchPostsParsesResultsAndCovers()
    {
        const string mockSearchJson = """
            [
              {
                "id": 12345,
                "slug": "spider-man-ps4",
                "title": {"rendered": "Marvel&#8217;s Spider-Man"},
                "link": "https://dlpsgame.com/spider-man-ps4/",
                "yoast_head_json": {
                  "og_image": [{"url": "https://dlpsgame.com/wp-content/uploads/spiderman.jpg"}]
                },
                "content": {"rendered": "RELEASE: 2018"}
              }
            ]
            """;

        using var handler = new Handler(_ => Json(mockSearchJson));
        using var http = new HttpClient(handler);
        var provider = new DlpsCatalogService(http);

        var page = await provider.SearchAsync("PlayStation 4", "spider", 1);

        Assert.Single(page.Items);
        var game = page.Items[0];
        Assert.Equal("Marvel’s Spider-Man", game.Name);
        Assert.Equal("https://dlpsgame.com/wp-content/uploads/spiderman.jpg", game.CoverUrl);
        Assert.Equal(2018, game.Year);
        Assert.Equal("https://dlpsgame.com/spider-man-ps4/", game.PageUrl);
    }

    [Fact]
    public async Task DlpsUnknownPlatformFailsWithFriendlyMessage()
    {
        var provider = new DlpsCatalogService();
        var error = await Assert.ThrowsAsync<CatalogProviderException>(() => provider.SearchAsync("Dreamcast", null, 1));
        Assert.Contains("Dreamcast", error.Message);
    }

    [Fact]
    public void EmulatedRegistryHasDlpsFirst()
    {
        var dlps = new DlpsCatalogService();
        var registry = new EmulatedCatalogProviderRegistry(dlps);

        Assert.Equal("dlps_downloader", registry.Providers[0].Name);
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = new();
        public List<string> Bodies { get; } = new();
        public List<string> Headers { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            Headers.Add(string.Join("; ", request.Headers.Select(header => $"{header.Key}: {string.Join(",", header.Value)}")));
            if (request.Content is not null) Bodies.Add(await request.Content.ReadAsStringAsync(cancellationToken));
            return respond(request);
        }
    }
}
