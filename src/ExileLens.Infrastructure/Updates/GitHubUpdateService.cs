using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using ExileLens.Application.Storage;
using ExileLens.Application.Updates;
using Microsoft.Extensions.Logging;

namespace ExileLens.Infrastructure.Updates;

/// <summary>
/// Checks GitHub Releases for a newer semantic version with an asset for this OS/architecture, downloads it
/// to the cache folder and verifies the SHA-256 published in the release's SHA256SUMS.txt. Installation is
/// only started after explicit user confirmation (handled by the UI). User data lives outside the install
/// folder, so settings and profiles survive updates.
/// </summary>
public sealed class GitHubUpdateService(HttpClient http, IAppPaths paths, ILogger<GitHubUpdateService> logger) : IUpdateService
{
    public const string Repository = "IgorBabayan/exile-lens";
    private const string ChecksumAsset = "SHA256SUMS.txt";

    public SemanticVersion CurrentVersion { get; } = ReadCurrentVersion();

    public async Task<UpdateInfo?> CheckAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Repository}/releases/latest");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            logger.LogInformation("Update check returned {Status}", (int)response.StatusCode);
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        var release = ParseRelease(doc.RootElement, out var checksumUrl);
        if (release is null || release.Value.Version <= CurrentVersion)
        {
            return null;
        }

        var (version, tag, name, notes, htmlUrl, assets) = release.Value;
        var asset = PlatformAssetMatcher.Select(assets, PlatformAssetMatcher.CurrentRid(), PlatformAssetMatcher.PreferredSuffixes());
        if (asset is null)
        {
            return null;
        }

        string? sha = null;
        if (checksumUrl is not null)
        {
            var sums = await http.GetStringAsync(checksumUrl, cancellationToken).ConfigureAwait(false);
            sha = PlatformAssetMatcher.FindChecksum(sums, asset.Name);
        }

        return new UpdateInfo(version, tag, name, notes, htmlUrl, asset, sha);
    }

    public async Task<string> DownloadAsync(UpdateInfo update, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        if (update.ExpectedSha256 is null)
        {
            throw new InvalidOperationException("The release has no checksum for this package; refusing to install an unverified download.");
        }

        var dir = Path.Combine(paths.CacheDirectory, "updates");
        Directory.CreateDirectory(dir);
        var target = Path.Combine(dir, update.Asset.Name);
        var temp = target + ".part";

        using (var response = await http.GetAsync(update.Asset.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
        {
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? update.Asset.Size;
            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous);
            using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[81920];
            long read = 0;
            int n;
            while ((n = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                hasher.AppendData(buffer, 0, n);
                await file.WriteAsync(buffer.AsMemory(0, n), cancellationToken).ConfigureAwait(false);
                read += n;
                if (total > 0)
                {
                    progress?.Report((double)read / total);
                }
            }

            var actual = Convert.ToHexStringLower(hasher.GetHashAndReset());
            if (!string.Equals(actual, update.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                file.Close();
                File.Delete(temp);
                throw new InvalidDataException("Downloaded update failed checksum verification.");
            }
        }

        File.Move(temp, target, overwrite: true);
        return target;
    }

    public void Install(string downloadedPath)
    {
        ProcessStartInfo psi;
        if (OperatingSystem.IsWindows() && downloadedPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            psi = new ProcessStartInfo(downloadedPath) { UseShellExecute = true };
        }
        else if (OperatingSystem.IsMacOS())
        {
            psi = new ProcessStartInfo("open", [downloadedPath]);
        }
        else if (OperatingSystem.IsLinux())
        {
            // AppImage/tarball: reveal the file; the user replaces the old AppImage.
            psi = new ProcessStartInfo("xdg-open", [Path.GetDirectoryName(downloadedPath)!]);
        }
        else
        {
            psi = new ProcessStartInfo(Path.GetDirectoryName(downloadedPath)!) { UseShellExecute = true };
        }

        Process.Start(psi)?.Dispose();
    }

    internal static (SemanticVersion Version, string Tag, string Name, string Notes, string HtmlUrl, List<UpdateAsset> Assets)? ParseRelease(JsonElement root, out string? checksumUrl)
    {
        checksumUrl = null;
        if (root.TryGetProperty("draft", out var draft) && draft.GetBoolean())
        {
            return null;
        }

        var tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
        if (!SemanticVersion.TryParse(tag, out var version))
        {
            return null;
        }

        var assets = new List<UpdateAsset>();
        if (root.TryGetProperty("assets", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in arr.EnumerateArray())
            {
                var name = a.GetProperty("name").GetString() ?? string.Empty;
                var url = a.GetProperty("browser_download_url").GetString() ?? string.Empty;
                var size = a.TryGetProperty("size", out var s) ? s.GetInt64() : 0;
                if (name == ChecksumAsset)
                {
                    checksumUrl = url;
                }
                else
                {
                    assets.Add(new UpdateAsset(name, url, size));
                }
            }
        }

        return (version, tag!,
            root.TryGetProperty("name", out var n) ? n.GetString() ?? tag! : tag!,
            root.TryGetProperty("body", out var b) ? b.GetString() ?? string.Empty : string.Empty,
            root.TryGetProperty("html_url", out var h) ? h.GetString() ?? string.Empty : string.Empty,
            assets);
    }

    private static SemanticVersion ReadCurrentVersion()
    {
        var info = typeof(GitHubUpdateService).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        return SemanticVersion.TryParse(info, out var v) ? v : new SemanticVersion(0, 0, 0, null);
    }
}
