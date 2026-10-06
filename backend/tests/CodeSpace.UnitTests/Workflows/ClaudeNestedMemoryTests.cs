using System.Diagnostics;
using CodeSpace.Core.Services.Agents.Harnesses.Claude;
using CodeSpace.Messages.Agents;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// Pins how a Claude run adds nested memory back in place (<see cref="ClaudeWorkspaceMemory"/>): every directory below
/// the cwd that holds a <c>CLAUDE.md</c>, a <c>.claude/CLAUDE.md</c> or a rule without <c>paths:</c> rides the one
/// <c>--add-dir</c> after the workspace and its repositories, shallowest first, when all of them fit the in-place budget —
/// and none does past it. Each case lays out a real tree under a GUID temp root reached through a symlink
/// (<see cref="TempTree"/>), as production never resolves a workspace path.
/// </summary>
[Trait("Category", "Unit")]
public sealed class ClaudeNestedMemoryTests : IDisposable
{
    private const string OverBudgetNotice = "Left the memory of every nested directory out of this run: together it spans more than 16 directories or 32768 bytes, more than a run loads before its first request.";

    private const string BudgetNotice = "Left any memory the runner had not yet found or checked out of this run: finding and checking it would take more than one build spends (65536 paths, 1048576 path components, 67108864 bytes).";

    private readonly TempTree _tree = new();
    private readonly string _workspace;

    public ClaudeNestedMemoryTests() { _workspace = _tree.Directory("ws"); }

    public void Dispose() => _tree.Dispose();

    [Theory]
    [InlineData("single-repo")]
    [InlineData("multi-repo at its root")]
    [InlineData("multi-repo at its primary repository")]
    public void Nested_directories_follow_the_roots_shallowest_first_and_by_name_within_a_level(string shape)
    {
        if (OperatingSystem.IsWindows()) return;

        var (cwd, repositories, added) = PlantLayout(shape);

        var plan = ClaudeWorkspaceMemory.For(new AgentTask { Goal = "g", Harness = ClaudeCodeHarness.HarnessKind, WorkspaceDirectory = cwd, WorkspaceRepositoryDirectories = repositories });

        plan.Directories.ShouldBe(added, shape);
        plan.Notices.ShouldBeEmpty(shape);
    }

    [Theory]
    [InlineData("a CLAUDE.md")]
    [InlineData("a .claude/CLAUDE.md")]
    [InlineData("a rule without paths:")]
    [InlineData("a rule in a folder under rules")]
    [InlineData("a rule scoped to ** alone")]
    [InlineData("a CLAUDE.md linked to the AGENTS.md beside it")]
    [InlineData("a .claude directory linked to another inside the workspace")]
    public void Each_kind_of_memory_the_cli_loads_in_place_makes_its_directory_one_to_add(string kind)
    {
        if (OperatingSystem.IsWindows()) return;

        PlantMemory(kind);

        Plan().Directories.ShouldBe(new[] { _workspace, At("pkg") }, kind);
    }

    [Theory]
    [InlineData("a rule scoped by paths: alone")]
    [InlineData("a rules file that is not markdown")]
    [InlineData("a CLAUDE.md link that dangles")]
    [InlineData("a directory named CLAUDE.md")]
    [InlineData("settings and nothing else")]
    [InlineData("an AGENTS.md")]
    [InlineData("a CLAUDE.local.md")]
    public void A_directory_holding_nothing_the_cli_loads_in_place_is_not_added(string kind)
    {
        if (OperatingSystem.IsWindows()) return;

        PlantMemory(kind);

        var plan = Plan();

        plan.Directories.ShouldBe(new[] { _workspace }, kind);
        plan.Notices.ShouldBeEmpty(kind);
    }

    [Theory]
    [InlineData(16, true)]
    [InlineData(17, false)]
    public void Nested_directories_load_in_place_up_to_the_directory_budget_and_none_past_it(int directories, bool fits)
    {
        if (OperatingSystem.IsWindows()) return;

        for (var i = 0; i < directories; i++) _tree.File($"ws/pkg-{i:00}/CLAUDE.md", "Package.\n");

        var plan = Plan();

        plan.Directories.Count.ShouldBe(fits ? 1 + directories : 1, "all of them or none of them");
        plan.Notices.ShouldBe(fits ? Array.Empty<string>() : new[] { OverBudgetNotice });
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    public void Nested_memory_loads_in_place_up_to_the_byte_budget_counted_across_directories_and_kinds(int over, bool fits)
    {
        // The budget is on every nested memory file together: a CLAUDE.md in one directory and a rule in another.
        if (OperatingSystem.IsWindows()) return;

        var half = ClaudeWorkspaceMemory.MaxInPlaceBytes / 2;

        _tree.File("ws/a/CLAUDE.md", new string('a', half));
        _tree.File("ws/b/.claude/rules/r.md", new string('b', ClaudeWorkspaceMemory.MaxInPlaceBytes - half + over));

        var plan = Plan();

        plan.Directories.ShouldBe(fits ? new[] { _workspace, At("a"), At("b") } : new[] { _workspace });
        plan.Notices.ShouldBe(fits ? Array.Empty<string>() : new[] { OverBudgetNotice });
    }

    [Fact]
    public void Only_memory_that_loads_in_place_counts_against_the_byte_budget()
    {
        // The workspace's own memory loads either way, and a scoped rule never loads from an added directory.
        if (OperatingSystem.IsWindows()) return;

        _tree.File("ws/CLAUDE.md", new string('w', 2 * ClaudeWorkspaceMemory.MaxInPlaceBytes));
        _tree.File("ws/pkg/CLAUDE.md", "Package.\n");
        _tree.File("ws/pkg/.claude/rules/scoped.md", $"---\npaths: src/**\n---\n{new string('s', 2 * ClaudeWorkspaceMemory.MaxInPlaceBytes)}\n");

        Plan().Directories.ShouldBe(new[] { _workspace, At("pkg") });
    }

    [Theory]
    [InlineData("a nested CLAUDE.md linked outside", "pkg", "CLAUDE.md resolves outside the workspace")]
    [InlineData("a nested .claude directory linked outside", "pkg", ".claude resolves outside the workspace")]
    [InlineData("a nested import of an outside file", "pkg", "a file CLAUDE.md imports resolves outside the workspace")]
    [InlineData("a nested rule linked outside", "pkg", ".claude/rules/style.md resolves outside the workspace")]
    public void A_nested_directory_whose_memory_reaches_outside_the_workspace_is_left_out_by_name(string shape, string directory, string escape)
    {
        if (OperatingSystem.IsWindows()) return;

        var secret = _tree.File("outside/secret.md", "OUTSIDE");

        _tree.File("ws/other/CLAUDE.md", "Other.\n");

        switch (shape)
        {
            case "a nested CLAUDE.md linked outside":
                _tree.Link("ws/pkg/CLAUDE.md", secret);
                break;
            case "a nested .claude directory linked outside":
                _tree.File("outside/dot/CLAUDE.md", "OUTSIDE");
                _tree.Link("ws/pkg/.claude", Path.Combine(_tree.Root, "outside", "dot"));
                break;
            case "a nested import of an outside file":
                _tree.File("ws/pkg/CLAUDE.md", "Read @../../outside/secret.md\n");
                break;
            case "a nested rule linked outside":
                _tree.File("ws/pkg/CLAUDE.md", "Package.\n");
                _tree.Link("ws/pkg/.claude/rules/style.md", secret);
                break;
        }

        var plan = Plan();

        plan.Directories.ShouldBe(new[] { _workspace, At("other") }, shape);
        plan.Notices.ShouldBe(new[] { $"Left the memory in '{directory}' out of this run: {escape}." }, shape);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    public void What_nested_memory_imports_counts_against_the_byte_budget(int over, bool fits)
    {
        // The CLI loads an in-place directory's imports beside its memory, so the budget counts their bytes too.
        if (OperatingSystem.IsWindows()) return;

        const string memory = "@docs/big.md\n";

        _tree.File("ws/pkg/CLAUDE.md", memory);
        _tree.File("ws/pkg/docs/big.md", new string('i', ClaudeWorkspaceMemory.MaxInPlaceBytes - memory.Length + over));

        var plan = Plan();

        plan.Directories.ShouldBe(fits ? new[] { _workspace, At("pkg") } : new[] { _workspace });
        plan.Notices.ShouldBe(fits ? Array.Empty<string>() : new[] { OverBudgetNotice });
    }

    [Theory]
    [InlineData("guide.md", true)]
    [InlineData("missing.md", false)]
    public void A_directory_whose_only_memory_is_a_scoped_rule_loads_in_place_when_the_rule_imports_a_file(string import, bool added)
    {
        // A scoped rule never loads from an added directory, but the files it imports do (2.1.263): they are that
        // directory's memory in place. One that imports nothing holds none.
        if (OperatingSystem.IsWindows()) return;

        _tree.File("ws/pkg/guide.md", "Guide.\n");
        _tree.File("ws/pkg/.claude/rules/ts.md", $"---\npaths: \"*.ts\"\n---\nTypes. See @../../{import}\n");

        var plan = Plan();

        plan.Directories.ShouldBe(added ? new[] { _workspace, At("pkg") } : new[] { _workspace }, import);
        plan.Notices.ShouldBeEmpty(import);
    }

    [Theory]
    [InlineData("paths: src\n", false)]
    [InlineData("description: style\n", true)]
    public void A_rule_whose_frontmatter_runs_past_the_head_is_read_to_its_close(string key, bool added)
    {
        // Classified from its first 4 KiB alone, a scoped rule with a long frontmatter would read as unconditional and
        // add its directory in place for nothing.
        if (OperatingSystem.IsWindows()) return;

        var paths = string.Concat(Enumerable.Range(0, 200).Select(i => $"# padding line {i:000} of the frontmatter\n"));

        _tree.File("ws/pkg/.claude/rules/long.md", $"---\n{paths}{key}---\nRule.\n");

        File.ReadAllText(At("pkg/.claude/rules/long.md")).IndexOf("\n---\n", StringComparison.Ordinal).ShouldBeGreaterThan(ClaudeRuleScope.MaxHeadBytes, "fixture check: the fence closes past the head");
        Plan().Directories.ShouldBe(added ? new[] { _workspace, At("pkg") } : new[] { _workspace }, key);
    }

    [Fact]
    public void A_rule_whose_frontmatter_never_closes_within_what_the_runner_reads_is_unclassified_and_said()
    {
        if (OperatingSystem.IsWindows()) return;

        _tree.File("ws/pkg/.claude/rules/endless.md", "---\n" + string.Concat(Enumerable.Repeat("key: value\n", ClaudeWorkspaceMemory.MaxScannedBytes / 11 + 1)));

        var plan = Plan();

        plan.Directories.ShouldBe(new[] { _workspace });
        plan.Notices.ShouldBe(new[] { $"Left the rule 'pkg/.claude/rules/endless.md' unclassified: its frontmatter runs past {ClaudeWorkspaceMemory.MaxScannedBytes} bytes, more than the runner reads to tell whether paths: scope it." });
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void A_rule_linked_outside_the_cwd_is_no_memory_of_its_directory(bool intoSibling, bool added)
    {
        // At a primary-repository cwd the sibling is the run's own clone, so the guard admits a link into it, but the CLI
        // reads no rules entry that links outside its cwd (2.1.263): the directory holds nothing to load in place.
        if (OperatingSystem.IsWindows()) return;

        var (cwd, sibling) = (At("repo-a"), At("repo-b"));

        _tree.File("ws/repo-b/rules/plain.md", "A sibling's rule.\n");
        _tree.File("ws/repo-a/shared/plain.md", "A rule of the cwd's own.\n");
        _tree.Link("ws/repo-a/pkg/.claude/rules/plain.md", intoSibling ? "../../../../repo-b/rules/plain.md" : "../../../shared/plain.md");

        var plan = ClaudeWorkspaceMemory.For(new AgentTask { Goal = "g", Harness = ClaudeCodeHarness.HarnessKind, WorkspaceDirectory = cwd, WorkspaceRepositoryDirectories = [cwd, sibling] });

        plan.Directories.ShouldBe(added ? new[] { cwd, Path.Combine(cwd, "pkg") } : new[] { cwd });
        plan.Notices.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("a walk past links that climb a long chain to nothing")]
    [InlineData("in-place memory whose import names links that climb a long chain")]
    public async Task A_tree_of_links_that_climb_a_long_chain_spends_the_build_budget_and_stops_in_bounded_time(string shape)
    {
        // Each link hop may push any number of components, so one lookup through a 39-hop chain of d/../ pairs walks
        // thousands of them: per directory and per lookup that took minutes over a whole tree. The workspace's own memory
        // is checked first, so it still loads.
        if (OperatingSystem.IsWindows()) return;

        _tree.File("ws/CLAUDE.md", "Root.\n");
        PlantChain();

        if (shape.StartsWith("a walk", StringComparison.Ordinal))
        {
            // The chain's end is gone, so no directory holds memory and the walk goes on past every one of them.
            File.Delete(Path.Combine(_workspace, ".x", "real.md"));

            for (var i = 0; i < 1000; i++) _tree.Link($"ws/p{i:0000}/CLAUDE.md", "../.x/L0");
        }
        else
        {
            // A few bytes of imports per directory, well inside the in-place budget, each a link into the chain.
            for (var i = 0; i < 200; i++) _tree.Link($"ws/.y/i{i:000}", "../.x/L0");

            _tree.File("ws/.y/shared.md", string.Concat(Enumerable.Range(0, 200).Select(i => $"@i{i:000} ")));

            for (var i = 0; i < ClaudeWorkspaceMemory.MaxInPlaceDirectories; i++) _tree.File($"ws/p{i:00}/CLAUDE.md", "@../.y/shared.md\n");
        }

        var plan = await System.Threading.Tasks.Task.Run(Plan).WaitAsync(TimeSpan.FromSeconds(30));

        plan.Directories.ShouldBe(new[] { _workspace }, shape);
        plan.Notices.ShouldBe(new[] { BudgetNotice }, shape);
    }

    [Fact]
    public void Imports_every_directory_shares_are_resolved_once_per_build()
    {
        // Twenty links into the long chain cost a fifth of the budget once; resolved again for each of sixteen
        // directories they would spend it three times over.
        if (OperatingSystem.IsWindows()) return;

        PlantChain();

        for (var i = 0; i < 20; i++) _tree.Link($"ws/.y/i{i:000}", "../.x/L0");

        _tree.File("ws/.y/shared.md", string.Concat(Enumerable.Range(0, 20).Select(i => $"@i{i:000} ")));

        for (var i = 0; i < ClaudeWorkspaceMemory.MaxInPlaceDirectories; i++) _tree.File($"ws/p{i:00}/CLAUDE.md", "@../.y/shared.md\n");

        var plan = Plan();

        plan.Notices.ShouldBeEmpty();
        plan.Directories.Count.ShouldBe(1 + ClaudeWorkspaceMemory.MaxInPlaceDirectories);
    }

    [Fact]
    public void The_build_budget_is_pinned()
    {
        // Committed values, changed by PR: together they bound what one build spends finding and checking memory.
        ClaudeWorkspaceMemory.MaxBuildLookups.ShouldBe(65536);
        ClaudeWorkspaceMemory.MaxBuildComponents.ShouldBe(1048576);
        ClaudeWorkspaceMemory.MaxBuildBytes.ShouldBe(67108864);
    }

    [Fact]
    public void A_nested_directory_left_out_for_its_link_still_counts_against_the_budget()
    {
        // The budget is spent before any directory's memory is followed to where it leads, so the guard runs on at most
        // MaxInPlaceDirectories nested directories in one build however many link outside.
        if (OperatingSystem.IsWindows()) return;

        for (var i = 0; i < ClaudeWorkspaceMemory.MaxInPlaceDirectories; i++) _tree.File($"ws/pkg-{i:00}/CLAUDE.md", "Package.\n");

        _tree.Link("ws/pkg-99/CLAUDE.md", _tree.File("outside/secret.md", "OUTSIDE"));

        var plan = Plan();

        plan.Directories.ShouldBe(new[] { _workspace });
        plan.Notices.ShouldBe(new[] { OverBudgetNotice });
    }

    [Fact]
    public void The_walk_follows_no_link_and_enters_no_dot_directory_or_node_modules()
    {
        if (OperatingSystem.IsWindows()) return;

        _tree.File("ws/.hidden/CLAUDE.md", "Hidden.\n");
        _tree.File("ws/.github/CLAUDE.md", "GitHub.\n");
        _tree.File("ws/node_modules/x/CLAUDE.md", "Module.\n");
        _tree.File("ws/real/CLAUDE.md", "Real.\n");
        _tree.Link("ws/linked", At("real"));
        _tree.File("outside/dir/CLAUDE.md", "OUTSIDE");
        _tree.Link("ws/out", Path.Combine(_tree.Root, "outside", "dir"));

        var plan = Plan();

        plan.Directories.ShouldBe(new[] { _workspace, At("real") }, "a linked directory is not walked, whether it leads inside or out");
        plan.Notices.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(32, true)]
    [InlineData(33, false)]
    public void The_walk_looks_no_deeper_than_its_depth_bound_and_says_so(int depth, bool found)
    {
        if (OperatingSystem.IsWindows()) return;

        var directory = string.Join('/', Enumerable.Range(1, depth).Select(level => $"d{level}"));

        _tree.File($"ws/{directory}/CLAUDE.md", "Deep.\n");

        var plan = Plan();

        plan.Directories.ShouldBe(found ? new[] { _workspace, At(directory) } : new[] { _workspace });
        plan.Notices.ShouldBe(found ? Array.Empty<string>() : new[] { $"Left any memory more than {ClaudeWorkspaceMemory.MaxWalkDepth} directories below the workspace out of this run: the runner looks no deeper." });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void The_walk_examines_no_more_than_its_entry_bound_and_says_so(bool overTheBound)
    {
        // The cwd is the first entry examined and zz, sorted after every d-directory, the last: past the bound it is never reached.
        if (OperatingSystem.IsWindows()) return;

        var filler = ClaudeWorkspaceMemory.MaxWalkedEntries - 2 + (overTheBound ? 1 : 0);

        for (var i = 0; i < filler; i++) Directory.CreateDirectory(Path.Combine(_workspace, $"d{i:00000}"));

        _tree.File("ws/zz/CLAUDE.md", "Last.\n");

        var plan = Plan();

        plan.Directories.ShouldBe(overTheBound ? new[] { _workspace } : new[] { _workspace, At("zz") });
        plan.Notices.ShouldBe(overTheBound ? new[] { $"Left any memory past the first {ClaudeWorkspaceMemory.MaxWalkedEntries} directories and rules the runner examined out of this run: it looks no further." } : Array.Empty<string>());
    }

    [Theory]
    [InlineData("my pkg", "my pkg")]
    [InlineData("tab\tname", "tab?name")]
    [InlineData("na\u00efve", "na\u00efve")]
    [InlineData("quote'name", "quote'name")]
    public void A_nested_directory_whose_path_could_not_ride_the_argv_safely_is_left_out_by_name(string name, string said)
    {
        if (OperatingSystem.IsWindows()) return;

        _tree.File($"ws/{name}/CLAUDE.md", "Package.\n");
        _tree.File("ws/packages/@scope/ui+web_v1.2-x/CLAUDE.md", "Scoped package.\n");

        var plan = Plan();

        plan.Directories.ShouldBe(new[] { _workspace, At("packages/@scope/ui+web_v1.2-x") }, "the characters a package path commonly holds still ride the argv");
        plan.Notices.ShouldBe(new[] { $"Left the memory in '{said}' out of this run: its path holds a character other than a letter, a digit or one of . _ @ + - /." });
    }

    [Fact]
    public async Task A_fifo_in_nested_memory_is_never_opened_and_holds_nothing()
    {
        if (OperatingSystem.IsWindows()) return;

        _tree.Directory("ws/pkg/.claude/rules");
        await MakeFifoAsync(At("pkg/CLAUDE.md"));
        await MakeFifoAsync(At("pkg/.claude/rules/pipe.md"));
        _tree.File("ws/other/CLAUDE.md", "Other.\n");

        var plan = await System.Threading.Tasks.Task.Run(Plan).WaitAsync(TimeSpan.FromSeconds(1));

        plan.Directories.ShouldBe(new[] { _workspace, At("other") });
        plan.Notices.ShouldBeEmpty();
    }

    [Fact]
    public void Nested_memory_written_after_one_build_is_in_the_next()
    {
        // Every build walks again — a revise round's included — so memory an earlier round's agent wrote loads in the next.
        if (OperatingSystem.IsWindows()) return;

        Plan().Directories.ShouldBe(new[] { _workspace }, "fixture check: nothing nested yet");

        _tree.File("ws/lib/CLAUDE.md", "Library.\n");

        Plan().Directories.ShouldBe(new[] { _workspace, At("lib") });
    }

    [Fact]
    public void The_bounds_are_pinned()
    {
        // Committed values, changed by PR: each decides how much nested memory loads before the first request, or how
        // much one build reads to find it.
        ClaudeWorkspaceMemory.MaxInPlaceDirectories.ShouldBe(16);
        ClaudeWorkspaceMemory.MaxInPlaceBytes.ShouldBe(32768);
        ClaudeWorkspaceMemory.MaxWalkDepth.ShouldBe(32);
        ClaudeWorkspaceMemory.MaxWalkedEntries.ShouldBe(20000);
    }

    private ClaudeWorkspaceMemory.Plan Plan() =>
        ClaudeWorkspaceMemory.For(new AgentTask { Goal = "g", Harness = ClaudeCodeHarness.HarnessKind, WorkspaceDirectory = _workspace, WorkspaceRepositoryDirectories = [_workspace] });

    /// <summary>The path at <paramref name="relative"/> below the workspace, as the workspace spells it.</summary>
    private string At(string relative) => Path.Combine(_workspace, relative);

    private (string Cwd, string[] Repositories, string[] Added) PlantLayout(string shape)
    {
        switch (shape)
        {
            case "single-repo":
                _tree.File("ws/b/CLAUDE.md", "B.\n");
                _tree.File("ws/a/x/CLAUDE.md", "AX.\n");
                _tree.File("ws/a/CLAUDE.md", "A.\n");
                _tree.File("ws/c/d/e/CLAUDE.md", "CDE.\n");
                _tree.File("ws/c/notes.md", "No memory here.\n");
                return (_workspace, [_workspace], [_workspace, At("a"), At("b"), At("a/x"), At("c/d/e")]);
            case "multi-repo at its root":
                _tree.File("ws/r2/pkg/CLAUDE.md", "R2.\n");
                _tree.File("ws/r1/pkg/CLAUDE.md", "R1.\n");
                _tree.File("ws/r1/pkg/deep/CLAUDE.md", "R1 deep.\n");
                _tree.File("ws/tools/CLAUDE.md", "Tools.\n");
                _tree.File("ws/r1/CLAUDE.md", "R1 root.\n");
                return (_workspace, [At("r1"), At("r2")], [_workspace, At("r1"), At("r2"), At("tools"), At("r1/pkg"), At("r2/pkg"), At("r1/pkg/deep")]);
            case "multi-repo at its primary repository":
                _tree.File("ws/repo-a/pkg/CLAUDE.md", "A.\n");
                _tree.File("ws/repo-b/pkg/CLAUDE.md", "B, a sibling the cwd does not hold.\n");
                return (At("repo-a"), [At("repo-a"), At("repo-b")], [At("repo-a"), At("repo-a/pkg")]);
            default:
                throw new ArgumentOutOfRangeException(nameof(shape), shape, null);
        }
    }

    private void PlantMemory(string kind)
    {
        switch (kind)
        {
            case "a CLAUDE.md":
                _tree.File("ws/pkg/CLAUDE.md", "Package.\n");
                break;
            case "a .claude/CLAUDE.md":
                _tree.File("ws/pkg/.claude/CLAUDE.md", "Package.\n");
                break;
            case "a rule without paths:":
                _tree.File("ws/pkg/.claude/rules/style.md", "Tabs.\n");
                break;
            case "a rule in a folder under rules":
                _tree.File("ws/pkg/.claude/rules/team/style.md", "---\ndescription: team style\n---\nTabs.\n");
                break;
            case "a rule scoped to ** alone":
                _tree.File("ws/pkg/.claude/rules/all.md", "---\npaths: \"**\"\n---\nEverywhere.\n");
                break;
            case "a CLAUDE.md linked to the AGENTS.md beside it":
                _tree.File("ws/pkg/AGENTS.md", "Package.\n");
                _tree.Link("ws/pkg/CLAUDE.md", "AGENTS.md");
                break;
            case "a .claude directory linked to another inside the workspace":
                _tree.File("ws/.shared/CLAUDE.md", "Shared.\n");
                _tree.Link("ws/pkg/.claude", At(".shared"));
                break;
            case "a rule scoped by paths: alone":
                _tree.File("ws/pkg/.claude/rules/ts.md", "---\npaths:\n  - \"**/*.ts\"\n---\nTypes.\n");
                break;
            case "a rules file that is not markdown":
                _tree.File("ws/pkg/.claude/rules/notes.txt", "Not a rule.\n");
                break;
            case "a CLAUDE.md link that dangles":
                _tree.Link("ws/pkg/CLAUDE.md", "missing.md");
                break;
            case "a directory named CLAUDE.md":
                _tree.Directory("ws/pkg/CLAUDE.md");
                break;
            case "settings and nothing else":
                _tree.File("ws/pkg/.claude/settings.json", "{\"env\":{}}");
                break;
            case "an AGENTS.md":
                _tree.File("ws/pkg/AGENTS.md", "Codex reads this, Claude does not.\n");
                break;
            case "a CLAUDE.local.md":
                _tree.File("ws/pkg/CLAUDE.local.md", "A developer's own notes.\n");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, null);
        }
    }

    /// <summary>A chain of 39 links, <c>ws/.x/L0</c> on, one short of the kernel's limit, each target climbing <c>d/../</c> 150 times before it names the next link; the last names <c>ws/.x/real.md</c>.</summary>
    private void PlantChain()
    {
        var padding = string.Concat(Enumerable.Repeat("d/../", 150));

        _tree.Directory("ws/.x/d");
        _tree.File("ws/.x/real.md", "Real.\n");

        for (var hop = 38; hop >= 0; hop--) _tree.Link($"ws/.x/L{hop}", padding + (hop == 38 ? "real.md" : $"L{hop + 1}"));
    }

    private static async Task MakeFifoAsync(string path)
    {
        using var mkfifo = Process.Start("mkfifo", path)!;
        await mkfifo.WaitForExitAsync();
        mkfifo.ExitCode.ShouldBe(0, $"fixture check: mkfifo {path}");
    }
}
