using CodeSpace.Core.Services.Agents.Harnesses.Codex;
using CodeSpace.Core.Services.Agents.Sandbox.Isolation;
using CodeSpace.Messages.Agents;
using Shouldly;

namespace CodeSpace.SandboxTests;

/// <summary>
/// Every repository's own <c>AGENTS.md</c> in a multi-repo Codex run. The pinned 0.142.2 reads a project doc from its
/// cwd and the directories above it, never from one below, and a multi-repo run's cwd is the workspace root, so before
/// the harness appended them (<c>CodexRepositoryGuides</c>) no repository's doc reached the model. The arm pins both
/// halves against the real binary: what the harness appends to the run's <c>CODEX_HOME/AGENTS.md</c> reaches the
/// model in the instruction block the operating contract opens, and the CLI reads none of those docs on its own, so
/// each reaches it once.
///
/// <para>Same fidelity as the class: the pinned binary, the production argv and runner, the production broker; only the
/// model is scripted. Root lane, Standard: the posture a shipped Codex run has, which the CLI runs as uid 0.</para>
/// </summary>
public sealed partial class RepositoryConfigE2ETests
{
    /// <summary>What every request carries in the instruction block <c>CODEX_HOME/AGENTS.md</c> opens: the operating contract the harness writes first.</summary>
    private const string OperatingContractText = "UNATTENDED agent";

    /// <summary>
    /// Three repositories below a workspace root that is no repository: the first commits an <c>AGENTS.md</c>, the second
    /// an <c>AGENTS.override.md</c> beside one, and the third an <c>AGENTS.md</c> linked to a file outside the workspace,
    /// where a confined run could still read it, so its absence is the containment check's doing. Every repository also
    /// commits hostile Codex config. The first request's instruction block must carry the
    /// operating contract, then the first repository's doc, then the second's override, each once; the second's plain
    /// doc and the outside file must reach no request; the third must be named on the launch; and none of the config
    /// may load.
    /// </summary>
    [Fact]
    public async Task A_multi_repo_codex_run_reads_every_repositorys_agents_md()
    {
        const string harnessKind = CodexHarness.HarnessKind;
        var harness = ReviewerReadsItsDiffE2ETests.HarnessFor(harnessKind);

        if (!ReviewerReadsItsDiffE2ETests.Armed(harnessKind) || OperatingSystem.IsWindows()) return;

        await ReviewerReadsItsDiffE2ETests.RequirePinnedBinaryAsync(harness, harnessKind);

        using var hostile = new ConnectionCounter();
        var workspace = NewWorkspace(repositories: 3);
        var (plain, overridden, linked) = (workspace.Repositories[0], workspace.Repositories[1], workspace.Repositories[2]);
        var markers = workspace.Repositories.Select(repo => new Markers(repo, "mcp-server", "session", "prompt", "stop")).ToList();
        var outside = Path.Combine(NewOutsideDirectory(), "AGENTS.md");

        foreach (var (repo, marked) in workspace.Repositories.Zip(markers)) PlantCodexConfig(repo, hostile, marked);

        File.WriteAllText(outside, $"{Mention(linked, "OUTSIDE-DOC")}\n");
        plain.Commit("AGENTS.md", $"{Mention(plain, "PROJECT-DOC")}\n");
        overridden.Commit("AGENTS.md", $"{Mention(overridden, "PLAIN-DOC")}\n");
        overridden.Commit("AGENTS.override.md", $"{Mention(overridden, "OVERRIDE-DOC")}\n");
        linked.CommitLink("AGENTS.md", outside);

        GitRepositoryHolding(workspace.Directory).ShouldBeNull("fixture check: the workspace root must sit in no git repository, or the CLI's own project-doc walk could reach the repositories' docs and this says nothing");

        var (spec, run, upstream) = await RunAsync(harness, workspace, AgentAutonomyLevel.Standard, task => task);

        spec.WorkingDirectory.ShouldBe(workspace.Directory, "fixture check: the run must start at the workspace root, where a multi-repo run's cwd is");
        BrokerViolations(run, upstream, hostile, workspace).Concat(markers.SelectMany(marked => marked.Ran())).ShouldBeEmpty(Diagnosis(harnessKind, spec, run, upstream));

        var first = upstream.Requests.FirstOrDefault(r => r.Path.EndsWith("/responses", StringComparison.Ordinal))?.Body ?? "";
        var block = InstructionBlock(first);
        var order = new[] { OperatingContractText, SurfaceText(plain, "PROJECT-DOC"), SurfaceText(overridden, "OVERRIDE-DOC") }.Select(text => block.IndexOf(text, StringComparison.Ordinal)).ToList();

        order.ShouldAllBe(at => at >= 0, $"the first request's instruction block must carry the operating contract, the first repository's AGENTS.md and the second's AGENTS.override.md (positions {string.Join(", ", order)}). {Diagnosis(harnessKind, spec, run, upstream)}");
        order.ShouldBeInOrder(SortDirection.Ascending, $"each repository's doc must follow the operating contract, in the order the executor names the repositories. {Diagnosis(harnessKind, spec, run, upstream)}");
        Occurrences(first, SurfaceText(plain, "PROJECT-DOC")).ShouldBe(1, $"a repository's doc must reach the model once: the CLI reads none below its cwd on its own. {Diagnosis(harnessKind, spec, run, upstream)}");
        upstream.Requests.ShouldNotContain(r => r.Body.Contains(SurfaceText(overridden, "PLAIN-DOC"), StringComparison.Ordinal), $"an override beside it is the doc Codex picks, so the plain AGENTS.md must reach no request. {Diagnosis(harnessKind, spec, run, upstream)}");
        upstream.Requests.ShouldNotContain(r => r.Body.Contains(SurfaceText(linked, "OUTSIDE-DOC"), StringComparison.Ordinal), $"a doc linked outside the workspace must reach no request. {Diagnosis(harnessKind, spec, run, upstream)}");
        spec.LaunchNotices.ShouldBe(new[] { $"Left the AGENTS.md of '{Path.GetFileName(linked.Directory)}' out of this run: it resolves outside the workspace." });

        output.WriteLine($"{RanMarker} agents-md codex-cli multi-repo Standard uid={NonRootWorker.EffectiveUid()} confined={BubblewrapSandbox.Available is not null} hostileConnections={hostile.Connections}");
    }

    /// <summary>The text of the request's input item that carries the operating contract — the instruction block the CLI builds from <c>CODEX_HOME/AGENTS.md</c> and any project doc — or empty when none does.</summary>
    private static string InstructionBlock(string body) =>
        TryParse(body) is { } request ? Items(request, "input").SelectMany(item => Items(item, "content")).Select(part => Text(part, "text")).FirstOrDefault(text => text.Contains(OperatingContractText, StringComparison.Ordinal)) ?? "" : "";

    private static int Occurrences(string text, string value)
    {
        var count = 0;

        for (var at = text.IndexOf(value, StringComparison.Ordinal); at >= 0; at = text.IndexOf(value, at + value.Length, StringComparison.Ordinal)) count++;

        return count;
    }
}
