using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Commands.Agents;

/// <summary>
/// Commit the operator's selection from a pack preview: re-fetch + re-parse exactly the chosen paths (the
/// re-parse keeps the verbatim-frontmatter guarantee server-authoritative) and persist each as an imported
/// persona, skipping handle collisions. Returns a per-path outcome.
/// </summary>
public sealed record ImportAgentPackCommand : ICommand<IReadOnlyList<AgentImportResult>>, IRequireTeamMembership
{
    public required Guid RepositoryId { get; init; }
    public string? Reference { get; init; }
    public string? RootPath { get; init; }

    /// <summary>The SourcePaths the operator selected in the preview (the stable per-file identity).</summary>
    public IReadOnlyList<string> SelectedSourcePaths { get; init; } = Array.Empty<string>();
}
