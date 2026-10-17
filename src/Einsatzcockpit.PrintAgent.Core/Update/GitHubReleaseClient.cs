using System.Net;
using System.Text.Json;

namespace Einsatzcockpit.PrintAgent.Core.Update;

public sealed record ReleaseInfo(string Version, Uri ZipUrl, Uri Sha256SumsUrl);

public sealed class GitHubReleaseClient(HttpClient client)
{
    public async Task<ReleaseInfo?> GetLatestAsync(string repository, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"https://api.github.com/repos/{repository}/releases/latest");
        request.Headers.UserAgent.ParseAdd("EinsatzcockpitPrintAgent/1.0");
        using var response = await client.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
        return ParseRelease(document.RootElement);
    }

    public static ReleaseInfo? ParseRelease(JsonElement release)
    {
        if (release.TryGetProperty("draft", out var draft) && draft.GetBoolean()
            || release.TryGetProperty("prerelease", out var prerelease) && prerelease.GetBoolean())
        {
            return null;
        }

        var tag = release.TryGetProperty("tag_name", out var tagName) ? tagName.GetString() : null;
        if (!CalVerVersion.TryParse(tag, out var version) || version.IsDevelopment
            || !release.TryGetProperty("assets", out var assets))
        {
            return null;
        }

        Uri? zip = null;
        Uri? sums = null;
        foreach (var asset in assets.EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString();
            var url = asset.GetProperty("browser_download_url").GetString();
            if (url is null)
            {
                continue;
            }

            if (name?.EndsWith("-win-x64.zip", StringComparison.OrdinalIgnoreCase) == true)
            {
                zip = new Uri(url);
            }
            else if (string.Equals(name, "SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase))
            {
                sums = new Uri(url);
            }
        }

        return zip is not null && sums is not null ? new ReleaseInfo(version.ToString(), zip, sums) : null;
    }
}
