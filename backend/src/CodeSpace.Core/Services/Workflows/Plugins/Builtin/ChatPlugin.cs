using CodeSpace.Core.Services.Workflows.Nodes.Builtin;

namespace CodeSpace.Core.Services.Workflows.Plugins.Builtin;

/// <summary>
/// Workflow → team-chat integration. Lets a workflow post into a conversation as the CodeSpace bot
/// (announcements, interactive review cards, digests). Its own domain — independent of the
/// git / llm / http / core-flow toolsets — so an operator can disable chat automation without
/// touching other workflows.
/// </summary>
public sealed class ChatPlugin : IPluginModule
{
    public string Name => "Chat";

    public IReadOnlyList<Type> Nodes { get; } = new[]
    {
        typeof(ChatPostMessageNode),
    };

    public IReadOnlyList<Type> RunSourceMatchers { get; } = Array.Empty<Type>();
    public IReadOnlyList<Type> AuxiliaryServices { get; } = Array.Empty<Type>();
}
