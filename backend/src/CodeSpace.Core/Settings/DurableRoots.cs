namespace CodeSpace.Core.Settings;

/// <summary>
/// Where the two durable data roots live when nobody said — the artifact blob store and the agent-run spool.
///
/// <para>Both used to fall back to the system temp directory, which is why <see cref="DurableRootsGuard"/> made
/// configuring them a condition of starting in Production: artifacts referenced by durable rows and re-attach
/// spools die with the host or with a temp sweep, and the rows keep claiming bytes that are gone.</para>
///
/// <para>But the container images already answer the question. <c>Dockerfile.api</c> and <c>Dockerfile.worker</c>
/// both do <c>mkdir -p /var/lib/codespace/artifacts /var/lib/codespace/spool</c> and chown them to the app user
/// before dropping privileges. The deployment was being asked to restate a decision the image had already made,
/// and the punishment for not restating it was a host that refused to boot. So the default is those paths.</para>
///
/// <para>Outside a container that directory is not creatable without root, so the fallback is the platform's own
/// per-user data location — durable on a developer's machine in the way that matters, which is surviving a
/// reboot and a temp sweep. What is deliberately NOT a fallback anywhere is the temp directory itself: that is
/// the one location the guard exists to keep this data out of.</para>
/// </summary>
public static class DurableRoots
{
    /// <summary>Matches the <c>mkdir</c> in both Dockerfiles. Changing either without the other puts the data somewhere the image never prepared.</summary>
    public const string ContainerArtifactStore = "/var/lib/codespace/artifacts";

    /// <summary>Matches the <c>mkdir</c> in both Dockerfiles.</summary>
    public const string ContainerAgentRunSpool = "/var/lib/codespace/spool";

    /// <summary>The artifact blob root: what was configured, else the image's own path, else a per-user one.</summary>
    public static string ArtifactStore(string? configured) => Resolve(configured, ContainerArtifactStore, "artifacts");

    /// <summary>The agent-run spool root: what was configured, else the image's own path, else a per-user one.</summary>
    public static string AgentRunSpool(string? configured) => Resolve(configured, ContainerAgentRunSpool, "agent-runs");

    /// <summary>
    /// A configured value always wins, even a bad one — an operator who names a path meant it, and
    /// <see cref="DurableRootsGuard"/> is what tells them if they named a place data cannot survive in.
    /// </summary>
    private static string Resolve(string? configured, string containerPath, string userDataLeaf)
    {
        if (!string.IsNullOrWhiteSpace(configured)) return Path.GetFullPath(configured.Trim());

        // Existence rather than an OS check: the image creates these, so their presence IS the signal that we are
        // running where they were prepared. A Linux host that is not this image falls through to the user path
        // rather than trying to write somewhere it has no rights to.
        if (Directory.Exists(containerPath)) return containerPath;

        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create), "codespace", userDataLeaf);
    }
}
