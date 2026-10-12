using System.Text;
using System.Text.Json;

namespace CodeSpace.Core.Services.Workflows.Artifacts.Providers.Local;

/// <summary>
/// The physical identity of a filesystem namespace: the one directory a configured <c>rootPath</c> actually reaches,
/// spelled one way.
///
/// <para>Every local driver opens its root with <see cref="Path.GetFullPath(string)"/> and lets the operating system
/// follow whatever links lie along it, so <c>R</c>, <c>R/</c>, <c>R/.</c>, <c>R//</c>, a relative spelling of
/// <c>R</c>, a symbolic link to <c>R</c> and — on a case-insensitive filesystem — <c>r</c> are all one directory.
/// Object keys carry no team segment, so two profiles naming that directory share every object they both store; a
/// comparison that saw two spellings as two namespaces would let one team's purge delete the other team's bytes.</para>
///
/// <para>Where the comparison is unsure it errs towards SAME: the folding below may merge directories a case-sensitive
/// volume keeps apart, and that only ever keeps bytes. Splitting one directory into two is the failure that loses
/// them.</para>
///
/// <para>Links are resolved in the order the operating system resolves them — one component at a time, a link's target
/// spliced in where the link stood, and <c>..</c> applied to the directory reached so far rather than to the text — so
/// <c>L -&gt; a/..</c> beside <c>a -&gt; R/objects</c> is <c>R</c>, as it is to <c>open(2)</c>, not the directory
/// holding <c>L</c>. A root is also not the only place a link can stand: <see cref="ObjectPath"/> resolves the whole
/// path a driver opens for one key.</para>
/// </summary>
internal static class LocalRootPath
{
    /// <summary>The configuration property every local provider names its root with.</summary>
    public const string Property = "rootPath";

    /// <summary>The same bound <c>realpath(3)</c> uses; a longer chain is a loop or an attack, and neither has an identity.</summary>
    private const int MaxSymbolicLinkHops = 40;

    /// <summary>The provider's namespace projection: <c>{"rootPath": &lt;canonical&gt;}</c>, so a canonical configuration projects to itself.</summary>
    public static JsonElement NamespaceConfiguration(JsonElement nonSecretConfiguration)
    {
        var canonical = Canonicalize(Read(nonSecretConfiguration));

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(new Dictionary<string, string> { [Property] = canonical }));
        return document.RootElement.Clone();
    }

    /// <summary>Absolute, lexically normalised, links resolved where they exist, then folded as this platform's filesystem compares names.</summary>
    public static string Canonicalize(string rootPath) => FoldForPlatform(Physical(rootPath));

    /// <summary>
    /// The file a local driver removes when it deletes <paramref name="objectKey"/>: every directory from the root down
    /// to it resolved as the operating system resolves them, the file's own name as written.
    ///
    /// <para>Every directory, because a link beneath the root redirects a key exactly as a link in the root does, so a
    /// collector that owns its root outright can still reach a victim's file through <c>objects</c> or any directory
    /// below it. Not the name itself, because a delete unlinks the name it is given: a key that is a link removes the
    /// link and leaves its target alone.</para>
    /// </summary>
    public static string ObjectPath(JsonElement nonSecretConfiguration, string objectsDirectory, string objectKey)
    {
        var segments = KeySegments(objectKey);
        var directory = Physical(Path.Combine([Read(nonSecretConfiguration), objectsDirectory, .. segments[..^1]]));

        return FoldForPlatform(Path.Combine(directory, segments[^1]));
    }

    private static string Physical(string path) => ResolveSymbolicLinks(Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)));

    /// <summary>The key exactly as a local driver splits it, refused exactly where a driver refuses it: a key no driver opens names no object.</summary>
    private static string[] KeySegments(string objectKey)
    {
        if (string.IsNullOrWhiteSpace(objectKey) || objectKey.Contains('\0') || Path.IsPathRooted(objectKey))
            throw new ArgumentException("An object key must be a non-empty relative key.", nameof(objectKey));

        var segments = objectKey.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (segments.Length == 0 || segments.Any(segment => segment is "." or ".."))
            throw new ArgumentException($"Object key '{objectKey}' cannot contain traversal segments.", nameof(objectKey));

        return segments;
    }

    private static string Read(JsonElement configuration)
    {
        if (configuration.ValueKind == JsonValueKind.Object && configuration.TryGetProperty(Property, out var root)
            && root.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(root.GetString()))
            return root.GetString()!;

        throw new ArgumentException($"A local storage namespace requires a non-empty string '{Property}'.", nameof(configuration));
    }

    /// <summary>
    /// <paramref name="fullPath"/> with every existing symbolic link along it replaced by what it points at, the way
    /// <c>realpath(3)</c> does it: the path is walked one component at a time from the root, a link's target is spliced
    /// in where the link stood and walked in its turn, and <c>..</c> steps up from the directory reached so far — which
    /// holds no links by then, so stepping up from it is exactly what the operating system does.
    ///
    /// <para>Components that do not exist yet stay as written: there is nothing there to follow, and the directory a
    /// later write creates is exactly that spelling beneath the part already resolved.</para>
    /// </summary>
    private static string ResolveSymbolicLinks(string fullPath)
    {
        var resolved = Path.GetPathRoot(fullPath)!;
        var pending = new Stack<string>();
        var hops = 0;

        PushComponents(pending, fullPath);

        while (pending.TryPop(out var component))
        {
            var target = component is "." or ".." ? null : LinkTarget(Path.Combine(resolved, component));

            if (target == null)
            {
                resolved = Step(resolved, component);
                continue;
            }

            if (++hops > MaxSymbolicLinkHops)
                throw new ArgumentException($"'{Property}' '{fullPath}' passes through more than {MaxSymbolicLinkHops} symbolic links, so the directory it names cannot be established.");

            if (Path.IsPathRooted(target)) resolved = Path.GetPathRoot(target)!;

            PushComponents(pending, target);
        }

        return resolved;
    }

    /// <summary>One component down from a directory with no links left in it — or, for <c>..</c>, one up.</summary>
    private static string Step(string resolved, string component) => component switch
    {
        "." => resolved,
        ".." => Path.GetDirectoryName(resolved) ?? resolved,
        _ => Path.Combine(resolved, component),
    };

    /// <summary>Queues the components of <paramref name="path"/> below its root so that its first component is walked next.</summary>
    private static void PushComponents(Stack<string> pending, string path)
    {
        var components = path[Path.GetPathRoot(path)!.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);

        for (var index = components.Length - 1; index >= 0; index--) pending.Push(components[index]);
    }

    /// <summary>
    /// A component this process cannot inspect is treated as no link. Resolving it is not possible from here, and
    /// whatever this process cannot reach it cannot write to or delete from either.
    /// </summary>
    private static string? LinkTarget(string path)
    {
        try { return new FileInfo(path).LinkTarget; }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    /// <summary>
    /// Windows and macOS filesystems compare names case-insensitively by default, and macOS also ignores Unicode
    /// normalisation, so on those platforms both are folded. Linux compares bytes and keeps the path as resolved.
    /// </summary>
    private static string FoldForPlatform(string path) =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? path.Normalize(NormalizationForm.FormC).ToLowerInvariant() : path;
}
