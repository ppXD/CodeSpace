using CodeSpace.Messages.Agents;

namespace CodeSpace.Core.Services.Agents;

/// <summary>
/// Discovers + imports an agent pack from a bound repository source — the preview→select→commit flow.
/// Composes the artifact parser, the source-fetch layer (<c>IRepositorySourceService</c>), and the persona
/// import writer; harness/format-agnostic above the parser. Source-agnostic: GitHub and GitLab both work
/// through the same source service. v1 reads from an ALREADY-BOUND repository (credentialed) — no anonymous
/// URL.
/// </summary>
public interface IAgentPackImportService
{
    /// <summary>
    /// Fetch + parse the pack's agents into a dry-run preview (NO persistence): each discovered agent's full
    /// structure plus its derived handle, slug-conflict flag, and importability. Idempotent + side-effect-free.
    /// </summary>
    Task<AgentPackPreview> PreviewAsync(Guid repositoryId, string? reference, string? rootPath, Guid teamId, CancellationToken cancellationToken);

    /// <summary>
    /// Re-fetch + re-parse the selected paths (server-authoritative — never trusts a client-sent body) and
    /// persist each as an imported persona, skipping handle collisions. Returns a per-path outcome.
    /// </summary>
    Task<IReadOnlyList<AgentImportResult>> ImportAsync(Guid repositoryId, string? reference, string? rootPath, IReadOnlyList<string> selectedSourcePaths, Guid teamId, Guid actorUserId, CancellationToken cancellationToken);
}
