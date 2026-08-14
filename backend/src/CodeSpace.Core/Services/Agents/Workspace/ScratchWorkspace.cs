using CodeSpace.Messages.Agents;

namespace CodeSpace.Core.Services.Agents.Workspace;

/// <summary>
/// DC-4 slice 2: the REPO-LESS run's world — a scratch working directory the harness runs in, the declared-artifact
/// capture reads from, and the acceptance oracle grades against. Before this a repo-less run had NO workspace at
/// all (the resolver returns null), so an agent-written report died with the process and any acceptance contract
/// failed closed on "no-branch-or-repo". No git anywhere: <see cref="Repositories"/> is empty (the executor's
/// git-shaped capture/push/manifest steps all skip on that), and disposal deletes the directory — the durable
/// residue is exactly the typed artifact-manifest rows the declared capture minted.
/// </summary>
public sealed class ScratchWorkspaceHandle : IWorkspaceHandle
{
    private ScratchWorkspaceHandle(string directory) => Directory = directory;

    public string Directory { get; }

    public IReadOnlyList<WorkspaceRepositoryHandle> Repositories { get; } = Array.Empty<WorkspaceRepositoryHandle>();

    public string PrimaryAlias => "";

    /// <summary>Create the scratch directory for one attempt — run-id keyed so a leaked dir is attributable.</summary>
    public static ScratchWorkspaceHandle Create(Guid agentRunId)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"codespace-scratch-{agentRunId:N}");
        System.IO.Directory.CreateDirectory(directory);
        return new ScratchWorkspaceHandle(directory);
    }

    /// <summary>A scratch has no git — an empty capture, honestly (the executor's enrich step skips a repo-less workspace outright; this exists for the interface's contract).</summary>
    public Task<WorkspaceChanges> CaptureChangesAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new WorkspaceChanges { Patch = "", BaseSha = "", ChangedFiles = Array.Empty<string>(), FileStats = Array.Empty<FileDiffStat>() });

    public Task<WorkspaceChanges> CaptureChangesAsync(string alias, CancellationToken cancellationToken) =>
        throw new WorkspaceException("a scratch workspace has no repositories to capture by alias");

    public ValueTask DisposeAsync()
    {
        try { System.IO.Directory.Delete(Directory, recursive: true); } catch { /* best-effort: a leaked temp dir on the worker's ephemeral disk is harmless */ }
        return ValueTask.CompletedTask;
    }
}
