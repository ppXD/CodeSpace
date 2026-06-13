namespace CodeSpace.Core.Services.Agents.Tools;

/// <summary>
/// The catalog of model-callable agent tools — the discovery surface both the MCP server (which lists/serves
/// them to the CLI harnesses) and the future native loop consume. Today it projects the tool-eligible workflow
/// nodes onto <see cref="IAgentTool"/>; MCP servers and other tool sources fold onto the same surface later.
/// </summary>
public interface IAgentToolRegistry
{
    /// <summary>Every available tool, sorted by <see cref="IAgentTool.Kind"/>.</summary>
    IReadOnlyList<IAgentTool> All { get; }

    /// <summary>Resolve a tool by its kind (case-sensitive); null when none is registered.</summary>
    IAgentTool? Resolve(string kind);
}
