using System.Security.Cryptography;
using CodeSpace.Messages.Agents.Benchmark;

namespace CodeSpace.Core.Services.Agents;

/// <summary>
/// Observes the BYTES of the harness executables this host would actually run — the fact a pinned
/// <see cref="IAgentHarness.Version"/> cannot supply, because the adapter asserts that version about itself and an
/// operator can repoint the binary through the harness's own <c>CommandEnvVar</c> without changing it.
///
/// <para>Pure and local: it resolves a command the way the OS would (an absolute or directory-qualified command as
/// given, a bare name across <c>PATH</c>), reads the file, and hashes it. No process is started — probing a CLI by
/// running <c>--version</c> would trust the binary to describe itself, which is exactly the claim under test — and
/// nothing reaches the network. Symlinks are followed, so the Docker worker's <c>/usr/local/bin/claude</c> shim
/// hashes the package bytes it points at.</para>
///
/// <para>Lives beside <see cref="AgentHarnessRegistry"/> because it is a fact about the registry's members, and its
/// pure entry point takes plain strings so it is unit-testable with no harness, no DI and no real CLI.</para>
/// </summary>
public static class HarnessBinaryObserver
{
    /// <summary>Every registered harness that drives an external binary, ordered by kind so the observation is stable across DI enumeration order.</summary>
    public static IReadOnlyList<HarnessBinaryIdentity> Observe(IEnumerable<IAgentHarness> harnesses) =>
        harnesses.OrderBy(harness => harness.Kind, StringComparer.Ordinal).Select(Identity).OfType<HarnessBinaryIdentity>().ToList();

    /// <summary>Observe one command's bytes against this process's PATH. The pure seam: production passes what the harness resolved.</summary>
    public static HarnessBinaryIdentity Observe(string kind, string version, string command) => Observe(kind, version, command, Environment.GetEnvironmentVariable("PATH"));

    /// <summary>The file the OS would execute for <paramref name="command"/> on this process's PATH, or null when nothing resolves.</summary>
    public static string? ResolveOnPath(string command) => ResolveOnPath(command, Environment.GetEnvironmentVariable("PATH"));

    // Internal (not private): the search path is a PARAMETER so the resolution is unit-pinned directly, without a
    // test clobbering the process's own PATH — a concurrently running test may be shelling out through it.
    internal static HarnessBinaryIdentity Observe(string kind, string version, string command, string? searchPath)
    {
        if (ResolveOnPath(command, searchPath) is not { } resolved)
            return new HarnessBinaryIdentity { Kind = kind, Version = version, UnobservedReason = HarnessBinaryIdentity.ReasonNotFound };

        return Sha256Of(resolved) is { } digest
            ? new HarnessBinaryIdentity { Kind = kind, Version = version, BinarySha256 = digest }
            : new HarnessBinaryIdentity { Kind = kind, Version = version, UnobservedReason = HarnessBinaryIdentity.ReasonUnreadable };
    }

    /// <summary>A rooted or directory-qualified command is never searched on <paramref name="searchPath"/> — that is the shell's rule too.</summary>
    internal static string? ResolveOnPath(string command, string? searchPath)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;

        if (Path.IsPathRooted(command) || command.Contains(Path.DirectorySeparatorChar) || command.Contains(Path.AltDirectorySeparatorChar))
            return File.Exists(command) ? command : null;

        var directories = (searchPath ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);

        return directories.Select(directory => SafeCombine(directory, command)).FirstOrDefault(candidate => candidate is not null && File.Exists(candidate));
    }

    private static HarnessBinaryIdentity? Identity(IAgentHarness harness) =>
        harness is IAgentHarnessBinary binary ? Observe(harness.Kind, harness.Version, binary.ResolveCommand()) : null;

    // A malformed PATH entry (invalid characters on this platform) must not take the whole observation down — the
    // remaining entries can still hold the binary, exactly as the OS would find it.
    private static string? SafeCombine(string directory, string command)
    {
        try
        {
            return Path.Combine(directory, command);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static string? Sha256Of(string path)
    {
        try
        {
            using var file = File.OpenRead(path);

            return Convert.ToHexStringLower(SHA256.HashData(file));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
