using CodeSpace.Core.Services.Agents.Harnesses.Claude;
using CodeSpace.Core.Services.Agents.Sandbox.Isolation;
using CodeSpace.Core.Services.Agents.Sandbox.Runners;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;
using Shouldly;

namespace CodeSpace.SandboxTests;

/// <summary>
/// Repository memory that links outside the workspace. The pinned CLI opens an added directory's <c>CLAUDE.md</c> and
/// <c>.claude/CLAUDE.md</c> by path and follows a symlink at either wherever it leads, and reads a <c>.claude</c>
/// directory that is itself a link wherever it leads, so a repository that commits one of the three as a link out of its
/// workspace hands what it reaches to the model — on Linux, <c>/proc/self/environ</c> would be the CLI's own
/// environment, the run's broker token included. The harness leaves such a repository's directory out of
/// <c>--add-dir</c> (<c>ClaudeWorkspaceMemory</c>) and says so on the launch. Its other escapes — a rules entry linked
/// out, an import that resolves outside — the pinned CLI does not follow (observed against 2.1.263), so they have no
/// positive control here; the unit tests pin that the guard leaves them out all the same.
///
/// <para>Same fidelity as the class: the pinned binary, the production argv and runner, the production broker; only
/// the model is scripted. The arm runs twice against the same workspace: as production builds it, and — the positive
/// control — with every left-out directory put back into <c>--add-dir</c>, the argv the harness built before the guard.
/// The control must hand each repository's outside file to the model, or the guarded run keeping it away proves
/// nothing.</para>
/// </summary>
public sealed partial class RepositoryConfigE2ETests
{
    /// <summary>Each entry of a repository's memory the pinned CLI follows out of the workspace when it is a link, and why the guard names it on the launch.</summary>
    private static readonly (string Escape, string Notice)[] FollowedEscapes =
    [
        ("CLAUDE.md", "CLAUDE.md resolves outside the workspace"),
        (".claude/CLAUDE.md", ".claude/CLAUDE.md resolves outside the workspace"),
        (".claude", ".claude resolves outside the workspace"),
    ];

    /// <summary>
    /// A multi-repo workspace with one repository for each shape the pinned CLI follows out of it (<see cref="FollowedEscapes"/>),
    /// each that shape as its one escape and each with memory of its own that stays inside, and a last repository whose
    /// <c>CLAUDE.md</c> links to the <c>AGENTS.md</c> beside it. Nothing from outside may reach any request; none of the
    /// linking repositories' memory may load, not even what stays inside them; the last one's must still be in the first
    /// request; and each linking repository must be missing from <c>--add-dir</c> and named on the launch. Root lane,
    /// Confined: the pinned CLI refuses a Standard run's <c>bypassPermissions</c> to uid 0, and plan mode loads memory
    /// exactly as any other mode does.
    /// </summary>
    [Fact]
    public async Task A_claude_run_leaves_out_repository_memory_that_links_outside_the_workspace()
    {
        const string harnessKind = ClaudeCodeHarness.HarnessKind;
        const AgentAutonomyLevel tier = AgentAutonomyLevel.Confined;
        var harness = ReviewerReadsItsDiffE2ETests.HarnessFor(harnessKind);

        if (!ReviewerReadsItsDiffE2ETests.Armed(harnessKind) || OperatingSystem.IsWindows()) return;

        await ReviewerReadsItsDiffE2ETests.RequirePinnedBinaryAsync(harness, harnessKind);

        using var hostile = new ConnectionCounter();
        var workspace = NewWorkspace(repositories: FollowedEscapes.Length + 1);
        var linking = workspace.Repositories.Take(FollowedEscapes.Length).ToList();
        var kept = workspace.Repositories[^1];

        foreach (var (repo, (escape, _)) in linking.Zip(FollowedEscapes)) PlantMemoryLinkedOutside(repo, escape);

        kept.Commit("AGENTS.md", $"{Mention(kept, "MEMORY")}\n");
        kept.CommitLink("CLAUDE.md", "AGENTS.md");

        var (spec, run, upstream) = await RunAsync(harness, workspace, tier, task => task);
        var (unguardedSpec, unguardedRun, unguarded) = await RunAsync(harness, workspace, tier, task => task, reshape: production => WithAddedDirectories(production, linking.Select(repo => repo.Directory)));

        AddedDirectories(unguardedSpec).ShouldBe(new[] { workspace.Directory }.Concat(workspace.Repositories.Select(repo => repo.Directory)), ignoreOrder: true, "fixture check: the control must add every directory the guard left out");
        BrokerViolations(unguardedRun, unguarded, hostile, workspace).ShouldBeEmpty($"fixture check: the control must run to its answer. {Diagnosis(harnessKind, unguardedSpec, unguardedRun, unguarded)}");
        Missing(unguarded, linking, "INSIDE-MEMORY").ShouldBeEmpty($"fixture check: with each linking repository added, its memory that stays inside loads. {Diagnosis(harnessKind, unguardedSpec, unguardedRun, unguarded)}");
        Missing(unguarded, linking, "OUTSIDE-MEMORY").ShouldBeEmpty($"positive control: with each linking repository added, the pinned CLI follows its link out of the workspace and hands the outside file to the model — a repository missing here is a shape the guarded run keeping away proves nothing about. {Diagnosis(harnessKind, unguardedSpec, unguardedRun, unguarded)}");

        BrokerViolations(run, upstream, hostile, workspace).ShouldBeEmpty(Diagnosis(harnessKind, spec, run, upstream));
        Reached(upstream, linking, "OUTSIDE-MEMORY").ShouldBeEmpty($"nothing from outside the workspace may reach the model. {Diagnosis(harnessKind, spec, run, upstream)}");
        Reached(upstream, linking, "INSIDE-MEMORY").ShouldBeEmpty($"a linking repository is left out whole: none of its memory loads, not even what stays inside it. {Diagnosis(harnessKind, spec, run, upstream)}");
        AddedDirectories(spec).ShouldBe(new[] { workspace.Directory, kept.Directory }, "only the root and the repository whose memory stays inside are added");
        spec.LaunchNotices.ShouldBe(linking.Zip(FollowedEscapes, (repo, shape) => $"Left the memory in '{Path.GetFileName(repo.Directory)}' out of this run: {shape.Notice}."));
        (upstream.Requests.FirstOrDefault(OffersTools)?.Body ?? "").ShouldContain(SurfaceText(kept, "MEMORY"), Case.Sensitive, $"the last repository's CLAUDE.md links inside it, so its memory still loads before the first request. {Diagnosis(harnessKind, spec, run, upstream)}");

        output.WriteLine($"{RanMarker} memory-link-outside {harnessKind} multi-repo {tier} uid={NonRootWorker.EffectiveUid()} confined={BubblewrapSandbox.Available is not null} shapes={string.Join(',', FollowedEscapes.Select(shape => shape.Escape))}");
    }

    /// <summary>
    /// <paramref name="escape"/> committed as a link to an outside file that carries <c>OUTSIDE-MEMORY</c> — for
    /// <c>.claude</c>, to an outside directory holding that <c>CLAUDE.md</c> — beside memory of its own that stays inside
    /// and carries <c>INSIDE-MEMORY</c>: <c>.claude/CLAUDE.md</c> when the link is <c>CLAUDE.md</c>, <c>CLAUDE.md</c>
    /// otherwise.
    /// </summary>
    private void PlantMemoryLinkedOutside(Repository repo, string escape)
    {
        var outside = NewOutsideDirectory();
        var memory = Path.Combine(outside, "CLAUDE.md");

        File.WriteAllText(memory, $"{Mention(repo, "OUTSIDE-MEMORY")}\n");

        repo.CommitLink(escape, escape == ".claude" ? outside : memory);
        repo.Commit(escape == "CLAUDE.md" ? ".claude/CLAUDE.md" : "CLAUDE.md", $"{Mention(repo, "INSIDE-MEMORY")}\n");
    }

    /// <summary>Every repository of <paramref name="repositories"/> whose <paramref name="surface"/> text reached a request.</summary>
    private static IEnumerable<string> Reached(ScriptedModelUpstream upstream, IEnumerable<Repository> repositories, string surface) =>
        repositories.Where(repo => upstream.Requests.Any(r => r.Body.Contains(SurfaceText(repo, surface), StringComparison.Ordinal))).Select(repo => repo.Directory);

    /// <summary>Every repository of <paramref name="repositories"/> whose <paramref name="surface"/> text reached no request.</summary>
    private static IEnumerable<string> Missing(ScriptedModelUpstream upstream, IReadOnlyList<Repository> repositories, string surface) =>
        repositories.Select(repo => repo.Directory).Except(Reached(upstream, repositories, surface));

    /// <summary>
    /// A directory outside the workspace that the run can still read. Where the host confines, only the system roots are
    /// bound beside the workspace and the config home, and <c>/tmp</c> is the sandbox's own, so it goes under one of those
    /// roots (this lane runs as root); anywhere else the temp path serves.
    /// </summary>
    private string NewOutsideDirectory()
    {
        const string boundRoot = "/etc";

        if (BubblewrapSandbox.Available is not null) BubblewrapSandbox.ReadOnlyRootDirs.ShouldContain(boundRoot, "fixture check: the outside file must sit where the sandbox can see it, or the control finds nothing to follow");

        var directory = Path.Combine(BubblewrapSandbox.Available is null ? Path.GetTempPath() : boundRoot, $"cs-repo-config-outside-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        _directories.Add(directory);
        return directory;
    }

    /// <summary>The directories one variadic <c>--add-dir</c> carries, in order; none when there is no <c>--add-dir</c>.</summary>
    private static IReadOnlyList<string> AddedDirectories(SandboxSpec spec)
    {
        var at = spec.Args.ToList().IndexOf("--add-dir");

        return at < 0 ? [] : spec.Args.Skip(at + 1).TakeWhile(arg => !arg.StartsWith("--", StringComparison.Ordinal)).ToList();
    }

    /// <summary>The spec with each of <paramref name="directories"/> it does not already add put back at the head of its <c>--add-dir</c> list.</summary>
    private static SandboxSpec WithAddedDirectories(SandboxSpec spec, IEnumerable<string> directories)
    {
        var args = spec.Args.ToList();
        args.InsertRange(args.IndexOf("--add-dir") + 1, directories.Except(AddedDirectories(spec), StringComparer.Ordinal));
        return spec with { Args = args };
    }
}
