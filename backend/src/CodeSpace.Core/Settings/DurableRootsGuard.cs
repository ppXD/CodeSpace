namespace CodeSpace.Core.Settings;

/// <summary>
/// P2 slice 3 (the production /tmp ban): the two DURABLE data roots — the artifact blob store and the agent-run
/// spool — silently fall back to the system temp directory when unconfigured, which is fine for dev/test and
/// fatal in production: artifacts referenced by durable rows and re-attach spools die with the host (or a temp
/// sweep), and the store's identity claims outlive their bytes. In Production the roots MUST be configured; the
/// host refuses to start otherwise, naming the exact keys.
///
/// <para>Deliberately exempt (ephemeral by design, documented here so the exemption is a decision, not an
/// oversight): the agent WORKSPACE clones and pack-import clones (janitor-swept caches — losing them costs a
/// re-clone), the per-run MCP socket path (AF_UNIX's 108-char cap forces a short path; a socket is not data),
/// the sandbox-internal tmpfs mounts (never host writes), and the operator-invoked benchmark workspaces.</para>
/// </summary>
public static class DurableRootsGuard
{
    /// <summary>The violations for this environment — empty outside Production, and empty when both roots are configured. Pure, so the rule is unit-pinned.</summary>
    public static IReadOnlyList<string> Violations(RuntimeSettings settings, string environmentName)
    {
        if (!string.Equals(environmentName, "Production", StringComparison.OrdinalIgnoreCase))
            return Array.Empty<string>();

        var violations = new List<string>();

        Check("Artifacts:StoreDirectory", DurableRoots.ArtifactStore(settings.ArtifactStoreDirectory), "artifact blobs die with the host while their durable rows keep claiming them");
        Check("Agents:RunSpoolDirectory", DurableRoots.AgentRunSpool(settings.AgentRunSpoolDirectory), "re-attach after a restart has nothing to observe");

        return violations;

        void Check(string key, string resolved, string consequence)
        {
            if (!IsUnderTempDirectory(resolved)) return;

            violations.Add($"{key} resolves to {resolved}, which is under the system temp directory — {consequence}. Point it at a volume.");
        }
    }

    /// <summary>
    /// Compared as full paths with a trailing separator, so <c>/tmp/codespace</c> counts and a sibling like
    /// <c>/tmpfoo</c> does not.
    /// </summary>
    private static bool IsUnderTempDirectory(string resolved)
    {
        var temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));

        return Path.GetFullPath(resolved).StartsWith(temp + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    /// <summary>Refuse to start a Production host whose durable roots are ephemeral — a legible startup failure beats a silent temp landing discovered at the first host wipe.</summary>
    public static void ThrowIfProductionUnconfigured(RuntimeSettings settings, string environmentName)
    {
        var violations = Violations(settings, environmentName);

        if (violations.Count > 0)
            throw new InvalidOperationException("Refusing to start in Production with ephemeral durable roots:\n - " + string.Join("\n - ", violations));
    }
}
