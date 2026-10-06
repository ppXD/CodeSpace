using System.Text.RegularExpressions;
using CodeSpace.Core.Services.Agents.Harnesses.Claude;
using CodeSpace.Messages.Agents;
using Shouldly;
using YamlDotNet.RepresentationModel;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// Pins the pointer rules a Claude run's config home carries (<see cref="ClaudeWorkspaceMemory"/>): one per rule scoped
/// by <c>paths:</c> anywhere in the workspace, and one per nested directory past the in-place budget, each a runner
/// sentence naming the files to read under <c>paths:</c> rebased onto the cwd — and never a repository byte, an
/// <c>@</c>, or globs the CLI would read as unconditional. Each case lays out a real tree under a GUID temp root reached
/// through a symlink (<see cref="TempTree"/>), as production never resolves a workspace path.
/// </summary>
[Trait("Category", "Unit")]
public sealed partial class ClaudePointerRulesTests : IDisposable
{
    private const string OverBudgetNotice = "Left the memory of every nested directory out of the run's first request: together it spans more than 16 directories or 32768 bytes. A read below one of them points the run at that directory's memory instead.";

    private const string BudgetNotice = "Left any memory the runner had not yet found or checked out of this run: finding and checking it would take more than one build spends (65536 paths, 1048576 path components, 67108864 bytes).";

    private readonly TempTree _tree = new();
    private readonly string _workspace;

    public ClaudePointerRulesTests() { _workspace = _tree.Directory("ws"); }

    public void Dispose() => _tree.Dispose();

    [Fact]
    public void A_scoped_rule_gets_one_pointer_that_names_it_under_its_own_globs_and_holds_none_of_its_text()
    {
        if (OperatingSystem.IsWindows()) return;

        _tree.File("ws/.claude/rules/ts.md", "---\npaths:\n  - \"src/**/*.ts\"\n---\nREPO-RULE-TEXT: see @docs/rule-notes.md\n");

        var plan = Plan();

        plan.Directories.ShouldBe(new[] { _workspace }, "a scoped rule never loads in place");
        plan.Notices.ShouldBeEmpty();
        plan.Pointers.ShouldHaveSingleItem().RelativePath.ShouldBe("rules/codespace-repository-000.md");
        plan.Pointers[0].IsExecutable.ShouldBeFalse();
        plan.Pointers[0].Content.ShouldBe($"---\npaths:\n  - \"src/**/*.ts\"\n---\nThe repository rule `{At(".claude/rules/ts.md")}` applies to the file you just read. Read it now and follow it for files it matches.\n");
    }

    [Fact]
    public void Past_the_in_place_budget_each_nested_directory_gets_a_pointer_naming_its_memory_files_before_its_rules()
    {
        if (OperatingSystem.IsWindows()) return;

        for (var i = 0; i <= ClaudeWorkspaceMemory.MaxInPlaceDirectories; i++) _tree.File($"ws/pkg-{i:00}/CLAUDE.md", "Package.\n");

        _tree.File("ws/pkg-00/.claude/CLAUDE.md", "Package, again.\n");
        _tree.File("ws/pkg-00/.claude/rules/style.md", "Tabs.\n");
        _tree.File("ws/pkg-00/.claude/rules/ts.md", "---\npaths: \"*.ts\"\n---\nTypes.\n");

        var plan = Plan();

        plan.Directories.ShouldBe(new[] { _workspace }, "past the budget no nested directory is added");
        plan.Notices.ShouldBe(new[] { OverBudgetNotice });
        plan.Pointers.Select(pointer => pointer.RelativePath).ShouldBe(Enumerable.Range(0, ClaudeWorkspaceMemory.MaxInPlaceDirectories + 2).Select(i => $"rules/codespace-repository-{i:000}.md"), "numbered in walk order");
        plan.Pointers[0].Content.ShouldBe($"---\npaths:\n  - \"/pkg-00\"\n---\nRepository instructions for `pkg-00/` were not preloaded: `{At("pkg-00/CLAUDE.md")}`, `{At("pkg-00/.claude/CLAUDE.md")}`, `{At("pkg-00/.claude/rules/style.md")}`. Read them now and follow them while you work under `pkg-00/`.\n");
        Globs(plan.Pointers[1]).ShouldBe(new[] { "/pkg-00/**/*.ts" }, "a directory's own memory comes before its rules");
        plan.Pointers.Skip(2).Select(Globs).ShouldBe(Enumerable.Range(1, ClaudeWorkspaceMemory.MaxInPlaceDirectories).Select(i => new[] { $"/pkg-{i:00}" }));
    }

    [Fact]
    public void Within_the_budget_nested_memory_loads_in_place_and_is_pointed_at_for_a_subagent_beside_its_scoped_rules()
    {
        // An Explore or Plan subagent starts without project memory, so memory loaded in place reaches it only through a
        // pointer; the unpinned CLI attached it there on the subagent's first read below the directory.
        if (OperatingSystem.IsWindows()) return;

        _tree.File("ws/pkg/CLAUDE.md", "Package.\n");
        _tree.File("ws/pkg/.claude/rules/ts.md", "---\npaths: \"*.ts\"\n---\nTypes.\n");
        _tree.File("ws/lib/.claude/rules/py.md", "---\npaths: [\"/x/**\", \"!x/gen\"]\n---\nPython.\n");

        var plan = Plan();

        plan.Directories.ShouldBe(new[] { _workspace, At("pkg") }, "a directory with only a scoped rule that imports nothing holds no memory to load in place");
        plan.Pointers.Where(IsRulePointer).Select(Described).ShouldBe(new[] { "/lib/x,!/lib/x/gen -> lib/.claude/rules/py.md", "/pkg/**/*.ts -> pkg/.claude/rules/ts.md" });
        plan.Pointers.Single(pointer => !IsRulePointer(pointer)).Content.ShouldBe($"---\npaths:\n  - \"/pkg\"\n---\nRepository instructions for `pkg/` are in `{At("pkg/CLAUDE.md")}`. Read them now if they are not already in your context, and follow them while you work under `pkg/`.\n");
        plan.Pointers.Select(pointer => pointer.RelativePath).ShouldBe(Enumerable.Range(0, 3).Select(i => $"rules/codespace-repository-{i:000}.md"), "numbered in walk order, a directory's memory before its rules");
        plan.Notices.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("single-repo")]
    [InlineData("multi-repo at its root")]
    [InlineData("multi-repo at its primary repository")]
    public void Every_scoped_rule_below_the_cwd_is_rebased_onto_it_and_none_outside_it_is_pointed_at(string shape)
    {
        if (OperatingSystem.IsWindows()) return;

        var (cwd, repositories, expected) = PlantLayout(shape);

        var plan = ClaudeWorkspaceMemory.For(new AgentTask { Goal = "g", Harness = ClaudeCodeHarness.HarnessKind, WorkspaceDirectory = cwd, WorkspaceRepositoryDirectories = repositories });

        plan.Pointers.Select(Described).ShouldBe(expected, shape);
        plan.Notices.ShouldBeEmpty(shape);
    }

    [Fact]
    public void Rules_are_pointed_at_in_walk_order_folders_followed_by_name()
    {
        if (OperatingSystem.IsWindows()) return;

        _tree.File("ws/.claude/rules/c.md", "---\npaths: c\n---\nC.\n");
        _tree.File("ws/.claude/rules/b.md", "---\npaths: b\n---\nB.\n");
        _tree.File("ws/.claude/rules/a/z.md", "---\npaths: z\n---\nZ.\n");
        _tree.File("ws/.claude/rules/plain.md", "Unscoped: loads with the workspace.\n");
        _tree.File("ws/deep/er/.claude/rules/d.md", "---\npaths: d\n---\nD.\n");
        _tree.File("ws/aa/.claude/rules/e.md", "---\npaths: e\n---\nE.\n");

        Plan().Pointers.Select(pointer => (pointer.RelativePath, Globs(pointer).Single())).ShouldBe(new[]
        {
            ("rules/codespace-repository-000.md", "z"),
            ("rules/codespace-repository-001.md", "b"),
            ("rules/codespace-repository-002.md", "c"),
            ("rules/codespace-repository-003.md", "/aa/**/e"),
            ("rules/codespace-repository-004.md", "/deep/er/**/d"),
        });
    }

    [Theory]
    [InlineData("ws/.claude/rules/x.md", "---\npaths: \"my dir/*.ts\"\n---\nR.\n", "Left the rule '.claude/rules/x.md' out of this run: its paths: hold 'my dir/*.ts', which no pointer can carry exactly.")]
    [InlineData("ws/.claude/rules/x.md", "---\npaths: \"@scope/**\"\n---\nR.\n", "Left the rule '.claude/rules/x.md' out of this run: its paths: hold '@scope', which no pointer can carry exactly.")]
    [InlineData("ws/.claude/rules/x.md", "---\npaths: [\"gen/**\", \"!gen/my keep\"]\n---\nR.\n", "Left the rule '.claude/rules/x.md' out of this run: its paths: hold '!gen/my keep', which no pointer can carry exactly.")]
    [InlineData("ws/.claude/rules/x.md", "---\npaths: \"{a,b,c\"\n---\nR.\n", "Left the rule '.claude/rules/x.md' out of this run: its paths: hold '{a,b,c', which no pointer can carry exactly.")]
    [InlineData("ws/.claude/rules/my rule.md", "---\npaths: src\n---\nR.\n", "Left the rule '.claude/rules/my rule.md' out of this run: its path holds a character other than a letter, a digit or one of . _ + - /.")]
    [InlineData("ws/packages/@scope/ui/.claude/rules/x.md", "---\npaths: src\n---\nR.\n", "Left the rule 'packages/@scope/ui/.claude/rules/x.md' out of this run: its path holds a character other than a letter, a digit or one of . _ + - /.")]
    public void A_rule_no_pointer_can_carry_exactly_gets_none_and_is_named(string rule, string text, string notice)
    {
        // One glob it cannot carry and the whole rule goes: dropping only that glob could drop a negation and widen the rule.
        if (OperatingSystem.IsWindows()) return;

        _tree.File(rule, text);

        var plan = Plan();

        plan.Pointers.ShouldBeEmpty();
        plan.Notices.ShouldBe(new[] { notice });
    }

    [Fact]
    public void A_nested_directory_past_the_budget_whose_path_holds_an_at_sign_gets_no_pointer_and_is_named()
    {
        // An @ rides the argv safely, so the directory would load in place within the budget; a pointer holds none at all.
        if (OperatingSystem.IsWindows()) return;

        for (var i = 0; i < ClaudeWorkspaceMemory.MaxInPlaceDirectories; i++) _tree.File($"ws/pkg-{i:00}/CLAUDE.md", "Package.\n");

        _tree.File("ws/packages/@scope/CLAUDE.md", "Scoped package.\n");

        var plan = Plan();

        plan.Pointers.Count.ShouldBe(ClaudeWorkspaceMemory.MaxInPlaceDirectories);
        plan.Notices.ShouldBe(new[] { OverBudgetNotice, "Left the memory in 'packages/@scope' out of this run: its path holds a character other than a letter, a digit or one of . _ + - /." });
    }

    [Theory]
    [InlineData("a scoped rule beside a nested CLAUDE.md that imports an outside file", "Left the memory in 'pkg' out of this run: a file CLAUDE.md imports resolves outside the workspace.")]
    [InlineData("a scoped rule that imports an outside file, alone in its directory", "Left the memory in 'pkg' out of this run: a file .claude/rules/ts.md imports resolves outside the workspace.")]
    [InlineData("a scoped rule in a .claude directory linked outside", "Left the memory in 'pkg' out of this run: .claude resolves outside the workspace.")]
    [InlineData("a scoped rule beside the workspace's own CLAUDE.md linked outside", "Left the memory in the workspace out of this run: CLAUDE.md resolves outside the workspace.")]
    public void A_directory_whose_memory_reaches_outside_the_workspace_gets_no_pointer_and_is_named_once(string shape, string notice)
    {
        // A pointer names files the model may read, so the directory it points into passes the same guard as an --add-dir.
        if (OperatingSystem.IsWindows()) return;

        var secret = _tree.File("outside/secret.md", "OUTSIDE");
        const string scoped = "---\npaths: \"*.ts\"\n---\nTypes.\n";

        switch (shape)
        {
            case "a scoped rule beside a nested CLAUDE.md that imports an outside file":
                _tree.File("ws/pkg/CLAUDE.md", "See @../../outside/secret.md\n");
                _tree.File("ws/pkg/.claude/rules/ts.md", scoped);
                break;
            case "a scoped rule that imports an outside file, alone in its directory":
                _tree.File("ws/pkg/.claude/rules/ts.md", scoped + "See @../../../../outside/secret.md\n");
                break;
            case "a scoped rule in a .claude directory linked outside":
                _tree.File("outside/dot/rules/ts.md", scoped);
                _tree.Link("ws/pkg/.claude", Path.Combine(_tree.Root, "outside", "dot"));
                break;
            case "a scoped rule beside the workspace's own CLAUDE.md linked outside":
                _tree.Link("ws/CLAUDE.md", secret);
                _tree.File("ws/.claude/rules/ts.md", scoped);
                break;
        }

        var plan = Plan();

        plan.Pointers.ShouldBeEmpty(shape);
        plan.Notices.ShouldBe(new[] { notice }, shape);
    }

    [Fact]
    public void No_pointer_holds_an_at_sign_a_repository_byte_or_anything_but_paths_in_its_frontmatter()
    {
        // The property behind every pointer, over a tree that tries each way in: rule text full of imports and mentions,
        // globs and names with @, an email in nested memory, a dot-directory and node_modules, both kinds of pointer.
        if (OperatingSystem.IsWindows()) return;

        // An import that resolves outside would leave its directory out (the guard), so these name nothing that exists.
        const string text = "REPO-TEXT @/cs-no-such-directory/passwd @~/.mcp.json @\"~/.mcp.json\" 。@x mail@example.com\n";

        for (var i = 0; i <= ClaudeWorkspaceMemory.MaxInPlaceDirectories; i++) _tree.File($"ws/pkg-{i:00}/CLAUDE.md", text);

        _tree.File("ws/.claude/rules/a.md", $"---\npaths: [\"src/**\", \"!src/gen\", \"*.ts\"]\n---\n{text}");
        _tree.File("ws/.claude/rules/b.md", $"---\npaths: \"@x/**\"\n---\n{text}");
        _tree.File("ws/pkg-03/.claude/rules/c.md", $"---\npaths: \"{{lib/a,b}}\"\n---\n{text}");
        _tree.File("ws/web/@org/ui/.claude/rules/d.md", $"---\npaths: src\n---\n{text}");
        _tree.File("ws/web/@org/ui/CLAUDE.md", text);
        _tree.File("ws/.github/.claude/rules/e.md", $"---\npaths: src\n---\n{text}");
        _tree.File("ws/node_modules/x/.claude/rules/f.md", $"---\npaths: src\n---\n{text}");

        var plan = Plan();

        plan.Pointers.Count.ShouldBe(ClaudeWorkspaceMemory.MaxInPlaceDirectories + 1 + 2, "fixture check: every directory pointer and the a.md and c.md rule pointers");
        plan.Pointers.ShouldAllBe(pointer => !pointer.Content.Contains('@') && !pointer.Content.Contains("REPO-TEXT", StringComparison.Ordinal) && !pointer.Content.Contains('\''));
        plan.Pointers.SelectMany(pointer => pointer.Content.Split('\n')).ShouldNotContain(line => MentionStart().IsMatch(line));
        plan.Pointers.ShouldAllBe(pointer => pointer.RelativePath.StartsWith(ClaudeWorkspaceMemory.PointerRulePrefix, StringComparison.Ordinal) && !pointer.RelativePath.Contains(".."));
        plan.Pointers.Select(FrontmatterKeys).ShouldAllBe(keys => keys.SequenceEqual(new[] { "paths" }));
        plan.Pointers.ShouldAllBe(pointer => ClaudeRuleScope.Read(pointer.Content) != null, "no pointer is unconditional");
    }

    [Fact]
    public void A_run_carries_no_more_pointers_than_the_bound_and_says_so()
    {
        if (OperatingSystem.IsWindows()) return;

        for (var i = 0; i <= ClaudeWorkspaceMemory.MaxPointerRules; i++) _tree.File($"ws/.claude/rules/r{i:000}.md", $"---\npaths: src/{i}\n---\nRule.\n");

        var plan = Plan();

        plan.Pointers.Count.ShouldBe(ClaudeWorkspaceMemory.MaxPointerRules);
        plan.Pointers[^1].RelativePath.ShouldBe("rules/codespace-repository-127.md");
        plan.Notices.ShouldBe(new[] { "Left the repository rules and nested memory past the first 128 pointers out of this run: the runner writes no more." });
    }

    [Fact]
    public void A_run_checks_no_more_directories_for_pointers_than_the_bound_and_says_so()
    {
        // Every directory's memory reaches outside, so none gets a pointer: the guard, which reads each one's imports,
        // still runs on no more than the bound.
        if (OperatingSystem.IsWindows()) return;

        _tree.File("outside/secret.md", "OUTSIDE");

        for (var i = 0; i <= ClaudeWorkspaceMemory.MaxPointerRules; i++) _tree.File($"ws/d{i:000}/.claude/rules/ts.md", "---\npaths: \"*.ts\"\n---\nSee @../../../../outside/secret.md\n");

        var plan = Plan();

        // Each directory's rule imports a file, so the first seventeen also count against the in-place budget.
        plan.Pointers.ShouldBeEmpty();
        plan.Notices.Count.ShouldBe(ClaudeWorkspaceMemory.MaxPointerRules + 2);
        plan.Notices[0].ShouldBe(OverBudgetNotice);
        plan.Notices[^1].ShouldBe("Left the repository rules and nested memory past the first 128 pointers out of this run: the runner writes no more.");
        plan.Notices.ShouldNotContain(notice => notice.Contains($"'d{ClaudeWorkspaceMemory.MaxPointerRules:000}'", StringComparison.Ordinal), "the directory past the bound is never checked");
    }

    [Fact]
    public void A_rule_the_cli_reads_without_globs_loads_in_place_and_gets_no_pointer_of_its_own()
    {
        if (OperatingSystem.IsWindows()) return;

        _tree.File("ws/pkg/.claude/rules/all.md", "---\npaths: \"**\"\n---\nEverywhere.\n");

        var plan = Plan();

        plan.Directories.ShouldBe(new[] { _workspace, At("pkg") });
        plan.Pointers.ShouldNotContain(pointer => IsRulePointer(pointer));
        plan.Pointers.ShouldHaveSingleItem().Content.ShouldContain($"are in `{At("pkg/.claude/rules/all.md")}`", Case.Sensitive, "the directory's own pointer names it as memory");
    }

    [Fact]
    public void A_scoped_rule_written_after_one_build_is_pointed_at_in_the_next()
    {
        // Every build walks again — a revise round's included — so a rule an earlier round's agent wrote is pointed at.
        if (OperatingSystem.IsWindows()) return;

        Plan().Pointers.ShouldBeEmpty("fixture check: no rule yet");

        _tree.File("ws/lib/.claude/rules/py.md", "---\npaths: \"*.py\"\n---\nPython.\n");

        Plan().Pointers.Select(Globs).ShouldBe(new[] { new[] { "/lib/**/*.py" } });
    }

    [Fact]
    public void A_directory_of_ten_thousand_rules_gets_a_pointer_within_its_byte_bound_that_counts_what_it_cannot_name()
    {
        // Past the budget a directory's pointer named every memory file: thousands of rules, empty ones included, made one
        // pointer megabytes long, and the read that attached it ended the run.
        if (OperatingSystem.IsWindows()) return;

        PlantOverBudget();

        for (var i = 0; i < 10000; i++) _tree.File($"ws/pkg-00/.claude/rules/r{i:00000}.md", "");

        var pointer = Plan().Pointers[0];
        var named = Regex.Matches(pointer.Content, @"`(/[^`]+\.md)`").Select(match => match.Groups[1].Value).ToList();

        pointer.Content.Length.ShouldBeLessThanOrEqualTo(ClaudeWorkspaceMemory.MaxPointerBytes);
        named[0].ShouldBe(At("pkg-00/CLAUDE.md"), "the directory's CLAUDE.md is named first");
        named.Count.ShouldBeGreaterThan(1, "as many files are named as fit");
        pointer.Content.ShouldContain($" and {10001 - named.Count} more memory files under `{At("pkg-00")}/.claude/`. Read them now", Case.Sensitive);
    }

    [Fact]
    public void A_launch_whose_tree_names_thousands_of_long_memory_paths_still_fits_its_frame()
    {
        // Three directories of 6300 rules each, every path 900-odd bytes: named one by one, their pointers outgrew the
        // 16 MiB launch frame and the launch was refused as if its goal were too long.
        if (OperatingSystem.IsWindows()) return;

        var folder = string.Join('/', "abc".Select(letter => new string(letter, 250)));

        PlantOverBudget();

        for (var directory = 0; directory < 3; directory++)
        {
            for (var i = 0; i < 6300; i++) _tree.File($"ws/pkg-{directory:00}/.claude/rules/{folder}/r{i:00000}.md", "");
        }

        var spec = new ClaudeCodeHarness().BuildInvocation(new AgentTask { Goal = "g", Harness = ClaudeCodeHarness.HarnessKind, WorkspaceDirectory = _workspace, WorkspaceRepositoryDirectories = [_workspace] });
        var pointers = spec.ConfigHomeFiles.Where(file => file.RelativePath.StartsWith(ClaudeWorkspaceMemory.PointerRulePrefix, StringComparison.Ordinal)).ToList();

        pointers.Count.ShouldBe(ClaudeWorkspaceMemory.MaxInPlaceDirectories + 1, "fixture check: one pointer per directory past the budget");
        pointers.ShouldAllBe(pointer => pointer.Content.Length <= ClaudeWorkspaceMemory.MaxPointerBytes);
        pointers.Sum(pointer => pointer.Content.Length).ShouldBeLessThanOrEqualTo(ClaudeWorkspaceMemory.MaxPointerTotalBytes);
        NativeLaunchProtocol.FitsTheFrame(spec).ShouldBeTrue();
    }

    [Fact]
    public void All_pointers_together_stay_within_their_byte_bound_and_say_where_they_stopped()
    {
        if (OperatingSystem.IsWindows()) return;

        var globs = string.Join(", ", Enumerable.Range(0, 40).Select(i => $"\"src/area-{i:00}/**/*.ts\""));

        for (var i = 0; i < 120; i++) _tree.File($"ws/.claude/rules/r{i:000}.md", $"---\npaths: [{globs}]\n---\nRule.\n");

        var plan = Plan();
        var notice = plan.Notices.ShouldHaveSingleItem();

        plan.Pointers.Sum(pointer => pointer.Content.Length).ShouldBeLessThanOrEqualTo(ClaudeWorkspaceMemory.MaxPointerTotalBytes);
        plan.Pointers.Count.ShouldBeLessThan(120, "fixture check: the pointers together run past the bound");
        notice.ShouldBe($"Left the repository rules and nested memory past the first {plan.Pointers.Count} pointers out of this run: together they would run past {ClaudeWorkspaceMemory.MaxPointerTotalBytes} bytes.");
    }

    [Fact]
    public void A_memory_file_whose_path_no_pointer_may_carry_is_left_out_of_its_directorys_pointer_alone()
    {
        // The directory's own CLAUDE.md stays named: one unsafe name refused the whole pointer, blaming the directory.
        if (OperatingSystem.IsWindows()) return;

        PlantOverBudget();
        _tree.File("ws/pkg-05/.claude/rules/team notes.md", "Team notes.\n");
        _tree.File("ws/pkg-05/.claude/rules/my style.md", "Style.\n");

        var plan = Plan();

        plan.Pointers.Count.ShouldBe(ClaudeWorkspaceMemory.MaxInPlaceDirectories + 1);
        plan.Pointers[5].Content.ShouldContain($"were not preloaded: `{At("pkg-05/CLAUDE.md")}`. Read them now", Case.Sensitive);
        plan.Notices.ShouldBe(new[] { OverBudgetNotice, "Left the memory file 'pkg-05/.claude/rules/my style.md' and 1 more like it out of the pointer to 'pkg-05': its path holds a character other than a letter, a digit or one of . _ + - /." });
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void At_a_primary_repository_cwd_a_rule_linked_into_the_sibling_gets_no_pointer(bool intoSibling, bool pointed)
    {
        // The CLI skips a rules entry that links outside its cwd, so it never attached the rule; neither may a pointer.
        if (OperatingSystem.IsWindows()) return;

        var (cwd, sibling) = (At("primary"), At("sibling"));

        _tree.File("ws/sibling/shared-rules/ts.md", "---\npaths: \"*.ts\"\n---\nA sibling's rule.\n");
        _tree.File("ws/primary/shared-rules/ts.md", "---\npaths: \"*.ts\"\n---\nThe cwd's own rule.\n");
        _tree.Link("ws/primary/.claude/rules/ts.md", intoSibling ? "../../../sibling/shared-rules/ts.md" : "../../shared-rules/ts.md");

        var plan = ClaudeWorkspaceMemory.For(new AgentTask { Goal = "g", Harness = ClaudeCodeHarness.HarnessKind, WorkspaceDirectory = cwd, WorkspaceRepositoryDirectories = [cwd, sibling] });

        plan.Pointers.Count.ShouldBe(pointed ? 1 : 0);
        plan.Notices.ShouldBeEmpty();
    }

    [Fact]
    public void A_scoped_rule_whose_frontmatter_runs_past_the_head_gets_its_pointer()
    {
        // Classified from its first 4 KiB alone, the rule read as unconditional and got no pointer, and nothing said so.
        if (OperatingSystem.IsWindows()) return;

        var globs = Enumerable.Range(0, 120).Select(i => $"d{i:000}/*.md").Append("src/**/*.ts").ToList();

        _tree.File("ws/pkg/.claude/rules/big.md", $"---\ndescription: {new string('d', 2500)}\npaths:\n{string.Concat(globs.Select(glob => $"  - \"{glob}\"\n"))}---\nBig.\n");

        File.ReadAllText(At("pkg/.claude/rules/big.md")).LastIndexOf("---\n", StringComparison.Ordinal).ShouldBeGreaterThan(ClaudeRuleScope.MaxHeadBytes, "fixture check: the fence closes past the head");

        var plan = Plan();

        plan.Directories.ShouldBe(new[] { _workspace }, "a scoped rule loads nothing in place");
        Globs(plan.Pointers.ShouldHaveSingleItem()).ShouldBe(globs.Select(glob => glob.StartsWith("src/", StringComparison.Ordinal) ? "/pkg/src/**/*.ts" : $"/pkg/{glob}"));
        plan.Notices.ShouldBeEmpty();
    }

    [Fact]
    public void A_glob_that_would_close_its_pointers_frontmatter_gets_no_pointer_and_is_named()
    {
        // Brace expansion makes ---x from {-,b}--x: written into a pointer, the CLI would close its frontmatter there and
        // read the pointer with other globs than the runner wrote.
        if (OperatingSystem.IsWindows()) return;

        _tree.File("ws/.claude/rules/x.md", "---\npaths: \"{-,b}--x\"\n---\nR.\n");

        var plan = Plan();

        ClaudeRuleScope.Read(File.ReadAllText(At(".claude/rules/x.md"))).ShouldBe(new[] { "---x", "b--x" }, "fixture check: the CLI scopes the rule to both globs");
        plan.Pointers.ShouldBeEmpty();
        plan.Notices.ShouldBe(new[] { "Left the rule '.claude/rules/x.md' out of this run: the CLI would read its pointer with other globs than the runner wrote." });
    }

    [Theory]
    [InlineData("each directory's own links into a long chain")]
    [InlineData("one shared file of links into a long chain")]
    public async Task Checking_every_directory_a_pointer_points_into_stays_within_the_build_budget(string shape)
    {
        // Each pointed directory was checked against a budget of its own: 128 small directories whose memory climbs a long
        // chain of links held the build for minutes. Shared, the chain is walked once; apart, the budget runs out.
        if (OperatingSystem.IsWindows()) return;

        var shared = shape.StartsWith("one", StringComparison.Ordinal);

        PlantChain();

        for (var i = 0; i < 20; i++) _tree.Link($"ws/.y/i{i:000}", "../.x/L0");

        for (var directory = 0; directory < ClaudeWorkspaceMemory.MaxPointerRules; directory++)
        {
            if (!shared) for (var i = 0; i < 20; i++) _tree.Link($"ws/.y/d{directory:000}/i{i:000}", "../../.x/L0");

            _tree.File($"ws/d{directory:000}/CLAUDE.md", string.Concat(Enumerable.Range(0, 20).Select(i => shared ? $"@../.y/i{i:000} " : $"@../.y/d{directory:000}/i{i:000} ")));
        }

        var plan = await Task.Run(Plan).WaitAsync(TimeSpan.FromSeconds(30));

        plan.Pointers.Count.ShouldBe(shared ? ClaudeWorkspaceMemory.MaxPointerRules : plan.Pointers.Count, shape);
        plan.Notices.Contains(BudgetNotice).ShouldBe(!shared, shape);
    }

    [Fact]
    public void The_pointer_bounds_and_file_names_are_pinned()
    {
        // Committed values, changed by PR: how many pointers one run carries, how large, and where in its config home.
        ClaudeWorkspaceMemory.MaxPointerRules.ShouldBe(128);
        ClaudeWorkspaceMemory.MaxPointerBytes.ShouldBe(4096);
        ClaudeWorkspaceMemory.MaxPointerTotalBytes.ShouldBe(131072);
        ClaudeWorkspaceMemory.PointerRulePrefix.ShouldBe("rules/codespace-repository-");
    }

    /// <summary>The CLI's own import grammar's start: an @ at the start of a line, after whitespace (JavaScript's) or after CJK punctuation.</summary>
    [GeneratedRegex("(^|[\\s 　﻿。、？！])@")]
    private static partial Regex MentionStart();

    private ClaudeWorkspaceMemory.Plan Plan() =>
        ClaudeWorkspaceMemory.For(new AgentTask { Goal = "g", Harness = ClaudeCodeHarness.HarnessKind, WorkspaceDirectory = _workspace, WorkspaceRepositoryDirectories = [_workspace] });

    /// <summary>The path at <paramref name="relative"/> below the workspace, as the workspace spells it.</summary>
    private string At(string relative) => Path.Combine(_workspace, relative);

    /// <summary>The globs the CLI reads from a pointer.</summary>
    private static string[] Globs(ConfigHomeFile pointer) => ClaudeRuleScope.Read(pointer.Content).ShouldNotBeNull($"{pointer.RelativePath} must be scoped").ToArray();

    /// <summary>A rule pointer as its globs and the rule it names, relative to the workspace.</summary>
    private string Described(ConfigHomeFile pointer) => $"{string.Join(',', Globs(pointer))} -> {Path.GetRelativePath(_workspace, Named(pointer))}";

    /// <summary>One more nested directory than loads in place, <c>pkg-00</c> to <c>pkg-16</c>, each with a <c>CLAUDE.md</c>.</summary>
    private void PlantOverBudget()
    {
        for (var i = 0; i <= ClaudeWorkspaceMemory.MaxInPlaceDirectories; i++) _tree.File($"ws/pkg-{i:00}/CLAUDE.md", "Package.\n");
    }

    /// <summary>A chain of 39 links, <c>ws/.x/L0</c> on, each target climbing <c>d/../</c> 150 times before it names the next link; the last names <c>ws/.x/real.md</c>.</summary>
    private void PlantChain()
    {
        var padding = string.Concat(Enumerable.Repeat("d/../", 150));

        _tree.Directory("ws/.x/d");
        _tree.File("ws/.x/real.md", "Real.\n");

        for (var hop = 38; hop >= 0; hop--) _tree.Link($"ws/.x/L{hop}", padding + (hop == 38 ? "real.md" : $"L{hop + 1}"));
    }

    /// <summary>Whether the pointer names a scoped rule rather than a directory's memory.</summary>
    private static bool IsRulePointer(ConfigHomeFile pointer) => pointer.Content.Contains("The repository rule `", StringComparison.Ordinal);

    /// <summary>The rule a rule pointer names.</summary>
    private static string Named(ConfigHomeFile pointer) => Regex.Match(pointer.Content, "The repository rule `([^`]+)`").Groups[1].Value;

    /// <summary>The keys of a pointer's frontmatter mapping.</summary>
    private static IEnumerable<string> FrontmatterKeys(ConfigHomeFile pointer)
    {
        var yaml = new YamlStream();
        yaml.Load(new StringReader(pointer.Content.Split("---\n")[1]));
        return ((YamlMappingNode)yaml.Documents[0].RootNode).Children.Keys.Select(key => ((YamlScalarNode)key).Value!);
    }

    private (string Cwd, string[] Repositories, string[] Expected) PlantLayout(string shape)
    {
        switch (shape)
        {
            case "single-repo":
                _tree.File("ws/.claude/rules/ts.md", "---\npaths: \"*.ts\"\n---\nTypes.\n");
                _tree.File("ws/pkg/.claude/rules/md.md", "---\npaths: [\"docs/*.md\", \"/top/**\"]\n---\nDocs.\n");
                return (_workspace, [_workspace], ["*.ts -> .claude/rules/ts.md", "/pkg/docs/*.md,/pkg/top -> pkg/.claude/rules/md.md"]);
            case "multi-repo at its root":
                _tree.File("ws/r2/.claude/rules/ts.md", "---\npaths: \"*.ts\"\n---\nTypes.\n");
                _tree.File("ws/r1/.claude/rules/ts.md", "---\npaths: \"*.ts\"\n---\nTypes.\n");
                _tree.File("ws/r1/sub/.claude/rules/x.md", "---\npaths: \"/x/**\"\n---\nX.\n");
                return (_workspace, [At("r1"), At("r2")], ["/r1/**/*.ts -> r1/.claude/rules/ts.md", "/r2/**/*.ts -> r2/.claude/rules/ts.md", "/r1/sub/x -> r1/sub/.claude/rules/x.md"]);
            case "multi-repo at its primary repository":
                _tree.File("ws/repo-a/.claude/rules/ts.md", "---\npaths: \"*.ts\"\n---\nTypes.\n");
                _tree.File("ws/repo-a/lib/.claude/rules/ts.md", "---\npaths: \"*.ts\"\n---\nTypes.\n");
                _tree.File("ws/repo-b/.claude/rules/ts.md", "---\npaths: \"*.ts\"\n---\nA sibling the cwd does not hold.\n");
                return (At("repo-a"), [At("repo-a"), At("repo-b")], ["*.ts -> repo-a/.claude/rules/ts.md", "/lib/**/*.ts -> repo-a/lib/.claude/rules/ts.md"]);
            default:
                throw new ArgumentOutOfRangeException(nameof(shape), shape, null);
        }
    }
}
