namespace CodeSpace.Core.Services.Workflows.Artifacts.Providers.Local.Legacy;

/// <summary>
/// The pre-CAS local layout, as a pure function of the digest.
///
/// <para>It MIRRORS <see cref="Backends.LocalFileArtifactBlobBackend"/>: that backend places
/// <c>&lt;root&gt;/&lt;aa&gt;/&lt;bb&gt;/&lt;sha&gt;</c> and records the resulting <c>file://</c> url as the row's
/// <c>storage_url</c>. It is deliberately NOT
/// <see cref="LocalRwxArtifactStorageDriverFactory"/>'s layout, which interposes an <c>objects</c> segment of its own
/// — a different tier, at different paths, holding different bytes.</para>
///
/// <para>Every prediction is checked back against the locator the row already carries
/// (<see cref="NamesTheSameFile"/>), because a layout is the one thing here that cannot be verified by inspection: it
/// either reproduces a url the deployment wrote years ago or it does not.</para>
/// </summary>
public static class LegacyLocalObjectKeys
{
    /// <summary>
    /// The object key this layout gives <paramref name="sha256"/>, or null when the digest is not one the backend
    /// could ever have placed. The guard is the backend's own: 64 hex characters, and no case folding — that backend
    /// derives its directories from the digest verbatim, so a key that lowercased one would name a different path.
    /// </summary>
    public static string? For(string? sha256)
    {
        if (sha256 is not { Length: 64 } || !sha256.All(Uri.IsHexDigit)) return null;

        return $"{sha256[..2]}/{sha256.Substring(2, 2)}/{sha256}";
    }

    /// <summary>
    /// Whether <paramref name="objectKey"/> under <paramref name="rootPath"/> is the very file
    /// <paramref name="recordedLocator"/> already names.
    ///
    /// <para>Compared as resolved paths rather than as url text: the locator was minted by a different process, on a
    /// possibly different host, and two spellings of one path — a trailing separator, a percent-encoded segment —
    /// are the same file and must not read as a key-mapping failure.</para>
    /// </summary>
    public static bool NamesTheSameFile(string rootPath, string objectKey, string? recordedLocator)
    {
        if (string.IsNullOrWhiteSpace(rootPath) || string.IsNullOrWhiteSpace(recordedLocator)) return false;
        if (!Uri.TryCreate(recordedLocator, UriKind.Absolute, out var locator) || !locator.IsFile) return false;

        var predicted = Path.GetFullPath(Path.Combine([rootPath, .. objectKey.Split('/')]));

        return string.Equals(predicted, Path.GetFullPath(locator.LocalPath), StringComparison.Ordinal);
    }
}
