using CodeSpace.Core.Services.Agents.Eval.Benchmark.Graders;
using Shouldly;

namespace CodeSpace.UnitTests.Agents.Benchmark;

/// <summary>
/// 🟢 Unit (pure): the runtime every tests-pass check runs under. What it pins is the boundary between checks the
/// platform can stand behind and checks it cannot: platform-owned Python runs isolated behind <c>-I</c>, the
/// environment cannot be extended from the graded tree, and every other shape is LABELLED rather than silently
/// trusted — a shell or node judge too, however pinned its own bytes are, because what it starts or requires resolves
/// against the graded tree. The real-process half (a planted <c>json.py</c> really does not shadow, a stdlib-named
/// subject is reported) is pinned in <see cref="TestsPassGraderTests"/>.
/// </summary>
[Trait("Category", "Unit")]
public sealed class OracleRuntimeTests
{
    private const string Grading = "/work/grade-1";

    [Fact]
    public void The_unverified_note_marker_is_pinned()
    {
        // The grade's readers and the tests key on this literal; renaming it silently unlabels every forgeable pass.
        OracleRuntime.UnverifiedNoteMarker.ShouldBe("oracle: UNVERIFIED (");
    }

    [Theory]
    [InlineData("/usr/local/bin:/usr/bin", "/usr/local/bin:/usr/bin")]          // the worker's own tools are kept, in order
    [InlineData("/usr/bin::/bin", "/usr/bin:/bin")]                              // an empty entry IS the working directory
    [InlineData("bin:/usr/bin", "/usr/bin")]                                     // a relative entry resolves inside the graded tree
    [InlineData("./node_modules/.bin:/usr/bin", "/usr/bin")]
    [InlineData("{G}/tools:/usr/bin", "/usr/bin")]                               // an absolute entry inside the graded tree
    [InlineData("{G}:/usr/bin", "/usr/bin")]
    [InlineData("{G}-other/bin:/usr/bin", "{G}-other/bin:/usr/bin")]             // a sibling that merely shares the prefix is not inside it
    [InlineData("/usr/bin:/usr/bin", "/usr/bin")]
    [InlineData("tools", "/usr/local/bin:/usr/bin:/bin")]                        // nothing survives → the fallback, never an empty PATH
    [InlineData(null, "/usr/local/bin:/usr/bin:/bin")]
    public void The_PATH_keeps_no_entry_the_graded_tree_could_put_a_binary_in(string? host, string expected)
    {
        if (OperatingSystem.IsWindows()) return;

        OracleRuntime.FixedPath(host?.Replace("{G}", Grading), Grading).ShouldBe(expected.Replace("{G}", Grading));
    }

    [Fact]
    public void Every_check_gets_an_environment_the_graded_tree_cannot_extend()
    {
        if (OperatingSystem.IsWindows()) return;

        var command = BuildWithHost(new[] { "make", "check" }, "tools:/usr/bin");

        command.Environment["PATH"].ShouldBe("/usr/bin");
        command.Environment["PYTHONNOUSERSITE"].ShouldBe("1", "no user site-packages can stand in for a module the judge imports");
        command.Environment["PYTHONDONTWRITEBYTECODE"].ShouldBe("1", "the judge's sealed directory gets no bytecode cache");
        command.Environment["NODE_PATH"].ShouldBe("");
        command.Environment["NODE_OPTIONS"].ShouldBe("", "no --require can preload the candidate's code into a node judge");
    }

    [Fact]
    public void Inline_python_runs_isolated_behind_the_launcher()
    {
        var command = Build(new[] { "python3", "-c", "CODE", "a" });

        command.Command.ShouldBe("python3");
        command.Args.ShouldBe(new[] { "-I", "-B", "-c", OracleRuntime.PythonLauncher, "code", "CODE", "a" }, "-B because -I ignores PYTHONDONTWRITEBYTECODE, and the judge must not write a bytecode cache beside itself");
        command.Note.ShouldBeNull("inline code lives in the spec — it is platform-owned, and -I keeps the graded tree off the front of sys.path");
        command.ReportsShadowing.ShouldBeTrue("the launcher names any module of the graded tree the code imports by a name the interpreter already resolves");
    }

    [Fact]
    public void Python_options_before_the_program_are_kept_in_front_of_the_isolation()
    {
        var command = Build(new[] { "/usr/bin/python3.12", "-u", "-W", "ignore", "-Xutf8", "-B", "-c", "CODE" });

        command.Command.ShouldBe("/usr/bin/python3.12");
        command.Args.ShouldBe(new[] { "-u", "-W", "ignore", "-Xutf8", "-B", "-I", "-B", "-c", OracleRuntime.PythonLauncher, "code", "CODE" });
        command.Note.ShouldBeNull();
    }

    [Fact]
    public void A_pinned_python_judge_in_its_own_directory_runs_isolated_with_that_directory_first()
    {
        var command = Build(new[] { "python3", "./tests/check.py", "--fast" }, "tests/");

        command.Args.ShouldBe(new[] { "-I", "-B", "-c", OracleRuntime.PythonLauncher, "front", "./tests/check.py", "--fast" });
        command.Note.ShouldBeNull();
        command.ReportsShadowing.ShouldBeFalse("its directory is the platform's, so nothing beside it can shadow anything");
    }

    [Fact]
    public void A_pinned_python_judge_at_the_root_runs_isolated_but_is_labelled()
    {
        var command = Build(new[] { "python3", "check.py" }, "check.py");

        command.Args.ShouldBe(new[] { "-I", "-B", "-c", OracleRuntime.PythonLauncher, "back", "check.py" }, "its directory is the candidate's tree, so it trails the standard library");
        command.ReportsShadowing.ShouldBeTrue();
        command.Note.ShouldNotBeNull();
        command.Note!.ShouldStartWith(OracleRuntime.UnverifiedNoteMarker);
        command.Note.ShouldContain("repository root");
    }

    [Fact]
    public void A_python_program_the_candidate_supplies_runs_as_written_and_is_labelled()
    {
        var command = Build(new[] { "python3", "main.py", "7" });

        command.Args.ShouldBe(new[] { "main.py", "7" }, "the subject runs exactly as authored — isolating it would change the work, not the judge");
        command.Note!.ShouldContain("main.py is not a platform-owned judge");
    }

    [Theory]
    [InlineData("python3|-m|pytest|tests", "-m")]
    [InlineData("python3|-uc|CODE", "-uc")]
    [InlineData("python3|--version", "--version")]
    [InlineData("python3", "names no program")]
    public void A_python_command_the_launcher_cannot_stand_in_front_of_runs_as_written_and_is_labelled(string argv, string reason)
    {
        var parts = argv.Split('|');

        var command = Build(parts);

        command.Args.ShouldBe(parts.Skip(1));
        command.Note!.ShouldStartWith(OracleRuntime.UnverifiedNoteMarker);
        command.Note.ShouldContain(reason);
    }

    [Theory]
    [InlineData("sh|check.sh", "check.sh", "repository root")]                    // the root judge owns only itself
    [InlineData("sh|tests/check.sh", "tests/check.sh", "not the files beside it")] // a file pin without its directory
    [InlineData("sh|tests/check.sh", "", "not a platform-owned judge")]
    [InlineData("sh|-c|./check.sh", "check.sh", "-c")]                            // inline shell resolves against the tree
    [InlineData("sh|-ec|true", "", "-c")]
    [InlineData("node|-e|1", "tests/", "-e")]
    [InlineData("node|--test|tests/", "tests/", "--test")]
    [InlineData("make|check", "", "`make` runs against the graded tree")]
    [InlineData("dotnet|test", "", "`dotnet` runs against the graded tree")]
    [InlineData("env|python3|tests/check.py", "tests/", "`env`")]
    [InlineData("npx|jest", "", "`npx`")]
    public void A_check_whose_judge_is_not_pinned_in_its_own_directory_is_labelled(string argv, string pinned, string reason)
    {
        var parts = argv.Split('|');

        var command = Build(parts, pinned.Length == 0 ? Array.Empty<string>() : new[] { pinned });

        command.Args.ShouldBe(parts.Skip(1), "a non-python check always runs exactly as authored");
        command.Note!.ShouldStartWith(OracleRuntime.UnverifiedNoteMarker);
        command.Note.ShouldContain(reason);
    }

    [Theory]
    [InlineData("sh|tests/check.sh")]                                     // a judge in its own pinned directory …
    [InlineData("bash|-eu|tests/check.sh")]                               // flags before the script
    [InlineData("sh|-o|pipefail|tests/check.sh")]                         // an option that takes a value
    [InlineData("sh|tests/unit/check.sh")]                                // a directory inside a pinned one
    [InlineData("/bin/sh|tests/check.sh")]                                // a shell spelled by its absolute path is still a shell
    public void A_pinned_shell_judge_is_still_labelled_because_what_it_starts_runs_against_the_graded_tree(string argv)
    {
        // Every command a shell judge runs gets the graded tree as its working directory: a `python3 -c` it starts finds
        // the candidate's json.py first, a `pytest` it starts loads the candidate's conftest.py. The judge's own bytes are
        // the platform's; what they start is not isolated, so the pass may not read as clean.
        var parts = argv.Split('|');

        var command = Build(parts, "tests/");

        command.Args.ShouldBe(parts.Skip(1));
        command.Note!.ShouldStartWith(OracleRuntime.UnverifiedNoteMarker);
        command.Note.ShouldContain("working directory");
    }

    [Theory]
    [InlineData("./tests/run.sh")]                                        // run directly: its own #! line picks the interpreter
    [InlineData("tests/run")]                                             // extensionless, spelled as a path
    public void A_pinned_judge_run_directly_is_labelled_because_its_interpreter_line_is_not_isolated(string program)
    {
        var command = Build(new[] { program }, "tests/");

        command.Args.ShouldBeEmpty();
        command.Note!.ShouldStartWith(OracleRuntime.UnverifiedNoteMarker);
        command.Note.ShouldContain("interpreter line");
    }

    [Fact]
    public void A_pinned_node_judge_is_labelled_because_node_resolves_modules_up_through_the_graded_tree()
    {
        // `require('chai')` from tests/check.js walks tests/node_modules, then node_modules at the root — the candidate's.
        var command = Build(new[] { "node", "tests/check.js" }, "tests/");

        command.Args.ShouldBe(new[] { "tests/check.js" });
        command.Note!.ShouldStartWith(OracleRuntime.UnverifiedNoteMarker);
        command.Note.ShouldContain("node_modules");
    }

    [Theory]
    [InlineData("/usr/bin/make|check", "`make`")]                         // the same runner as `make check`, spelled by its path
    [InlineData("/usr/local/bin/pytest|-q", "`pytest`")]
    [InlineData("/opt/ci/check.sh", "`check.sh`")]                         // a worker-installed program: what it reads from the tree is unknown
    public void An_absolute_program_outside_the_tree_is_labelled_like_the_bare_word_it_is(string argv, string named)
    {
        var parts = argv.Split('|');

        var command = Build(parts);

        command.Args.ShouldBe(parts.Skip(1));
        command.Note!.ShouldStartWith(OracleRuntime.UnverifiedNoteMarker);
        command.Note.ShouldContain(named);
        command.Note.ShouldContain("runs against the graded tree");
    }

    [Fact]
    public void A_worker_owned_python_judge_outside_the_tree_runs_isolated_and_clean()
    {
        var command = Build(new[] { "python3", "/opt/ci/check.py" });

        command.Args.ShouldBe(new[] { "-I", "-B", "-c", OracleRuntime.PythonLauncher, "front", "/opt/ci/check.py" });
        command.Note.ShouldBeNull("the judge's bytes are the worker's, never the candidate's, and its interpreter is isolated");
    }

    [Theory]
    [InlineData("codespace-oracle: shadowed calendar\nTraceback …", "calendar")]
    [InlineData("noise\ncodespace-oracle: shadowed json,statistics\n", "json, statistics")]
    public void The_launchers_shadow_report_becomes_an_unverified_reason(string stderr, string names)
    {
        var note = OracleRuntime.ShadowNote(stderr);

        note!.ShouldStartWith(OracleRuntime.UnverifiedNoteMarker);
        note.ShouldContain(names);
        note.ShouldContain("standard library or an installed package");
    }

    [Theory]
    [InlineData("")]
    [InlineData("Traceback (most recent call last):\n")]
    public void No_shadow_report_means_no_note(string stderr) => OracleRuntime.ShadowNote(stderr).ShouldBeNull();

    [Theory]
    [InlineData(null, null, null)]
    [InlineData("a", null, "a")]
    [InlineData(null, "b", "b")]
    [InlineData("", "b", "b")]
    [InlineData("a", "b", "a; b")]
    public void Notes_combine_onto_one_line(string? first, string? second, string? expected) =>
        OracleRuntime.CombineNotes(first, second).ShouldBe(expected);

    private static OracleRuntime.OracleCommand Build(IReadOnlyList<string> argv, params string[] pinned) => OracleRuntime.Build(argv, Grading, pinned, "/usr/bin");

    private static OracleRuntime.OracleCommand BuildWithHost(IReadOnlyList<string> argv, string hostPath) => OracleRuntime.Build(argv, Grading, Array.Empty<string>(), hostPath);
}
