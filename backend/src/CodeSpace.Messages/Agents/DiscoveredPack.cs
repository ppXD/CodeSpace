namespace CodeSpace.Messages.Agents;

/// <summary>
/// The harness-neutral result of recursively discovering a pack on disk (a cloned/checked-out source tree) — the
/// parsed agents AND skills found anywhere under the root, each carrying its repo-root-relative
/// <c>SourcePath</c> (the stable identity the preview selects on and the importer syncs on). Pure data: the
/// walker never touches the DB or the network; the preview/commit layer adds slug-conflict + importable flags
/// (which need a team) and persistence. A single bad file never aborts discovery: a malformed/unreadable
/// <c>SKILL.md</c> still appears in <see cref="Skills"/> with its <c>Diagnostics</c> populated (so the operator
/// sees the error in the preview); a <c>.md</c> that doesn't parse to an agent name is simply absent from
/// <see cref="Agents"/> (a README/doc/unreadable file is not an agent, not a ridden-along error).
/// </summary>
public sealed record DiscoveredPack
{
    public IReadOnlyList<ParsedAgentDefinition> Agents { get; init; } = Array.Empty<ParsedAgentDefinition>();

    public IReadOnlyList<ParsedSkillDefinition> Skills { get; init; } = Array.Empty<ParsedSkillDefinition>();
}
