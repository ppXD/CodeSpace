using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Services.Agents.Parsers.ClaudeCode;
using CodeSpace.Core.Services.Agents.Skills;
using CodeSpace.Core.Services.Agents.Skills.Parsers.ClaudeCode;
using CodeSpace.Messages.Agents;

namespace CodeSpace.Core.Services.Agents;

/// <summary>
/// Default <see cref="IPackSourceWalker"/> — a recursive Markdown walk over a local pack tree. Discovery rules,
/// in order: (1) every <c>SKILL.md</c> is a skill, and its directory (UNLESS it is the repo root) is recorded as
/// a skill root; (2) every OTHER <c>.md</c> that is NOT inside a skill directory and whose frontmatter yields a
/// <c>name</c> is an agent (the <c>name</c> requirement filters READMEs/docs/skill-resource markdown that carry
/// no agent frontmatter; the skill-directory exclusion keeps a skill's own <c>references/*.md</c> from being
/// mis-read as agents). A root-level <c>SKILL.md</c> registers as a skill but is NOT an excluding root — otherwise
/// it would suppress every agent in the tree. Discovery is deliberately PERMISSIVE — the operator selects from the
/// preview, so a stray candidate is deselected, never blind-imported. Source paths are root-relative +
/// forward-slashed (the stable sync identity); the file list is sorted so discovery order is deterministic.
///
/// <para>RESILIENT: a single unreadable or malformed file never aborts the walk. A <c>SKILL.md</c> always appears
/// — a malformed or unreadable one rides along with <c>Diagnostics</c> populated; an unreadable / nameless / doc
/// <c>.md</c> is simply not classified as an agent (an agent is positively identified by a parseable name).</para>
/// </summary>
public sealed class PackSourceWalker : IPackSourceWalker, ISingletonDependency
{
    private const string SkillFileName = "SKILL.md";

    private readonly IAgentArtifactParserRegistry _agentParsers;
    private readonly ISkillArtifactParserRegistry _skillParsers;

    public PackSourceWalker(IAgentArtifactParserRegistry agentParsers, ISkillArtifactParserRegistry skillParsers)
    {
        _agentParsers = agentParsers;
        _skillParsers = skillParsers;
    }

    public async Task<DiscoveredPack> WalkAsync(string rootDir, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(rootDir))
            throw new DirectoryNotFoundException($"Pack source directory not found: {rootDir}");

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootDir));
        var agentParser = _agentParsers.Resolve(ClaudeCodeAgentParser.ParserKind);
        var skillParser = _skillParsers.Resolve(ClaudeCodeSkillParser.ParserKind);

        var mdFiles = Directory.EnumerateFiles(rootDir, "*.md", SearchOption.AllDirectories)
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();

        var skills = new List<ParsedSkillDefinition>();
        var skillDirs = new List<string>();

        foreach (var file in mdFiles.Where(IsSkillFile))
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Record the skill's directory as an agent-excluding root — UNLESS it IS the repo root. A root-level
            // SKILL.md is a single skill, not a claim over the whole tree; recording root would suppress every agent.
            var dir = Path.GetDirectoryName(Path.GetFullPath(file))!;
            if (!string.Equals(dir, root, StringComparison.Ordinal)) skillDirs.Add(dir);

            skills.Add(await ReadAndParseSkillAsync(file, RelativePath(rootDir, file), skillParser, cancellationToken).ConfigureAwait(false));
        }

        var agents = new List<ParsedAgentDefinition>();

        foreach (var file in mdFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (IsSkillFile(file) || IsUnderAnySkillDir(file, skillDirs)) continue;

            var parsed = await ReadAndParseAgentAsync(file, RelativePath(rootDir, file), agentParser, cancellationToken).ConfigureAwait(false);

            if (parsed is { } p && !string.IsNullOrWhiteSpace(p.Name)) agents.Add(p);
        }

        return new DiscoveredPack { Agents = agents, Skills = skills };
    }

    /// <summary>Read + parse a SKILL.md; on a read failure (locked / permission / broken symlink / bad encoding) surface a diagnostic-bearing skill so it rides along rather than aborting the walk.</summary>
    private static async Task<ParsedSkillDefinition> ReadAndParseSkillAsync(string file, string sourcePath, ISkillArtifactParser parser, CancellationToken cancellationToken)
    {
        try
        {
            var text = await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false);
            return parser.Parse(text, sourcePath);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new ParsedSkillDefinition { SourcePath = sourcePath, Diagnostics = new[] { $"Could not read the file: {ex.Message}" } };
        }
    }

    /// <summary>Read + parse a candidate agent .md; on a read failure return null so the unreadable file is skipped (not classifiable as an agent) without aborting the walk.</summary>
    private static async Task<ParsedAgentDefinition?> ReadAndParseAgentAsync(string file, string sourcePath, IAgentArtifactParser parser, CancellationToken cancellationToken)
    {
        try
        {
            var text = await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false);
            return parser.Parse(text, sourcePath);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    private static bool IsSkillFile(string path) =>
        string.Equals(Path.GetFileName(path), SkillFileName, StringComparison.OrdinalIgnoreCase);

    /// <summary>True when <paramref name="file"/> lives inside (or directly in) a recorded skill directory — its skill resources, not an agent. The <c>+ DirectorySeparatorChar</c> guard stops a prefix-sibling (skills/tdd-extra vs skill dir skills/tdd) from falsely matching.</summary>
    private static bool IsUnderAnySkillDir(string file, IReadOnlyList<string> skillDirs)
    {
        if (skillDirs.Count == 0) return false;

        var dir = Path.GetDirectoryName(Path.GetFullPath(file));
        if (dir is null) return false;

        foreach (var skillDir in skillDirs)
            if (string.Equals(dir, skillDir, StringComparison.Ordinal)
                || dir.StartsWith(skillDir + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                return true;

        return false;
    }

    private static string RelativePath(string rootDir, string file) =>
        Path.GetRelativePath(rootDir, file).Replace('\\', '/');
}
