namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// Isolates <see cref="LocalGitWorkspaceProviderTests"/>, whose cleanup-leak test COUNTS the dirs under the
/// process-global <c>LocalGitWorkspaceProvider.WorkspacesRoot</c> (<c>&lt;temp&gt;/codespace-agent-workspaces</c>)
/// before vs after a partial-clone failure and asserts the count is unchanged. Other test classes provision real
/// workspaces under that SAME root (e.g. <c>GitIntegrateNodeTests</c>, <c>AgentRunExecutorEgressTests</c>), so when
/// xUnit runs them in parallel a sibling's transient workspace dir lands in the before→after window and flakes the
/// count. <c>DisableParallelization</c> runs this class in its own sequential phase — it never overlaps a parallel
/// workspace-creator, so the global count is stable (it's the COUNTER, so isolating it alone closes the race; no need
/// to enumerate every creator). Mirrors <c>ModelPriceEnvMutationCollection</c> / <c>LocalProcessIdleWatchdogCollection</c>.
/// </summary>
[CollectionDefinition("WorkspaceProvisioning", DisableParallelization = true)]
public sealed class WorkspaceProvisioningCollection;
