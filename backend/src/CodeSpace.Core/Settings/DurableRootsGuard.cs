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

        if (string.IsNullOrWhiteSpace(settings.ArtifactStoreDirectory))
            violations.Add("Artifacts:StoreDirectory is not configured — artifact blobs would land under the system temp dir and die with the host while their durable rows keep claiming them.");

        if (string.IsNullOrWhiteSpace(settings.AgentRunSpoolDirectory))
            violations.Add("Agents:RunSpoolDirectory is not configured — agent-run spools would land under the system temp dir, so re-attach after a restart has nothing to observe.");

        return violations;
    }

    /// <summary>Refuse to start a Production host whose durable roots are unconfigured — a legible startup failure beats a silent temp landing discovered at the first host wipe.</summary>
    public static void ThrowIfProductionUnconfigured(RuntimeSettings settings, string environmentName)
    {
        var violations = Violations(settings, environmentName);

        if (violations.Count > 0)
            throw new InvalidOperationException("Refusing to start in Production with unconfigured durable roots:\n - " + string.Join("\n - ", violations));
    }
}
