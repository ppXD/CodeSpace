using CodeSpace.Core.Services.Agents.Harnesses.Claude;
using CodeSpace.Core.Services.Agents.Harnesses.Codex;
using CodeSpace.Core.Services.Agents.Sandbox.Isolation;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;
using Shouldly;

namespace CodeSpace.SandboxTests;

/// <summary>
/// The output reviewer's clone is the produced branch, so its <c>CLAUDE.md</c> and <c>AGENTS.md</c> may be the reviewed
/// agent's own words. A review task carries <see cref="AgentTask.ExcludeRepositoryInstructions"/>, and each harness turns
/// that into its CLI's own switch: no <c>--add-dir</c> memory for Claude under <c>--setting-sources user</c>, and
/// <see cref="CodexHarness.NoProjectDocOverride"/> for Codex. Whether the CLI honours that switch is a fact about the
/// binary, which a fake CLI cannot answer, so each arm runs the pinned binary twice over one repository that commits both
/// files: once as a review task — neither file may reach any request — and once as an ordinary task, the positive control
/// that the same binary over the same repository does load the file the exclusion keeps out. Codex's
/// <c>CODEX_HOME/AGENTS.md</c>, which carries the operating contract, must still reach the reviewer: the override turns off
/// the project walk, not the run's own instructions.
///
/// <para>Same fidelity as the class: the pinned binary, the production argv and runner, the production broker; only the
/// model is scripted. Root lane: Claude Confined (the pinned CLI refuses a Standard run's <c>bypassPermissions</c> to uid
/// 0, and a reviewer is Confined anyway), Codex Standard (the posture of a shipped Codex run).</para>
/// </summary>
public sealed partial class RepositoryConfigE2ETests
{
    [Theory]
    [InlineData(ClaudeCodeHarness.HarnessKind)]
    [InlineData(CodexHarness.HarnessKind)]
    public async Task A_review_task_loads_none_of_the_reviewed_branchs_instruction_files(string harnessKind)
    {
        var harness = ReviewerReadsItsDiffE2ETests.HarnessFor(harnessKind);

        if (!ReviewerReadsItsDiffE2ETests.Armed(harnessKind) || OperatingSystem.IsWindows()) return;

        await ReviewerReadsItsDiffE2ETests.RequirePinnedBinaryAsync(harness, harnessKind);

        using var hostile = new ConnectionCounter();
        var workspace = NewWorkspace(repositories: 1);
        var repo = workspace.Repositories[0];
        var tier = harnessKind == ClaudeCodeHarness.HarnessKind ? AgentAutonomyLevel.Confined : AgentAutonomyLevel.Standard;
        var surface = harnessKind == ClaudeCodeHarness.HarnessKind ? "PRODUCER-MEMORY" : "PRODUCER-DOC";

        repo.Commit("CLAUDE.md", $"{Mention(repo, "PRODUCER-MEMORY")}\n");
        repo.Commit("AGENTS.md", $"{Mention(repo, "PRODUCER-DOC")}\n");

        var (controlSpec, controlRun, control) = await RunAsync(harness, workspace, tier, task => task);
        var (spec, run, upstream) = await RunAsync(harness, workspace, tier, task => task with { ExcludeRepositoryInstructions = true });

        BrokerViolations(controlRun, control, hostile, workspace).ShouldBeEmpty($"fixture check: the control must run to its answer. {Diagnosis(harnessKind, controlSpec, controlRun, control)}");
        control.Requests.ShouldContain(r => r.Body.Contains(SurfaceText(repo, surface), StringComparison.Ordinal), $"positive control: an ordinary task over this repository loads its {(surface == "PRODUCER-MEMORY" ? "CLAUDE.md" : "AGENTS.md")}, or the review task leaving it out proves nothing. {Diagnosis(harnessKind, controlSpec, controlRun, control)}");

        BrokerViolations(run, upstream, hostile, workspace).ShouldBeEmpty(Diagnosis(harnessKind, spec, run, upstream));
        upstream.Requests.ShouldNotContain(r => r.Body.Contains(SurfaceText(repo, "PRODUCER-MEMORY"), StringComparison.Ordinal), $"the reviewed branch's CLAUDE.md must reach no request of its reviewer. {Diagnosis(harnessKind, spec, run, upstream)}");
        upstream.Requests.ShouldNotContain(r => r.Body.Contains(SurfaceText(repo, "PRODUCER-DOC"), StringComparison.Ordinal), $"the reviewed branch's AGENTS.md must reach no request of its reviewer. {Diagnosis(harnessKind, spec, run, upstream)}");

        if (harnessKind == CodexHarness.HarnessKind)
        {
            spec.Args.ShouldContain(CodexHarness.NoProjectDocOverride, "fixture check: the review task's argv carries the override this arm measures");
            InstructionBlock(upstream.Requests.First(r => r.Path.EndsWith("/responses", StringComparison.Ordinal)).Body).ShouldContain(OperatingContractText, Case.Sensitive, $"the override turns off the project walk, never CODEX_HOME/AGENTS.md — the reviewer still gets its operating contract. {Diagnosis(harnessKind, spec, run, upstream)}");
        }

        output.WriteLine($"{RanMarker} reviewer-instructions {harnessKind} single-repo {tier} uid={NonRootWorker.EffectiveUid()} confined={BubblewrapSandbox.Available is not null}");
    }
}
