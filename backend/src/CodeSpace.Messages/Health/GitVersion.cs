using System.Text.RegularExpressions;

namespace CodeSpace.Messages.Health;

/// <summary>
/// A git release as <c>git --version</c> prints it: major, minor, patch. Vendor and pre-release suffixes
/// (<c>(Apple Git-154)</c>, <c>.windows.1</c>, <c>-rc1</c>) are dropped, a two-part release reads as patch 0, and anything
/// that is not <c>git version N.N</c> is unknown (<c>null</c>) rather than a guess — a host whose release cannot be read
/// must never be called too old.
/// </summary>
public readonly partial record struct GitVersion(int Major, int Minor, int Patch) : IComparable<GitVersion>
{
    public static GitVersion? Parse(string? versionOutput)
    {
        if (string.IsNullOrWhiteSpace(versionOutput)) return null;

        var match = ReleasePattern().Match(versionOutput);

        if (!match.Success) return null;

        var patch = match.Groups["patch"].Success ? int.Parse(match.Groups["patch"].Value) : 0;

        return new GitVersion(int.Parse(match.Groups["major"].Value), int.Parse(match.Groups["minor"].Value), patch);
    }

    public int CompareTo(GitVersion other) => (Major, Minor, Patch).CompareTo((other.Major, other.Minor, other.Patch));

    public bool IsAtLeast(GitVersion minimum) => CompareTo(minimum) >= 0;

    public override string ToString() => $"{Major}.{Minor}.{Patch}";

    [GeneratedRegex(@"^\s*git version (?<major>\d+)\.(?<minor>\d+)(?:\.(?<patch>\d+))?", RegexOptions.CultureInvariant)]
    private static partial Regex ReleasePattern();
}
