using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;

namespace Steamy.Services;

public interface IFileUploadService
{
    /// <summary>Uploads one local file and returns a public link Discord can show.</summary>
    Task<string> UploadAsync(string path, CancellationToken cancellationToken = default);
}

/// <summary>
/// Discord's Social SDK only sends text, so chat attachments are hosted on catbox.moe and shared
/// as a link: Discord embeds images and GIFs inline, and other files show as a download link.
/// </summary>
public sealed class CatboxFileUploadService : IFileUploadService
{
    public const long MaxBytes = 200 * 1024 * 1024;
    private static readonly Uri Endpoint = new("https://catbox.moe/user/api.php");

    private readonly HttpClient _http;

    public CatboxFileUploadService(HttpClient? http = null)
    {
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
    }

    public async Task<string> UploadAsync(string path, CancellationToken cancellationToken = default)
    {
        var file = new FileInfo(path);
        if (!file.Exists) throw new IOException("The file no longer exists.");
        if (file.Length > MaxBytes) throw new InvalidOperationException($"{file.Name} is larger than 200 MB, the most this file host accepts.");

        using var content = new MultipartFormDataContent();
        content.Add(new StringContent("fileupload"), "reqtype");
        await using var stream = file.OpenRead();
        var upload = new StreamContent(stream);
        upload.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Add(upload, "fileToUpload", file.Name);

        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint) { Content = content };
        // Cloudflare in front of catbox closes connections that arrive without a User-Agent.
        request.Headers.UserAgent.ParseAdd("Steamy/0.8");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var link = (await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false)).Trim();
        if (!response.IsSuccessStatusCode || !Uri.TryCreate(link, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps || uri.Host != "files.catbox.moe")
            throw new InvalidOperationException("The file host didn't accept the upload. Try again in a moment.");
        return uri.AbsoluteUri;
    }
}
