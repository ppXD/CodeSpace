using System.Diagnostics;
using System.Text;
using CodeSpace.Core.Services.Agents.Harnesses.Codex;
using CodeSpace.Messages.Agents;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// Pins what a multi-repo Codex run is given of each repository's own <c>AGENTS.md</c> (<see cref="CodexRepositoryGuides"/>).
/// Codex reads a project doc only from its cwd and above, and a multi-repo run's cwd is the workspace root, so the
/// harness appends each repository's doc to the run's own <c>CODEX_HOME/AGENTS.md</c>: the one Codex itself would pick
/// there, cut where Codex cuts a project doc, under a separator naming it. Each case lays out a real workspace under a
/// GUID temp root spelled through <see cref="TempTree"/>'s root link, so every host also compares a workspace reached
/// through a symlink.
/// </summary>
[Trait("Category", "Unit")]
public sealed class CodexRepositoryGuidesTests : IDisposable
{
    private readonly TempTree _tree = new();
    private readonly string _workspace;
    private readonly string _secret;

    public CodexRepositoryGuidesTests()
    {
        _workspace = _tree.Directory("ws");
        _secret = _tree.File("outside/secret.md", "OUTSIDE");
    }

    public void Dispose() => _tree.Dispose();

    [Fact]
    public void MaxProjectDocBytes_is_pinned()
    {
        // Codex's own project_doc_max_bytes default: 0.142.2 hands the model the first 32768 bytes of a project doc and
        // no more. If a Codex bump moves it, this moves by PR with the CLI.
        CodexRepositoryGuides.MaxProjectDocBytes.ShouldBe(32 * 1024);
    }

    [Theory]
    [InlineData("a single repository, which is the cwd itself")]
    [InlineData("a cwd at the primary repository, its sibling beside it")]
    [InlineData("a scratch directory, which holds no repository")]
    [InlineData("a task that names its own workspace and no repositories")]
    [InlineData("a directory that only shares the cwd's prefix")]
    public void A_run_whose_cwd_has_no_repository_strictly_inside_it_gets_no_appendix(string shape)
    {
        if (OperatingSystem.IsWindows()) return;

        _tree.File("ws/AGENTS.md", "ROOT");
        _tree.File("ws/a/AGENTS.md", "A");
        _tree.File("ws/b/AGENTS.md", "B");
        _tree.File("ws-other/AGENTS.md", "OTHER");

        var guides = shape switch
        {
            "a single repository, which is the cwd itself" => Guides(_workspace, _workspace),
            "a cwd at the primary repository, its sibling beside it" => Guides(Path.Combine(_workspace, "a"), Path.Combine(_workspace, "a"), Path.Combine(_workspace, "b")),
            "a scratch directory, which holds no repository" => Guides(_workspace, Array.Empty<string>()),
            "a task that names its own workspace and no repositories" => CodexRepositoryGuides.For(Task(_workspace, null)),
            _ => Guides(_workspace, _workspace + "-other"),
        };

        guides.Appendix.ShouldBeEmpty($"{shape}: Codex reads the cwd's own doc itself, and no repository below the cwd is hidden from it");
        guides.Notices.ShouldBeEmpty(shape);
    }

    [Fact]
    public void Each_repository_below_the_cwd_gives_its_doc_under_a_separator_naming_it_in_the_order_the_executor_names_them()
    {
        if (OperatingSystem.IsWindows()) return;

        _tree.File("ws/repo-a/AGENTS.md", "Keep a small.\n");
        _tree.File("ws/repo-b/AGENTS.md", "Keep b typed.\n");
        _tree.Directory("ws/repo-c");

        var guides = Guides(_workspace, Repo("repo-b"), Repo("repo-c"), Repo("repo-a"));

        guides.Appendix.ShouldBe("\n\n--- project-doc (repo-b/AGENTS.md) ---\n\nKeep b typed.\n\n\n--- project-doc (repo-a/AGENTS.md) ---\n\nKeep a small.\n", customMessage: "a repository without a doc gives nothing, and the rest keep the executor's order");
        guides.Notices.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("an override beside the doc", "OVERRIDE", "AGENTS.override.md")]
    [InlineData("an empty override beside the doc", null, null)]
    [InlineData("an override of only whitespace beside the doc", null, null)]
    [InlineData("an override that is a directory beside the doc", "PLAIN", "AGENTS.md")]
    [InlineData("an override linked to the doc beside it", "PLAIN", "AGENTS.override.md")]
    public void The_doc_is_the_one_codex_picks_an_override_first_whatever_it_holds(string shape, string? text, string? name)
    {
        // Codex 0.142.2 picks AGENTS.override.md whenever it is a file or a link, and gives nothing for one that holds
        // nothing, even with an AGENTS.md beside it; a directory of that name it passes over.
        if (OperatingSystem.IsWindows()) return;

        _tree.File("ws/repo-a/AGENTS.md", "PLAIN");

        switch (shape)
        {
            case "an override beside the doc": _tree.File("ws/repo-a/AGENTS.override.md", "OVERRIDE"); break;
            case "an empty override beside the doc": _tree.File("ws/repo-a/AGENTS.override.md", ""); break;
            case "an override of only whitespace beside the doc": _tree.File("ws/repo-a/AGENTS.override.md", "  \n\t\n"); break;
            case "an override that is a directory beside the doc": _tree.Directory("ws/repo-a/AGENTS.override.md"); break;
            default: _tree.Link("ws/repo-a/AGENTS.override.md", "AGENTS.md"); break;
        }

        var guides = Guides(_workspace, Repo("repo-a"));

        guides.Appendix.ShouldBe(text is null ? "" : $"\n\n--- project-doc (repo-a/{name}) ---\n\n{text}", customMessage: shape);
        guides.Notices.ShouldBeEmpty(shape);
    }

    [Theory]
    [InlineData(CodexRepositoryGuides.MaxProjectDocBytes, false)]
    [InlineData(CodexRepositoryGuides.MaxProjectDocBytes + 1, true)]
    public void A_doc_is_cut_where_codex_cuts_a_project_doc_and_the_cut_is_said(int length, bool cut)
    {
        if (OperatingSystem.IsWindows()) return;

        _tree.File("ws/repo-a/AGENTS.md", new string('x', CodexRepositoryGuides.MaxProjectDocBytes) + new string('T', length - CodexRepositoryGuides.MaxProjectDocBytes));

        var guides = Guides(_workspace, Repo("repo-a"));

        guides.Appendix.ShouldBe($"\n\n--- project-doc (repo-a/AGENTS.md) ---\n\n{new string('x', CodexRepositoryGuides.MaxProjectDocBytes)}", customMessage: "the run is given the doc's first bytes and nothing past them");
        guides.Notices.ShouldBe(cut ? new[] { $"Left all but the first {CodexRepositoryGuides.MaxProjectDocBytes} bytes of the AGENTS.md of 'repo-a' out of this run: Codex reads no more of a project doc." } : Array.Empty<string>());
    }

    [Fact]
    public void A_doc_cut_through_a_character_is_handed_over_as_codex_hands_it_over()
    {
        // Codex cuts the bytes and decodes them lossily, so a character split at the cap becomes U+FFFD.
        if (OperatingSystem.IsWindows()) return;

        var head = new string('x', CodexRepositoryGuides.MaxProjectDocBytes - 1);

        File.WriteAllBytes(Path.Combine(_tree.Directory("ws/repo-a"), "AGENTS.md"), Encoding.UTF8.GetBytes(head + "é"));

        Guides(_workspace, Repo("repo-a")).Appendix.ShouldBe($"\n\n--- project-doc (repo-a/AGENTS.md) ---\n\n{head}�");
    }

    [Theory]
    [InlineData("a doc linked outside by its absolute path", "AGENTS.md")]
    [InlineData("a doc linked outside by a relative target", "AGENTS.md")]
    [InlineData("a link whose .. climbs out of a linked directory", "AGENTS.md")]
    [InlineData("an override linked outside beside a doc inside", "AGENTS.override.md")]
    [InlineData("a repository directory an earlier round replaced with a link outside", "AGENTS.md")]
    public void A_doc_that_resolves_outside_the_workspace_is_left_out_and_said(string shape, string name)
    {
        if (OperatingSystem.IsWindows()) return;

        _tree.File("ws/repo-a/AGENTS.md", "A");
        _tree.Directory("ws/repo-b");

        switch (shape)
        {
            case "a doc linked outside by its absolute path": _tree.Link("ws/repo-b/AGENTS.md", _secret); break;
            case "a doc linked outside by a relative target": _tree.Link("ws/repo-b/AGENTS.md", "../../outside/secret.md"); break;
            case "a link whose .. climbs out of a linked directory":
                _tree.Directory("outside/deep/er");
                _tree.Link("ws/repo-b/deep", Path.Combine(_tree.Root, "outside", "deep", "er"));
                _tree.Link("ws/repo-b/AGENTS.md", "deep/../../secret.md");
                break;
            case "an override linked outside beside a doc inside":
                _tree.File("ws/repo-b/AGENTS.md", "B");
                _tree.Link("ws/repo-b/AGENTS.override.md", _secret);
                break;
            default:
                Directory.Delete(Path.Combine(_workspace, "repo-b"));
                _tree.File("outside/repo/AGENTS.md", "OUTSIDE");
                _tree.Link("ws/repo-b", Path.Combine(_tree.Root, "outside", "repo"));
                break;
        }

        var guides = Guides(_workspace, Repo("repo-a"), Repo("repo-b"));

        guides.Appendix.ShouldBe("\n\n--- project-doc (repo-a/AGENTS.md) ---\n\nA", customMessage: $"{shape}: only the repository whose doc stays inside is given");
        guides.Appendix.ShouldNotContain("OUTSIDE");
        guides.Notices.ShouldBe(new[] { $"Left the {name} of 'repo-b' out of this run: it resolves outside the workspace." }, shape);
    }

    [Theory]
    [InlineData("a doc linked to a file of another repository in the workspace", "SIBLING")]
    [InlineData("a doc linked to the README beside it", "README")]
    [InlineData("a doc linked to nothing", null)]
    [InlineData("a doc linked to a directory", null)]
    public void A_doc_linked_inside_the_workspace_is_given_and_one_that_reaches_no_file_gives_nothing(string shape, string? text)
    {
        if (OperatingSystem.IsWindows()) return;

        _tree.File("ws/repo-b/notes.md", "SIBLING");
        _tree.File("ws/repo-a/README.md", "README");

        switch (shape)
        {
            case "a doc linked to a file of another repository in the workspace": _tree.Link("ws/repo-a/AGENTS.md", "../repo-b/notes.md"); break;
            case "a doc linked to the README beside it": _tree.Link("ws/repo-a/AGENTS.md", "README.md"); break;
            case "a doc linked to nothing": _tree.Link("ws/repo-a/AGENTS.md", "missing.md"); break;
            default: _tree.Link("ws/repo-a/AGENTS.md", "../repo-b"); break;
        }

        var guides = Guides(_workspace, Repo("repo-a"));

        guides.Appendix.ShouldBe(text is null ? "" : $"\n\n--- project-doc (repo-a/AGENTS.md) ---\n\n{text}", customMessage: shape);
        guides.Notices.ShouldBeEmpty(shape);
    }

    [Fact]
    public async Task A_fifo_doc_is_never_opened_for_reading()
    {
        // A blocking open of a FIFO waits for a writer that never comes, and the build would wait with it.
        if (OperatingSystem.IsWindows()) return;

        await MakeFifoAsync(Path.Combine(_tree.Directory("ws/repo-a"), "AGENTS.md"));

        var guides = await System.Threading.Tasks.Task.Run(() => Guides(_workspace, Repo("repo-a"))).WaitAsync(TimeSpan.FromSeconds(1));

        guides.Appendix.ShouldBeEmpty();
        guides.Notices.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("repo a", "repo?a")]
    [InlineData("repo\na", "repo?a")]
    [InlineData("repo)a", "repo?a")]
    public void A_repository_whose_path_the_separator_cannot_carry_is_left_out_and_said(string directory, string printed)
    {
        // The separator repeats the repository's path below the cwd, which the workspace's author chose: a newline or a
        // bracket in it could forge or end a separator line.
        if (OperatingSystem.IsWindows()) return;

        _tree.File($"ws/{directory}/AGENTS.md", "FORGED");

        var guides = Guides(_workspace, Repo(directory));

        guides.Appendix.ShouldBeEmpty();
        guides.Notices.ShouldBe(new[] { $"Left the AGENTS.md of '{printed}' out of this run: its path holds a character other than a letter, a digit or one of . _ @ + - /." });
    }

    [Fact]
    public void A_repository_with_no_doc_says_nothing_whatever_its_path_holds()
    {
        if (OperatingSystem.IsWindows()) return;

        _tree.Directory("ws/repo a");

        var guides = Guides(_workspace, Repo("repo a"));

        guides.Appendix.ShouldBeEmpty();
        guides.Notices.ShouldBeEmpty();
    }

    [Fact]
    public void A_repository_named_twice_or_spelled_two_ways_is_given_once()
    {
        if (OperatingSystem.IsWindows()) return;

        _tree.File("ws/repo-a/AGENTS.md", "A");

        Guides(_workspace, Repo("repo-a"), Repo("repo-a") + "/", Path.Combine(_workspace, "repo-b", "..", "repo-a")).Appendix.ShouldBe("\n\n--- project-doc (repo-a/AGENTS.md) ---\n\nA");
    }

    private string Repo(string relative) => Path.Combine(_workspace, relative);

    private static CodexRepositoryGuides.Plan Guides(string workspace, params string[] repositories) => CodexRepositoryGuides.For(Task(workspace, repositories));

    private static AgentTask Task(string workspace, IReadOnlyList<string>? repositories) => new()
    {
        Goal = "Fix the failing billing tests",
        Harness = CodexHarness.HarnessKind,
        WorkspaceDirectory = workspace,
        WorkspaceRepositoryDirectories = repositories,
    };

    private static async Task MakeFifoAsync(string path)
    {
        using var mkfifo = Process.Start("mkfifo", path)!;
        await mkfifo.WaitForExitAsync();
        mkfifo.ExitCode.ShouldBe(0, $"fixture check: mkfifo {path}");
    }
}
