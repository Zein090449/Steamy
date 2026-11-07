using System.Net;
using Steamy.Services;

namespace Steamy.Tests;

public sealed class FileUploadServiceTests
{
    [Fact]
    public async Task UploadReturnsTheHostedLink()
    {
        var file = TempFile("screenshot.png", "image bytes");
        try
        {
            using var handler = new Handler(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent("https://files.catbox.moe/abc123.png") });
            using var http = new HttpClient(handler);
            var service = new CatboxFileUploadService(http);
            Assert.Equal("https://files.catbox.moe/abc123.png", await service.UploadAsync(file));
            Assert.Contains("fileupload", handler.Body);
            Assert.Contains("fileToUpload", handler.Body);
            // Catbox sits behind Cloudflare, which drops requests that carry no User-Agent.
            Assert.Equal("Steamy/0.8", handler.UserAgent);
            Assert.Contains("*/*", handler.Accept);
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public async Task UploadRejectsALinkFromAnotherHost()
    {
        var file = TempFile("file.zip", "bytes");
        try
        {
            using var handler = new Handler(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent("https://example.invalid/file.zip") });
            using var http = new HttpClient(handler);
            var service = new CatboxFileUploadService(http);
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.UploadAsync(file));
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public async Task UploadRefusesFilesBeyondTheHostLimit()
    {
        var file = TempFile("huge.bin", "");
        try
        {
            using (var stream = new FileStream(file, FileMode.Open, FileAccess.Write)) stream.SetLength(CatboxFileUploadService.MaxBytes + 1);
            using var handler = new Handler(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("https://files.catbox.moe/") });
            using var http = new HttpClient(handler);
            var service = new CatboxFileUploadService(http);
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.UploadAsync(file));
            Assert.Contains("200 MB", error.Message);
        }
        finally { File.Delete(file); }
    }

    private static string TempFile(string name, string content)
    {
        var folder = Path.Combine(Path.GetTempPath(), "Steamy-upload-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, name);
        File.WriteAllText(path, content);
        return path;
    }

    private sealed class Handler(HttpResponseMessage response) : HttpMessageHandler
    {
        public string Body { get; private set; } = "";
        public string UserAgent { get; private set; } = "";
        public string Accept { get; private set; } = "";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            UserAgent = request.Headers.UserAgent.ToString();
            Accept = string.Join(" ", request.Headers.Accept);
            return response;
        }
    }
}
