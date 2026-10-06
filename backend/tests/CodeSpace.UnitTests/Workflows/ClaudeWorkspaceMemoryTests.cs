using System.Diagnostics;
using CodeSpace.Core.Services.Agents.Harnesses;
using CodeSpace.Core.Services.Agents.Harnesses.Claude;
using CodeSpace.Messages.Agents;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// Pins the guard that keeps a Claude run from adding a directory whose memory reaches outside the workspace
/// (<see cref="ClaudeWorkspaceMemory"/>). The pinned CLI follows a symlink at an added directory's <c>CLAUDE.md</c>,
/// <c>.claude/CLAUDE.md</c> or <c>.claude</c> wherever it leads, and the guard treats a rules entry or an import that
/// leads out the same way, for a later CLI that follows those too. Each case lays out a real workspace and a real file
/// outside it under a GUID temp root, links them, and asserts the directory is left out with the notice the run's
/// timeline will show — or, for a link that stays inside or dangles, or a memory that is only large or wide, that it is
/// still added and nothing is said. The workspace is spelled through <see cref="TempTree"/>'s root link, so on every
/// host each case also compares a workspace reached through a symlink.
/// </summary>
[Trait("Category", "Unit")]
public sealed class ClaudeWorkspaceMemoryTests : IDisposable
{
    private readonly TempTree _tree = new();
    private readonly string _workspace;
    private readonly string _secret;

    public ClaudeWorkspaceMemoryTests()
    {
        _workspace = _tree.Directory("ws");
        _secret = _tree.File("outside/secret.md", "OUTSIDE");
    }

    public void Dispose() => _tree.Dispose();

    [Theory]
    [InlineData("a CLAUDE.md linked outside", "CLAUDE.md resolves outside the workspace")]
    [InlineData("a .claude/CLAUDE.md linked outside by a relative target", ".claude/CLAUDE.md resolves outside the workspace")]
    [InlineData("a .claude directory linked outside", ".claude resolves outside the workspace")]
    [InlineData("a .claude directory linked to an outside directory that holds nothing yet", ".claude resolves outside the workspace")]
    [InlineData("a chain of links that ends outside", "CLAUDE.md resolves outside the workspace")]
    [InlineData("a rule linked outside", ".claude/rules/style.md resolves outside the workspace")]
    [InlineData("a rules folder linked outside", ".claude/rules/team resolves outside the workspace")]
    [InlineData("a link whose .. climbs out of a linked directory", "CLAUDE.md resolves outside the workspace")]
    [InlineData("an import through an in-workspace link", "a file CLAUDE.md imports resolves outside the workspace")]
    [InlineData("an import of an outside file by its absolute path", "a file CLAUDE.md imports resolves outside the workspace")]
    [InlineData("an import that climbs out with ..", "a file .claude/CLAUDE.md imports resolves outside the workspace")]
    [InlineData("an import inside a code block", "a file CLAUDE.md imports resolves outside the workspace")]
    [InlineData("an import a scoped rule makes", "a file .claude/rules/scoped.md imports resolves outside the workspace")]
    [InlineData("an import with an escaped space", "a file CLAUDE.md imports resolves outside the workspace")]
    [InlineData("an import with a fragment", "a file CLAUDE.md imports resolves outside the workspace")]
    public void A_directory_whose_memory_reaches_outside_the_workspace_is_left_out(string shape, string escape)
    {
        if (OperatingSystem.IsWindows()) return;

        PlantEscape(shape);

        var plan = Plan();

        plan.Directories.ShouldBeEmpty($"{shape}: the CLI would read the outside file through this directory");
        plan.Notices.ShouldBe(new[] { $"Left the memory in the workspace out of this run: {escape}." }, $"{shape}: one notice naming what reached outside");
    }

    [Theory]
    [InlineData("no memory at all")]
    [InlineData("a CLAUDE.md linked to the AGENTS.md beside it")]
    [InlineData("a .claude directory linked to another inside the workspace")]
    [InlineData("a CLAUDE.md linked to nothing")]
    [InlineData("a CLAUDE.md linked to itself")]
    [InlineData("imports of files that do not exist")]
    [InlineData("imports naming directories")]
    [InlineData("an import of the run's home")]
    [InlineData("addresses and handles")]
    [InlineData("a file in the rules folder that is not markdown")]
    [InlineData("an image in the rules folder linked outside")]
    [InlineData("70 rules scoped by paths:")]
    [InlineData("5 rules beside 60 images")]
    [InlineData("a 200 KiB CLAUDE.md")]
    [InlineData("a CLAUDE.md importing a 200 KiB README")]
    [InlineData("1100 email addresses")]
    public void A_directory_whose_memory_stays_inside_the_workspace_or_reaches_nothing_is_still_added(string shape)
    {
        if (OperatingSystem.IsWindows()) return;

        PlantKept(shape);

        var plan = Plan();

        plan.Directories.ShouldBe(new[] { _workspace }, $"{shape}: nothing outside the workspace can load through this directory");
        plan.Notices.ShouldBeEmpty(shape);
    }

    [Fact]
    public void Only_the_repository_whose_memory_reaches_outside_is_left_out_and_the_rest_keep_their_order()
    {
        if (OperatingSystem.IsWindows()) return;

        var a = _tree.Directory("ws/a");
        var b = _tree.Directory("ws/b");
        var c = _tree.Directory("ws/c");

        _tree.File("ws/a/CLAUDE.md", "A");
        _tree.Link("ws/b/CLAUDE.md", _secret);
        _tree.File("ws/c/CLAUDE.md", "C");

        var plan = Plan(a, b, c);

        plan.Directories.ShouldBe(new[] { _workspace, a, c });
        plan.Notices.ShouldBe(new[] { "Left the memory in 'b' out of this run: CLAUDE.md resolves outside the workspace." });
    }

    [Theory]
    [InlineData(5, true)]    // the link is the fifth import down: the guard still resolves it
    [InlineData(6, false)]   // one further: the CLI reads no file five imports down, so it never reaches the link
    public void Imports_are_followed_five_hops_deep(int linkHop, bool leftOut)
    {
        if (OperatingSystem.IsWindows()) return;

        for (var hop = 0; hop < linkHop; hop++) _tree.File($"ws/{Hop(hop)}", $"Next: @{Hop(hop + 1)}\n");

        _tree.Link($"ws/{Hop(linkHop)}", _secret);

        Plan().Directories.ShouldBe(leftOut ? Array.Empty<string>() : new[] { _workspace });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_memory_naming_more_paths_than_the_lookup_bound_is_left_out_unchecked(bool overTheBound)
    {
        // The four places the CLI reads memory from are paths to resolve too, so CLAUDE.md names four fewer than the bound.
        if (OperatingSystem.IsWindows()) return;

        var imports = ClaudeWorkspaceMemory.MaxLookups - 4 + (overTheBound ? 1 : 0);

        _tree.File("ws/CLAUDE.md", string.Join(' ', Enumerable.Range(0, imports).Select(i => $"@missing-{i}.md")));

        var plan = Plan();

        plan.Directories.ShouldBe(overTheBound ? Array.Empty<string>() : new[] { _workspace });
        plan.Notices.ShouldBe(overTheBound ? new[] { $"Left the memory in the workspace out of this run: its memory names more than {ClaudeWorkspaceMemory.MaxLookups} paths to check." } : Array.Empty<string>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_memory_larger_than_the_scan_bound_is_left_out_unchecked(bool overTheBound)
    {
        // The bound is on the memory, not on one file: two files, each under it, that pass it together.
        if (OperatingSystem.IsWindows()) return;

        var half = ClaudeWorkspaceMemory.MaxScannedBytes / 2;

        _tree.File("ws/CLAUDE.md", new string('x', half));
        _tree.File("ws/.claude/CLAUDE.md", new string('y', ClaudeWorkspaceMemory.MaxScannedBytes - half + (overTheBound ? 1 : 0)));

        var plan = Plan();

        plan.Directories.ShouldBe(overTheBound ? Array.Empty<string>() : new[] { _workspace });
        plan.Notices.ShouldBe(overTheBound ? new[] { $"Left the memory in the workspace out of this run: its memory spans more than {ClaudeWorkspaceMemory.MaxScannedBytes} bytes to check." } : Array.Empty<string>());
    }

    [Theory]
    [InlineData(true)]    // the rule links into a sibling repository the same workspace holds: the run's own clone
    [InlineData(false)]   // the same link, to a file outside the workspace root
    public void A_primary_repository_cwd_may_link_its_memory_into_a_sibling_repository(bool intoSibling)
    {
        // A multi-repo run whose cwd is its primary repository: the other repositories sit beside the cwd, inside the
        // workspace root, and are not added — but memory linking into them brings in nothing from outside the run.
        if (OperatingSystem.IsWindows()) return;

        var primary = _tree.Directory("ws/repo-a");
        var sibling = _tree.Directory("ws/repo-b");

        _tree.File("ws/repo-b/.claude/rules/shared.md", "Shared rule.\n");
        _tree.Link("ws/repo-a/.claude/rules/shared.md", intoSibling ? "../../../repo-b/.claude/rules/shared.md" : _secret);

        var plan = ClaudeWorkspaceMemory.For(new AgentTask { Goal = "g", Harness = ClaudeCodeHarness.HarnessKind, WorkspaceDirectory = primary, WorkspaceRepositoryDirectories = [primary, sibling] });

        plan.Directories.ShouldBe(intoSibling ? new[] { primary } : Array.Empty<string>(), "only the cwd is added either way; the sibling is the run's own, the outside file is not");
        plan.Notices.ShouldBe(intoSibling ? Array.Empty<string>() : new[] { "Left the memory in the workspace out of this run: .claude/rules/shared.md resolves outside the workspace." });
    }

    [Fact]
    public void A_workspace_reached_through_a_link_that_climbs_out_of_another_link_is_checked_where_it_really_is()
    {
        // data → srv/link/../disk with srv/link → x/y: the kernel climbs from where srv/link really is, so data is x/disk.
        // Resolving the workspace and its memory with two different readings of that link threw before the launch.
        if (OperatingSystem.IsWindows()) return;

        _tree.Directory("x/y");
        _tree.File("x/disk/ws/CLAUDE.md", "Memory.\n");
        _tree.Link("srv/link", Path.Combine(_tree.Root, "x", "y"));
        _tree.Link("data", Path.Combine(_tree.Root, "srv", "link", "..", "disk"));

        var workspace = Path.Combine(_tree.Root, "data", "ws");
        var plan = ClaudeWorkspaceMemory.For(new AgentTask { Goal = "g", Harness = ClaudeCodeHarness.HarnessKind, WorkspaceDirectory = workspace, WorkspaceRepositoryDirectories = [workspace] });

        plan.Directories.ShouldBe(new[] { workspace });
        plan.Notices.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_fifo_in_the_memory_is_never_opened_for_reading()
    {
        // A blocking open of a FIFO waits for a writer that never comes, and the build would wait with it. The CLI reads
        // no FIFO either, so the directory is added as if the file were absent.
        if (OperatingSystem.IsWindows()) return;

        await MakeFifoAsync(Path.Combine(_workspace, "CLAUDE.md"));
        Directory.CreateDirectory(Path.Combine(_workspace, ".claude", "rules"));
        await MakeFifoAsync(Path.Combine(_workspace, ".claude", "rules", "pipe.md"));

        var plan = await System.Threading.Tasks.Task.Run(Plan).WaitAsync(TimeSpan.FromSeconds(1));

        plan.Directories.ShouldBe(new[] { _workspace });
        plan.Notices.ShouldBeEmpty();
    }

    [Fact]
    public void A_name_the_repository_chose_reaches_the_notice_without_control_characters()
    {
        if (OperatingSystem.IsWindows()) return;

        _tree.Link("ws/.claude/rules/evil\nname.md", _secret);

        Plan().Notices.ShouldBe(new[] { "Left the memory in the workspace out of this run: .claude/rules/evil?name.md resolves outside the workspace." });
    }

    [Fact]
    public void A_name_the_repository_chose_is_cut_in_the_notice()
    {
        if (OperatingSystem.IsWindows()) return;

        var name = new string('n', 150) + ".md";
        var origin = $".claude/rules/{name}";

        _tree.Link($"ws/.claude/rules/{name}", _secret);

        Plan().Notices.ShouldBe(new[] { $"Left the memory in the workspace out of this run: {origin[..ClaudeWorkspaceMemory.MaxNoticeNameLength]}… resolves outside the workspace." });
    }

    [Fact]
    public void A_run_with_no_workspace_adds_nothing_and_says_nothing()
    {
        var plan = ClaudeWorkspaceMemory.For(new AgentTask { Goal = "g", Harness = ClaudeCodeHarness.HarnessKind });

        plan.Directories.ShouldBeEmpty();
        plan.Notices.ShouldBeEmpty();
    }

    [Fact]
    public void The_bounds_are_pinned()
    {
        // Committed values, changed by PR: each one decides how much of a repository's memory the guard reads before it
        // gives up and leaves the directory out.
        ClaudeWorkspaceMemory.MaxImportHops.ShouldBe(5, "the CLI's own import depth (wgs=5 in 2.1.263)");
        ClaudeWorkspaceMemory.MaxLookups.ShouldBe(16384);
        ClaudeWorkspaceMemory.MaxScannedBytes.ShouldBe(4194304);
        ClaudeWorkspaceMemory.MaxNoticeNameLength.ShouldBe(120);
        PhysicalPath.MaxLinkHops.ShouldBe(40, "the kernel's own MAXSYMLINKS");
    }

    private ClaudeWorkspaceMemory.Plan Plan() => Plan(_workspace);

    private ClaudeWorkspaceMemory.Plan Plan(params string[] repositories) =>
        ClaudeWorkspaceMemory.For(new AgentTask { Goal = "g", Harness = ClaudeCodeHarness.HarnessKind, WorkspaceDirectory = _workspace, WorkspaceRepositoryDirectories = repositories });

    private static string Hop(int hop) => hop == 0 ? "CLAUDE.md" : $"hop-{hop}.md";

    private string Outside(string relative) => Path.Combine(_tree.Root, "outside", relative);

    private void PlantEscape(string shape)
    {
        switch (shape)
        {
            case "a CLAUDE.md linked outside":
                _tree.Link("ws/CLAUDE.md", _secret);
                break;
            case "a .claude/CLAUDE.md linked outside by a relative target":
                _tree.Link("ws/.claude/CLAUDE.md", "../../outside/secret.md");
                break;
            case "a .claude directory linked outside":
                _tree.File("outside/dot/CLAUDE.md", "OUTSIDE");
                _tree.Link("ws/.claude", Outside("dot"));
                break;
            case "a .claude directory linked to an outside directory that holds nothing yet":
                _tree.Directory("outside/empty");
                _tree.Link("ws/.claude", Outside("empty"));
                break;
            case "a chain of links that ends outside":
                _tree.Link("ws/docs/real.md", _secret);
                _tree.Link("ws/AGENTS.md", "docs/real.md");
                _tree.Link("ws/CLAUDE.md", "AGENTS.md");
                break;
            case "a rule linked outside":
                _tree.Link("ws/.claude/rules/style.md", _secret);
                break;
            case "a rules folder linked outside":
                _tree.File("outside/rules/a.md", "OUTSIDE");
                _tree.Link("ws/.claude/rules/team", Outside("rules"));
                break;
            case "a link whose .. climbs out of a linked directory":
                // deep → outside/p/q, so deep/../x.md is outside/p/x.md — not the x.md beside deep, which exists too.
                _tree.Directory("outside/p/q");
                _tree.File("outside/p/x.md", "OUTSIDE");
                _tree.File("ws/x.md", "INSIDE");
                _tree.Link("ws/deep", Outside("p/q"));
                _tree.Link("ws/CLAUDE.md", "deep/../x.md");
                break;
            case "an import through an in-workspace link":
                _tree.File("ws/CLAUDE.md", "Read @docs/guide.md first.\n");
                _tree.File("ws/docs/guide.md", "Then read @notes.md\n");
                _tree.Link("ws/docs/notes.md", _secret);
                break;
            case "an import of an outside file by its absolute path":
                _tree.File("ws/CLAUDE.md", $"@{_secret}\n");
                break;
            case "an import that climbs out with ..":
                _tree.File("ws/.claude/CLAUDE.md", "@../../outside/secret.md\n");
                break;
            case "an import inside a code block":
                _tree.File("ws/CLAUDE.md", "```\n@docs/n.md\n```\n");
                _tree.Link("ws/docs/n.md", _secret);
                break;
            case "an import a scoped rule makes":
                _tree.File("ws/.claude/rules/scoped.md", "---\npaths:\n  - \"src/**\"\n---\nSee @../../docs/x.md\n");
                _tree.Link("ws/docs/x.md", _secret);
                break;
            case "an import with an escaped space":
                _tree.File("ws/CLAUDE.md", "@my\\ notes.md\n");
                _tree.Link("ws/my notes.md", _secret);
                break;
            case "an import with a fragment":
                _tree.File("ws/CLAUDE.md", "@notes.md#setup\n");
                _tree.Link("ws/notes.md", _secret);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(shape), shape, null);
        }
    }

    private void PlantKept(string shape)
    {
        switch (shape)
        {
            case "no memory at all":
                break;
            case "a CLAUDE.md linked to the AGENTS.md beside it":
                _tree.File("ws/AGENTS.md", "Follow @docs/style.md.\n");
                _tree.File("ws/docs/style.md", "Tabs.\n");
                _tree.Link("ws/CLAUDE.md", "AGENTS.md");
                break;
            case "a .claude directory linked to another inside the workspace":
                // A dot-directory, which the nested walk never enters: a shared/CLAUDE.md would be nested memory of its own.
                _tree.File("ws/.shared/CLAUDE.md", "Shared.\n");
                _tree.File("ws/.shared/rules/r.md", "Rule.\n");
                _tree.Link("ws/.claude", Path.Combine(_workspace, ".shared"));
                break;
            case "a CLAUDE.md linked to nothing":
                _tree.Link("ws/CLAUDE.md", Outside("missing.md"));
                break;
            case "a CLAUDE.md linked to itself":
                _tree.Link("ws/CLAUDE.md", "CLAUDE.md");
                break;
            case "imports of files that do not exist":
                _tree.File("ws/CLAUDE.md", "@nowhere.md and @also/missing.md\n");
                break;
            case "imports naming directories":
                _tree.Directory("ws/docs");
                _tree.File("ws/CLAUDE.md", "@/ @.. @../ @docs\n");
                break;
            case "an import of the run's home":
                _tree.File("ws/CLAUDE.md", "@~/.mcp.json\n");
                break;
            case "addresses and handles":
                _tree.File("ws/CLAUDE.md", "Mail ops@example.com or ping @platform-team.\n");
                break;
            case "a file in the rules folder that is not markdown":
                _tree.File("ws/.claude/rules/notes.txt", "@../../../outside/secret.md\n");
                break;
            case "an image in the rules folder linked outside":
                _tree.File("ws/.claude/rules/style.md", "Rule.\n");
                _tree.Link("ws/.claude/rules/diagram.png", _secret);
                break;
            case "70 rules scoped by paths:":
                for (var i = 0; i < 70; i++) _tree.File($"ws/.claude/rules/rule-{i}.md", $"---\npaths:\n  - \"src/{i}/**\"\n---\nRule {i}.\n");
                break;
            case "5 rules beside 60 images":
                for (var i = 0; i < 5; i++) _tree.File($"ws/.claude/rules/rule-{i}.md", $"Rule {i}.\n");
                for (var i = 0; i < 60; i++) _tree.File($"ws/.claude/rules/img/figure-{i}.png", "PNG");
                break;
            case "a 200 KiB CLAUDE.md":
                _tree.File("ws/CLAUDE.md", Prose(200 * 1024));
                break;
            case "a CLAUDE.md importing a 200 KiB README":
                _tree.File("ws/CLAUDE.md", "Project overview: @README.md\n");
                _tree.File("ws/README.md", Prose(200 * 1024));
                break;
            case "1100 email addresses":
                _tree.File("ws/CLAUDE.md", string.Concat(Enumerable.Range(0, 1100).Select(i => $"owner{i}@corp{i}.example.com\n")));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(shape), shape, null);
        }
    }

    /// <summary>Ordinary text of about <paramref name="bytes"/> bytes, words and lines, no imports.</summary>
    private static string Prose(int bytes) => string.Concat(Enumerable.Repeat("Keep each change small and tested.\n", bytes / 35 + 1));

    private static async Task MakeFifoAsync(string path)
    {
        using var mkfifo = Process.Start("mkfifo", path)!;
        await mkfifo.WaitForExitAsync();
        mkfifo.ExitCode.ShouldBe(0, $"fixture check: mkfifo {path}");
    }
}
