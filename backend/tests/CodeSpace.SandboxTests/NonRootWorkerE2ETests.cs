using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Harnesses.Claude;
using CodeSpace.Core.Services.Agents.Harnesses.Codex;
using CodeSpace.Core.Services.Agents.Sandbox.Runners;
using CodeSpace.Messages.Agents;
using Shouldly;
using Xunit.Abstractions;

namespace CodeSpace.SandboxTests;

/// <summary>
/// 🟢 Sandbox isolation E2E (high fidelity, Rule 12), the NON-ROOT lane: the shipped worker's posture — uid 1654, no
/// capabilities, no_new_privs — on a host where bubblewrap confines through unprivileged user namespaces but this
/// process may not build a network namespace of its own. Until the relay, a confining host that could not build one
/// refused every network-off brokered run before it spent; here the REAL runner admits it, and the real chain carries
/// it to its broker through the relay and the lease's socket. Each arm asserts the posture first
/// (<see cref="NonRootWorker.Require"/>), then runs the SAME arm the root lane runs, so both lanes pin one behaviour —
/// but for two arms: this worker cannot filter, so its allowlist run is severed where the root lane's is filtered, and
/// it still reaches its broker; and its repository-config arm runs Claude at Standard, the tier the root lane's uid 0
/// cannot give it.
///
/// <para>Selected by its trait alone (<c>--filter Category=SandboxNonRoot</c>), never by the root lane's
/// <c>Category=Sandbox</c>. Every arm that ran prints its class's marker with <c>non-root</c> and its uid, which the
/// lane requires.</para>
/// </summary>
[Trait("Category", NonRootWorker.Category)]
public sealed class NonRootWorkerE2ETests(ITestOutputHelper output)
{
    /// <summary>Printed by the admission arm when it actually ran.</summary>
    public const string RanMarker = "[non-root-e2e] ran";

    private const string Lane = "non-root";

    [Fact]
    public void A_worker_that_cannot_build_a_namespace_admits_a_brokered_network_off_run()
    {
        if (!NonRootWorker.Require()) return;

        var runId = Guid.NewGuid();
        var spec = new SandboxSpec { Command = "agent", ModelBrokerPort = 43121, ModelBrokerSocketPath = AgentRunExecutor.ModelBrokerSocketPathFor(new AgentPermissions { Network = AgentNetworkAccess.Off }, runId) };

        Should.NotThrow(() => new LocalProcessRunner().EnsureEgressAdmissible(spec), "a network-off brokered run whose lease has its socket and whose helper is installed reaches its broker through the relay, which needs no namespace of the worker's");

        output.WriteLine($"{RanMarker} admission uid={NonRootWorker.EffectiveUid()}");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_network_off_brokered_run_reaches_its_broker_and_nothing_else(bool durable)
    {
        if (!NonRootWorker.Require()) return;

        using var arms = new SealedEgressE2ETests(output);
        await arms.ReachesItsBrokerAndNothingElseAsync(durable, Lane);
    }

    [Fact]
    public async Task A_relayed_run_has_no_ipv6_path_to_the_worker_either()
    {
        if (!NonRootWorker.Require()) return;

        using var arms = new SealedEgressE2ETests(output);
        await arms.HasNoIpv6PathToTheWorkerAsync(Lane);
    }

    [Fact]
    public async Task A_brokered_child_the_worker_cannot_relay_is_refused_and_would_have_reached_nothing()
    {
        if (!NonRootWorker.Require()) return;

        using var arms = new SealedEgressE2ETests(output);
        await arms.IsRefusedAndWouldHaveReachedNothingAsync(Lane);
    }

    [Fact]
    public async Task The_same_live_agent_reaches_the_next_worker_through_its_socket_after_a_restart()
    {
        if (!NonRootWorker.Require()) return;

        using var arms = new SealedEgressE2ETests(output);
        await arms.ReachesTheNextWorkerAfterARestartAsync(Lane);
    }

    [Fact]
    public async Task An_allowlist_run_this_worker_cannot_filter_is_severed_and_still_reaches_its_broker_through_the_relay()
    {
        if (!NonRootWorker.Require()) return;

        await new DurableLaunchEgressE2ETests(output).IsSeveredAndStillReachesItsBrokerAsync(Lane);
    }

    [Fact]
    public async Task A_severed_child_reaches_its_broker_through_the_lease_socket_and_the_same_child_reaches_the_next_worker()
    {
        if (!NonRootWorker.Require()) return;

        await new ModelCredentialBrokerNetnsE2ETests(output).SocketChannelAsync(Lane);
    }

    [Theory]
    [InlineData(ClaudeCodeHarness.HarnessKind)]
    [InlineData(CodexHarness.HarnessKind)]
    public async Task A_network_off_reviewer_reaches_its_model_through_the_relay(string harnessKind)
    {
        if (!NonRootWorker.Require()) return;

        using var arms = new ReviewerReadsItsDiffE2ETests(output);
        await arms.NetworkOffReviewerAsync(harnessKind, Lane);
    }

    [Fact]
    public async Task A_standard_claude_run_ignores_the_settings_its_repository_commits_and_still_reads_its_memory()
    {
        // The posture the worker ships Claude in — Standard, so bypassPermissions, which the pinned CLI refuses to the
        // root lane's uid 0 — against a repository committing hostile settings. Every command they plant would leave a
        // marker in the workspace this run may write.
        if (!NonRootWorker.Require()) return;

        using var arms = new RepositoryConfigE2ETests(output);
        await arms.ClaudeIgnoresRepositorySettingsAsync(AgentAutonomyLevel.Standard, repositories: 1, Lane);
    }

    [Fact]
    public async Task A_standard_claude_goal_naming_secrets_reaches_the_model_verbatim_and_reads_none_of_them()
    {
        // The goal channel in the posture the worker ships Claude in — Standard, so bypassPermissions, which the pinned
        // CLI refuses to the root lane's uid 0. The CLI read a text goal's mentions in bypass exactly as in plan mode.
        if (!NonRootWorker.Require()) return;

        using var arms = new GoalChannelE2ETests(output);
        await arms.MentionsReadNothingAsync(AgentAutonomyLevel.Standard, Lane);
    }

    [Fact]
    public async Task A_continued_standard_claude_session_takes_its_prompt_the_same_way()
    {
        if (!NonRootWorker.Require()) return;

        using var arms = new GoalChannelE2ETests(output);
        await arms.ResumedPromptReadsNothingAsync(AgentAutonomyLevel.Standard, Lane);
    }

    [Theory]
    [InlineData("/fix")]
    [InlineData("/security-review")]
    public async Task A_standard_claude_goal_that_opens_with_a_slash_word_reaches_the_model_as_text(string word)
    {
        if (!NonRootWorker.Require()) return;

        using var arms = new GoalChannelE2ETests(output);
        await arms.SlashWordReachesTheModelAsync(word, AgentAutonomyLevel.Standard, Lane);
    }
}
