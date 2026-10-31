using System.Net;
using System.Net.Http;
using System.Text;
using Steamy.Services;

namespace Steamy.UiTests;

public class ImageRedirectsTests
{
    private static HttpResponseMessage Reply(HttpStatusCode status, string? location = null, string body = "")
    {
        var response = new HttpResponseMessage(status);
        if (location is not null) response.Headers.Location = new Uri(location, UriKind.RelativeOrAbsolute);
        response.Content = new StringContent(body, Encoding.UTF8, "text/plain");
        return response;
    }

    private sealed class FakeGet
    {
        public readonly List<Uri> Requests = [];
        private readonly Queue<Func<Uri, HttpResponseMessage>> _replies = new();
        public void Enqueue(Func<Uri, HttpResponseMessage> reply) => _replies.Enqueue(reply);
        public Task<HttpResponseMessage> GetAsync(Uri uri)
        {
            Requests.Add(uri);
            return Task.FromResult(_replies.Dequeue()(uri));
        }
    }

    [Fact]
    public async Task FollowsRedirectChainToRelativeHttpsTarget()
    {
        var fake = new FakeGet();
        fake.Enqueue(_ => Reply(HttpStatusCode.Redirect, "/media.gif"));
        fake.Enqueue(_ => Reply(HttpStatusCode.OK, body: "gif-bytes"));
        using var response = await ImageRedirects.FollowAsync(fake.GetAsync, new Uri("https://tenor.example/view/page"), CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("gif-bytes", await response.Content.ReadAsStringAsync(CancellationToken.None));
        Assert.Equal(2, fake.Requests.Count);
        Assert.Equal("https://tenor.example/media.gif", fake.Requests[1].AbsoluteUri);
    }

    [Fact]
    public async Task StopsFollowingAtHopLimit()
    {
        var fake = new FakeGet();
        for (var hop = 0; hop <= ImageRedirects.MaxHops; hop++)
            fake.Enqueue(_ => Reply(HttpStatusCode.Redirect, "https://tenor.example/next"));
        using var response = await ImageRedirects.FollowAsync(fake.GetAsync, new Uri("https://tenor.example/start"), CancellationToken.None);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(ImageRedirects.MaxHops + 1, fake.Requests.Count);
    }

    [Fact]
    public async Task RefusesNonHttpsRedirectTarget()
    {
        var fake = new FakeGet();
        fake.Enqueue(_ => Reply(HttpStatusCode.Redirect, "http://insecure.example/payload"));
        using var response = await ImageRedirects.FollowAsync(fake.GetAsync, new Uri("https://safe.example/page"), CancellationToken.None);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Single(fake.Requests);
    }

    [Fact]
    public async Task ReturnsRedirectWithoutLocationHeader()
    {
        var fake = new FakeGet();
        fake.Enqueue(_ => Reply(HttpStatusCode.Found));
        using var response = await ImageRedirects.FollowAsync(fake.GetAsync, new Uri("https://safe.example/page"), CancellationToken.None);
        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Single(fake.Requests);
    }
}
