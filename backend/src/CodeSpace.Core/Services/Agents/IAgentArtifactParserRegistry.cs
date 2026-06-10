namespace CodeSpace.Core.Services.Agents;

/// <summary>
/// Resolves an <see cref="IAgentArtifactParser"/> by its <see cref="IAgentArtifactParser.Kind"/> — same
/// shape as <c>IAgentHarnessRegistry</c> / <c>ISandboxRunnerRegistry</c>. A new ecosystem parser becomes
/// resolvable just by registering its class; no edit here.
/// </summary>
public interface IAgentArtifactParserRegistry
{
    IReadOnlyList<IAgentArtifactParser> All { get; }

    /// <summary>Resolve the parser for <paramref name="kind"/>. Throws when none is registered for that kind.</summary>
    IAgentArtifactParser Resolve(string kind);
}
