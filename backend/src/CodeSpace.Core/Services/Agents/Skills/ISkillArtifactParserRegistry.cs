namespace CodeSpace.Core.Services.Agents.Skills;

/// <summary>
/// Resolves an <see cref="ISkillArtifactParser"/> by its <see cref="ISkillArtifactParser.Kind"/> — same shape
/// as <c>IAgentArtifactParserRegistry</c> / <c>IAgentHarnessRegistry</c>. A new ecosystem parser becomes
/// resolvable just by registering its class; no edit here.
/// </summary>
public interface ISkillArtifactParserRegistry
{
    IReadOnlyList<ISkillArtifactParser> All { get; }

    /// <summary>Resolve the parser for <paramref name="kind"/>. Throws when none is registered for that kind.</summary>
    ISkillArtifactParser Resolve(string kind);
}
