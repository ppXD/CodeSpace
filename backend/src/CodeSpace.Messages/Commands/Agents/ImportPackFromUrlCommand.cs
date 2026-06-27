using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Commands.Agents;

/// <summary>
/// Commit the operator's selection from a URL pack preview: re-clone the URL (allowlist-guarded), re-walk it,
/// and persist exactly the chosen <see cref="SourcePaths"/> under a resolved <c>Pack</c> — agents and skills
/// alike. The re-clone keeps the verbatim-frontmatter guarantee server-authoritative (the preview's content is
/// never trusted at commit). Idempotent: re-running resolves to the same pack and upserts on (pack, source-path)
/// rather than duplicating. Returns a per-path outcome.
/// </summary>
public sealed record ImportPackFromUrlCommand : ICommand<PackImportResult>, IRequireTeamMembership
{
    public required string Url { get; init; }
    public string? Reference { get; init; }

    /// <summary>The SourcePaths the operator selected in the preview (the stable per-file identity), agents or skills.</summary>
    public IReadOnlyList<string> SourcePaths { get; init; } = Array.Empty<string>();
}
