using System.Globalization;
using System.Runtime.InteropServices;

namespace ExileLens.Application.Updates;

/// <summary>Minimal SemVer 2.0 (major.minor.patch[-prerelease]) with correct precedence rules.</summary>
public readonly record struct SemanticVersion(int Major, int Minor, int Patch, string? PreRelease) : IComparable<SemanticVersion>
{
    public static bool TryParse(string? text, out SemanticVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var span = text.AsSpan().Trim();
        if (span.Length > 0 && (span[0] == 'v' || span[0] == 'V'))
        {
            span = span[1..];
        }

        var plus = span.IndexOf('+');
        if (plus >= 0)
        {
            span = span[..plus];
        }

        string? pre = null;
        var dash = span.IndexOf('-');
        if (dash >= 0)
        {
            pre = span[(dash + 1)..].ToString();
            span = span[..dash];
        }

        Span<Range> parts = stackalloc Range[4];
        var n = span.Split(parts, '.');
        if (n is < 2 or > 3)
        {
            return false;
        }

        if (!int.TryParse(span[parts[0]], NumberStyles.None, CultureInfo.InvariantCulture, out var major)
            || !int.TryParse(span[parts[1]], NumberStyles.None, CultureInfo.InvariantCulture, out var minor))
        {
            return false;
        }

        var patch = 0;
        if (n == 3 && !int.TryParse(span[parts[2]], NumberStyles.None, CultureInfo.InvariantCulture, out patch))
        {
            return false;
        }

        version = new SemanticVersion(major, minor, patch, string.IsNullOrEmpty(pre) ? null : pre);
        return true;
    }

    public int CompareTo(SemanticVersion other)
    {
        var c = Major.CompareTo(other.Major);
        if (c != 0)
        {
            return c;
        }

        c = Minor.CompareTo(other.Minor);
        if (c != 0)
        {
            return c;
        }

        c = Patch.CompareTo(other.Patch);
        if (c != 0)
        {
            return c;
        }

        if (PreRelease is null)
        {
            return other.PreRelease is null ? 0 : 1;
        }

        if (other.PreRelease is null)
        {
            return -1;
        }

        var a = PreRelease.Split('.');
        var b = other.PreRelease.Split('.');
        for (var i = 0; i < Math.Min(a.Length, b.Length); i++)
        {
            var aNum = int.TryParse(a[i], NumberStyles.None, CultureInfo.InvariantCulture, out var ai);
            var bNum = int.TryParse(b[i], NumberStyles.None, CultureInfo.InvariantCulture, out var bi);
            c = (aNum, bNum) switch
            {
                (true, true) => ai.CompareTo(bi),
                (true, false) => -1,
                (false, true) => 1,
                _ => string.CompareOrdinal(a[i], b[i]),
            };
            if (c != 0)
            {
                return c;
            }
        }

        return a.Length.CompareTo(b.Length);
    }

    public static bool operator >(SemanticVersion l, SemanticVersion r) => l.CompareTo(r) > 0;
    public static bool operator <(SemanticVersion l, SemanticVersion r) => l.CompareTo(r) < 0;
    public static bool operator >=(SemanticVersion l, SemanticVersion r) => l.CompareTo(r) >= 0;
    public static bool operator <=(SemanticVersion l, SemanticVersion r) => l.CompareTo(r) <= 0;

    public override string ToString() => PreRelease is null ? $"{Major}.{Minor}.{Patch}" : $"{Major}.{Minor}.{Patch}-{PreRelease}";
}

public sealed record UpdateAsset(string Name, string DownloadUrl, long Size);

public sealed record UpdateInfo(SemanticVersion Version, string Tag, string Name, string ReleaseNotes, string HtmlUrl, UpdateAsset Asset, string? ExpectedSha256);

public interface IUpdateService
{
    SemanticVersion CurrentVersion { get; }

    /// <summary>Returns a newer release with an asset for this platform/architecture, or null.</summary>
    Task<UpdateInfo?> CheckAsync(CancellationToken cancellationToken);

    /// <summary>Downloads the asset and verifies its SHA-256 against the release checksum file. Returns the local path.</summary>
    Task<string> DownloadAsync(UpdateInfo update, IProgress<double>? progress, CancellationToken cancellationToken);

    /// <summary>Starts the installer / reveals the package. Settings live in the user data folder and are untouched.</summary>
    void Install(string downloadedPath);
}

public static class PlatformAssetMatcher
{
    /// <summary>Release asset naming convention: ExileLens-{version}-{rid}{suffix}.</summary>
    public static string CurrentRid()
    {
        var arch = RuntimeInformation.OSArchitecture switch
        {
            Architecture.Arm64 => "arm64",
            _ => "x64",
        };
        if (OperatingSystem.IsWindows())
        {
            return $"win-{arch}";
        }

        return OperatingSystem.IsMacOS() ? $"osx-{arch}" : $"linux-{arch}";
    }

    /// <summary>Preferred installable formats per OS, best first.</summary>
    public static IReadOnlyList<string> PreferredSuffixes() =>
        OperatingSystem.IsWindows() ? ["-setup.exe", ".zip"]
        : OperatingSystem.IsMacOS() ? [".dmg", ".zip"]
        : [".AppImage", ".tar.gz"];

    public static UpdateAsset? Select(IEnumerable<UpdateAsset> assets, string rid, IReadOnlyList<string> suffixes)
    {
        var list = assets.Where(a => a.Name.Contains(rid, StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var suffix in suffixes)
        {
            var match = list.FirstOrDefault(a => a.Name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                return match;
            }
        }

        return null;
    }

    /// <summary>Parses a "sha256sum"-style file ("&lt;hex&gt;  &lt;file&gt;") and returns the hash for <paramref name="fileName"/>.</summary>
    public static string? FindChecksum(string checksumFile, string fileName)
    {
        foreach (var line in checksumFile.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var space = line.IndexOf(' ');
            if (space <= 0)
            {
                continue;
            }

            var name = line[(space + 1)..].TrimStart(' ', '*');
            if (string.Equals(name, fileName, StringComparison.Ordinal))
            {
                return line[..space].ToLowerInvariant();
            }
        }

        return null;
    }
}
