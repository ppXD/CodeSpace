using CodeSpace.Core.Services.Workflows.Nodes.Builtin;
using CodeSpace.Core.Services.Workflows.RunSources.Matchers;

namespace CodeSpace.Core.Services.Workflows.Plugins.Builtin;

/// <summary>
/// Git-domain nodes + the PR-event run-source matchers they go with. Loaded when the platform
/// has at least one git-backed provider instance (GitHub / GitLab). Disabling this plugin
/// in a non-git deployment removes the trigger.pr.* matchers and the git.* nodes cleanly
/// — the engine itself keeps working for HTTP-only or LLM-only workflows.
/// </summary>
public sealed class GitProvidersPlugin : IPluginModule
{
    public string Name => "Git providers";

    public IReadOnlyList<Type> Nodes { get; } = new[]
    {
        typeof(TriggerPrOpenedNode),
        typeof(TriggerPrUpdatedNode),
        typeof(GitFetchPrDiffNode),
        typeof(GitFetchPrChecksNode),
        typeof(GitListPullRequestsNode),
        typeof(GitPostPrCommentNode),
        typeof(GitPrReviewNode),
        typeof(GitOpenPullRequestNode),
    };

    public IReadOnlyList<Type> RunSourceMatchers { get; } = new[]
    {
        typeof(PrOpenedMatcher),
        typeof(PrUpdatedMatcher),
    };

    public IReadOnlyList<Type> AuxiliaryServices { get; } = Array.Empty<Type>();
}
