using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Steamy.Services;

namespace Steamy.Tests;

public sealed class UpdateServiceTests
{
    private static string Description(string digest = "sha256:" + "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa") =>
        $$"""{"tag_name":"v0.4.16","draft":false,"prerelease":false,"published_at":"2026-10-02T15:02:00Z","assets":[{"name":"Steamy-v0.4.16.zip","browser_download_url":"https://github.com/Zein090449/Steamy/releases/download/v0.4.16/Steamy-v0.4.16.zip","size":100,"digest":"{{digest}}"}]}""";

    [Theory]
    [InlineData(403)]
    [InlineData(429)]
    [InlineData(500)]
    [InlineData(503)]
    public async Task BlockedApiUsesPublishedManifestWithoutCredentials(int status)
    {
        using var handler = new Handler((uri, _) => uri.Host == "api.github.com"
            ? new((HttpStatusCode)status) : Json(Description()));
        using var http = new HttpClient(handler);
        using var service = new GitHubUpdateService(http, currentVersion: new(0, 4, 12));
        var update = await service.CheckAsync();
        Assert.Equal(new Version(0, 4, 16), update!.Version);
        Assert.Equal(64, update.Sha256!.Length);
        Assert.Equal(GitHubUpdateService.PublishedManifestUri, handler.Requests[1]);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task ApiConnectionFailureStillFindsThePublishedUpdate()
    {
        using var handler = new Handler((uri, _) => uri.Host == "api.github.com" ? throw new HttpRequestException("Offline API") : Json(Description()));
        using var http = new HttpClient(handler);
        using var service = new GitHubUpdateService(http, currentVersion: new(0, 4, 12));
        Assert.NotNull(await service.CheckAsync());
    }

    [Fact]
    public async Task SuccessfulApiRetainsChecksumAndDoesNotRequestFallback()
    {
        using var handler = new Handler((_, _) => Json(Description()));
        using var http = new HttpClient(handler);
        using var service = new GitHubUpdateService(http, currentVersion: new(0, 4, 12));
        Assert.NotNull((await service.CheckAsync())!.Sha256);
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData("digest")]
    [InlineData("draft")]
    [InlineData("prerelease")]
    [InlineData("external")]
    [InlineData("size")]
    [InlineData("published")]
    public async Task InvalidFallbackNeverReportsUpToDate(string failure)
    {
        var data = JsonNode.Parse(Description())!;
        var asset = data["assets"]![0]!;
        switch (failure)
        {
            case "digest": asset["digest"] = "sha256:invalid"; break;
            case "draft": data["draft"] = true; break;
            case "prerelease": data["prerelease"] = true; break;
            case "external": asset["browser_download_url"] = "https://example.invalid/Steamy-v0.4.16.zip"; break;
            case "size": asset["size"] = 0; break;
            case "published": data["published_at"] = "unknown"; break;
        }
        using var handler = new Handler((uri, _) => uri.Host == "api.github.com" ? new(HttpStatusCode.Forbidden) : Json(data.ToJsonString()));
        using var http = new HttpClient(handler);
        using var service = new GitHubUpdateService(http, currentVersion: new(0, 4, 16));
        await Assert.ThrowsAsync<UpdateException>(() => service.CheckAsync());
    }

    [Fact]
    public async Task BothSourcesFailWithAnActionableErrorInsteadOfFalseSuccess()
    {
        using var handler = new Handler((_, _) => new(HttpStatusCode.Forbidden));
        using var http = new HttpClient(handler);
        using var service = new GitHubUpdateService(http, currentVersion: new(0, 4, 12));
        var error = await Assert.ThrowsAsync<UpdateException>(() => service.CheckAsync());
        Assert.Contains("releases/latest", error.Message);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task CancelledCheckDoesNotStartFallback()
    {
        using var cancellation = new CancellationTokenSource();
        using var handler = new Handler((_, token) => { cancellation.Cancel(); token.ThrowIfCancellationRequested(); return Json(Description()); });
        using var http = new HttpClient(handler);
        using var service = new GitHubUpdateService(http);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.CheckAsync(cancellation.Token));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task OversizedFallbackIsRejected()
    {
        using var handler = new Handler((uri, _) => uri.Host == "api.github.com" ? new(HttpStatusCode.Forbidden) : Json(new string(' ', 256 * 1024 + 1)));
        using var http = new HttpClient(handler);
        using var service = new GitHubUpdateService(http);
        await Assert.ThrowsAsync<UpdateException>(() => service.CheckAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DownloadChecksumIsCheckedBeforeReplacingAnyInstalledFile(bool correct)
    {
        var folder = Path.Combine(Path.GetTempPath(), "Steamy-update-" + Guid.NewGuid().ToString("N"));
        var install = Path.Combine(folder, "app");
        var work = Path.Combine(folder, "work");
        Directory.CreateDirectory(install);
        var original = Path.Combine(install, "Steamy.exe");
        await File.WriteAllTextAsync(original, "original executable");
        try
        {
            using var archive = new MemoryStream();
            using (var zip = new ZipArchive(archive, ZipArchiveMode.Create, leaveOpen: true))
            using (var writer = new StreamWriter(zip.CreateEntry("Steamy.exe").Open())) writer.Write("new executable");
            var bytes = archive.ToArray();
            var checksum = Convert.ToHexString(SHA256.HashData(bytes));
            using var handler = new Handler((_, _) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
            using var http = new HttpClient(handler);
            using var service = new GitHubUpdateService(http, install, work);
            var update = new UpdateInfo(new(0, 4, 16), "v0.4.16", new("https://github.com/Zein090449/Steamy/releases/download/v0.4.16/Steamy-v0.4.16.zip"), bytes.Length,
                correct ? checksum : new string('0', 64));
            if (correct)
            {
                await service.InstallAsync(update);
                Assert.Equal("new executable", await File.ReadAllTextAsync(original));
            }
            else
            {
                await Assert.ThrowsAsync<UpdateException>(() => service.InstallAsync(update));
                Assert.Equal("original executable", await File.ReadAllTextAsync(original));
                Assert.False(Directory.Exists(Path.Combine(work, "staging")));
            }
            Assert.False(File.Exists(Path.Combine(work, "Steamy-v0.4.16.zip")));
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public void ReleaseParsingCarriesTheIncrementalAsset()
    {
        var data = JsonNode.Parse(Description())!;
        data["assets"]!.AsArray().Add(new JsonObject
        {
            ["name"] = "Steamy-v0.4.16-delta.zip",
            ["browser_download_url"] = "https://github.com/Zein090449/Steamy/releases/download/v0.4.16/Steamy-v0.4.16-delta.zip",
            ["size"] = 40,
            ["digest"] = "sha256:" + new string('b', 64),
        });
        using var document = System.Text.Json.JsonDocument.Parse(data.ToJsonString());
        var update = GitHubUpdateService.ParseRelease(document.RootElement, new Version(0, 4, 15))!;
        Assert.NotNull(update.Delta);
        Assert.Equal(40, update.Delta!.AssetSize);
        Assert.Equal("sha256:" + new string('b', 64), "sha256:" + update.Delta.Sha256);
    }

    [Fact]
    public void PublishedManifestMayIncludeTheIncrementalAsset()
    {
        var data = JsonNode.Parse(Description())!;
        data["assets"]!.AsArray().Add(new JsonObject
        {
            ["name"] = "Steamy-v0.4.16-delta.zip",
            ["browser_download_url"] = "https://github.com/Zein090449/Steamy/releases/download/v0.4.16/Steamy-v0.4.16-delta.zip",
            ["size"] = 40,
            ["digest"] = "sha256:" + new string('b', 64),
        });
        using var document = System.Text.Json.JsonDocument.Parse(data.ToJsonString());
        var update = GitHubUpdateService.ParsePublishedManifest(document.RootElement, new Version(0, 4, 15))!;
        Assert.NotNull(update.Delta);
        Assert.EndsWith("/Steamy-v0.4.16-delta.zip", update.Delta!.AssetUrl.AbsoluteUri);
    }

    [Fact]
    public async Task IncrementalPackageReplacesOnlyChangedFiles()
    {
        var folder = Path.Combine(Path.GetTempPath(), "Steamy-delta-" + Guid.NewGuid().ToString("N"));
        var install = Path.Combine(folder, "app");
        var work = Path.Combine(folder, "work");
        Directory.CreateDirectory(install);
        await File.WriteAllTextAsync(Path.Combine(install, "Steamy.exe"), "old executable");
        await File.WriteAllTextAsync(Path.Combine(install, "unchanged.dll"), "same bytes");
        await File.WriteAllTextAsync(Path.Combine(install, "removed.dll"), "gone next release");
        try
        {
            var executable = "new executable";
            var manifest = $$"""{"format":1,"tag":"v0.4.16","baseTag":"v0.4.15","files":[{"path":"Steamy.exe","sha256":"{{Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(executable)))}}","size":{{System.Text.Encoding.UTF8.GetByteCount(executable)}}}],"deleted":["removed.dll"]}""";
            var deltaBytes = Zip(("delta-manifest.json", manifest), ("Steamy.exe", executable));
            var deltaUri = new Uri("https://example.test/Steamy-v0.4.16-delta.zip");
            using var handler = new Handler((_, _) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(deltaBytes) });
            using var http = new HttpClient(handler);
            using var service = new GitHubUpdateService(http, install, work, currentVersion: new(0, 4, 15));
            var update = new UpdateInfo(new(0, 4, 16), "v0.4.16", new("https://example.test/Steamy-v0.4.16.zip"), 999, null,
                new DeltaUpdate(deltaUri, deltaBytes.Length, Convert.ToHexString(SHA256.HashData(deltaBytes))));
            await service.InstallAsync(update);
            Assert.Equal(executable, await File.ReadAllTextAsync(Path.Combine(install, "Steamy.exe")));
            Assert.Equal("same bytes", await File.ReadAllTextAsync(Path.Combine(install, "unchanged.dll")));
            Assert.False(File.Exists(Path.Combine(install, "removed.dll")));
            Assert.Single(handler.Requests);
            Assert.False(Directory.Exists(Path.Combine(work, "staging")));
            Assert.False(Directory.Exists(Path.Combine(work, "delta-staging")));
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public async Task IncrementalPackageWithAnotherBaseFallsBackToTheFullDownload()
    {
        var folder = Path.Combine(Path.GetTempPath(), "Steamy-delta-base-" + Guid.NewGuid().ToString("N"));
        var install = Path.Combine(folder, "app");
        var work = Path.Combine(folder, "work");
        Directory.CreateDirectory(install);
        await File.WriteAllTextAsync(Path.Combine(install, "Steamy.exe"), "old executable");
        try
        {
            var deltaBytes = Zip(("delta-manifest.json", """{"format":1,"tag":"v0.4.16","baseTag":"v0.4.14","files":[],"deleted":[]}"""));
            var fullBytes = Zip(("Steamy.exe", "full executable"));
            var deltaUri = new Uri("https://example.test/Steamy-v0.4.16-delta.zip");
            var fullUri = new Uri("https://example.test/Steamy-v0.4.16.zip");
            using var handler = new Handler((uri, _) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(uri == deltaUri ? deltaBytes : fullBytes) });
            using var http = new HttpClient(handler);
            using var service = new GitHubUpdateService(http, install, work, currentVersion: new(0, 4, 15));
            var update = new UpdateInfo(new(0, 4, 16), "v0.4.16", fullUri, fullBytes.Length, Convert.ToHexString(SHA256.HashData(fullBytes)),
                new DeltaUpdate(deltaUri, deltaBytes.Length, Convert.ToHexString(SHA256.HashData(deltaBytes))));
            await service.InstallAsync(update);
            Assert.Equal("full executable", await File.ReadAllTextAsync(Path.Combine(install, "Steamy.exe")));
            Assert.Equal(new[] { deltaUri, fullUri }, handler.Requests);
        }
        finally { Directory.Delete(folder, true); }
    }

    private static byte[] Zip(params (string Path, string Text)[] files)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (path, text) in files)
            {
                using var stream = archive.CreateEntry(path).Open();
                using var writer = new StreamWriter(stream);
                writer.Write(text);
            }
        return buffer.ToArray();
    }

    private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK) { Content = new StringContent(value) };
    private sealed class Handler(Func<Uri, CancellationToken, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Null(request.Headers.Authorization);
            Requests.Add(request.RequestUri!);
            return Task.FromResult(respond(request.RequestUri!, cancellationToken));
        }
    }
}
