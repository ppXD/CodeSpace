using System.Diagnostics;
using CodeSpace.Core.Services.Agents;
using Shouldly;

namespace CodeSpace.IntegrationTests.Agents;

/// <summary>
/// 🟢 High-fidelity proof (Rule 12) that <see cref="InLoopAcceptanceHook.BuildScript"/>'s generated shell content
/// actually BEHAVES as designed — spawns the REAL generated script under a REAL <c>/bin/sh</c>, with a REAL
/// on-disk counter file and a REAL check command, and asserts the process's actual exit code + stderr + counter
/// file contents. No claude/codex CLI involved (that's the separate real-CLI/real-model E2E tier) — this proves
/// the script's OWN logic: block-then-cap, fail-soft on every internal failure mode, and that the acceptance
/// command's exit status (not the hook's own) drives everything.
/// </summary>
[Trait("Category", "Integration")]
public sealed class InLoopAcceptanceHookScriptFlowTests
{
    [Fact]
    public async Task A_passing_check_lets_the_hook_exit_0_without_touching_the_counter()
    {
        if (OperatingSystem.IsWindows()) return;

        using var scratch = new Scratch();
        var script = scratch.WriteScript(InLoopAcceptanceHook.BuildScript(new[] { "true" }, maxBlocks: 1));

        var result = await scratch.RunAsync(script);

        result.ExitCode.ShouldBe(0);
        File.Exists(scratch.CounterPath).ShouldBeFalse("a passing check never increments — there was nothing to block");
    }

    [Fact]
    public async Task A_failing_check_blocks_with_exit_2_and_a_legible_stderr_reason()
    {
        if (OperatingSystem.IsWindows()) return;

        using var scratch = new Scratch();
        var script = scratch.WriteScript(InLoopAcceptanceHook.BuildScript(new[] { "false" }, maxBlocks: 1));

        var result = await scratch.RunAsync(script);

        result.ExitCode.ShouldBe(2);
        result.Stderr.ShouldContain("still failing", Case.Insensitive);
        File.ReadAllText(scratch.CounterPath).Trim().ShouldBe("1");
    }

    [Fact]
    public async Task The_failing_checks_OWN_output_reaches_the_block_reason_actionably()
    {
        // The whole point of in-loop verify is that the model can actually ACT on the feedback — a generic "still
        // failing" with no detail gives it nothing to work with. The hook must surface the check's own stdout/stderr,
        // not just announce that something failed.
        if (OperatingSystem.IsWindows()) return;

        using var scratch = new Scratch();
        var checker = Path.Combine(scratch.Dir, "checker.sh");
        File.WriteAllText(checker, "#!/bin/sh\necho 'assertion failed: expected DONE.txt to exist' 1>&2\nexit 1\n");
        MakeExecutable(checker);

        var script = scratch.WriteScript(InLoopAcceptanceHook.BuildScript(new[] { checker }, maxBlocks: 1));

        var result = await scratch.RunAsync(script);

        result.ExitCode.ShouldBe(2);
        result.Stderr.ShouldContain("expected DONE.txt to exist", Case.Insensitive,
            "the checker's OWN failure message must reach the block reason — that's what makes it actionable rather than a generic notice");
    }

    [Fact]
    public async Task The_counter_caps_at_max_blocks_then_lets_the_harness_stop_even_though_the_check_still_fails()
    {
        if (OperatingSystem.IsWindows()) return;

        using var scratch = new Scratch();
        var script = scratch.WriteScript(InLoopAcceptanceHook.BuildScript(new[] { "false" }, maxBlocks: 2));

        var first = await scratch.RunAsync(script);
        var second = await scratch.RunAsync(script);
        var third = await scratch.RunAsync(script);

        first.ExitCode.ShouldBe(2, "attempt 1 of 2 — still under the cap, blocks");
        second.ExitCode.ShouldBe(2, "attempt 2 of 2 — still under the cap, blocks");
        third.ExitCode.ShouldBe(0, "the cap is reached — the hook gives up and lets the harness stop; the control plane grades the settled result");
    }

    [Fact]
    public async Task An_acceptance_command_that_does_not_exist_fails_soft_instead_of_blocking()
    {
        // exit 126/127 (command not found / not executable) is the check MACHINERY failing to even run — infra, not
        // a genuine failure — mirroring AgentAcceptanceContract.IsInfraFailure's own grader-side philosophy.
        if (OperatingSystem.IsWindows()) return;

        using var scratch = new Scratch();
        var script = scratch.WriteScript(InLoopAcceptanceHook.BuildScript(new[] { "/no/such/binary/codespace-proof-xyz" }, maxBlocks: 1));

        var result = await scratch.RunAsync(script);

        result.ExitCode.ShouldBe(0, "the check binary itself doesn't exist — fail-soft, let the harness stop");
    }

    [Fact]
    public async Task Garbage_on_stdin_never_affects_the_outcome()
    {
        // The hook never parses its stdin at all (drained straight to /dev/null) — feeding it something that
        // is emphatically NOT valid hook JSON must have zero effect on the block/allow decision.
        if (OperatingSystem.IsWindows()) return;

        using var scratch = new Scratch();
        var script = scratch.WriteScript(InLoopAcceptanceHook.BuildScript(new[] { "false" }, maxBlocks: 1));

        var result = await scratch.RunAsync(script, stdin: "{ this is not json at all ]][[");

        result.ExitCode.ShouldBe(2, "garbage stdin doesn't stop the hook from correctly blocking on the failing check");
    }

    [Fact]
    public async Task An_unreadable_counter_file_is_treated_as_zero_not_a_crash()
    {
        if (OperatingSystem.IsWindows()) return;

        using var scratch = new Scratch();
        // A directory where the counter FILE is expected — cat/echo against it fail, but the script must still
        // reach a clean exit rather than aborting on the read/write error.
        Directory.CreateDirectory(scratch.CounterPath);

        var script = scratch.WriteScript(InLoopAcceptanceHook.BuildScript(new[] { "false" }, maxBlocks: 1));

        var result = await scratch.RunAsync(script);

        result.ExitCode.ShouldBe(0, "the counter path is unreadable/unwritable (it's a directory) — fail-soft, let the harness stop");
    }

    [Fact]
    public async Task An_argv_token_with_a_single_quote_and_a_space_reaches_the_check_as_one_unchanged_argument()
    {
        // Proves the generated `set -- '...'` quoting survives a real shell: the check script below fails unless
        // it receives EXACTLY one argument equal to the tricky literal, never re-split or corrupted.
        if (OperatingSystem.IsWindows()) return;

        using var scratch = new Scratch();
        const string tricky = "it's a test value";

        // A double-quoted shell string treats an embedded single quote literally (no escaping needed there) — the
        // single-quote ESCAPING this test is proving lives entirely in InLoopAcceptanceHook.BuildScript's own
        // `set --` line, not in this checker script's comparison.
        var checker = Path.Combine(scratch.Dir, "checker.sh");
        File.WriteAllText(checker, $"#!/bin/sh\n[ \"$#\" -eq 1 ] && [ \"$1\" = \"{tricky}\" ]\n");
        MakeExecutable(checker);

        var script = scratch.WriteScript(InLoopAcceptanceHook.BuildScript(new[] { checker, tricky }, maxBlocks: 1));

        var result = await scratch.RunAsync(script);

        result.ExitCode.ShouldBe(0, "the checker only exits 0 when it received the tricky token intact as ONE argument — proving the check PASSED, so the hook let it stop");
    }

    private static void MakeExecutable(string path) =>
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

    /// <summary>A throwaway config-home-shaped directory: <c>CLAUDE_CONFIG_DIR</c> points at it, <c>hooks/</c> holds the script + counter, exactly mirroring where the real harnesses materialize <see cref="InLoopAcceptanceHook.ScriptRelativePath"/>.</summary>
    private sealed class Scratch : IDisposable
    {
        public string Dir { get; } = Path.Combine(Path.GetTempPath(), "cs-stophook-" + Guid.NewGuid().ToString("N"));
        public string CounterPath => Path.Combine(Dir, "hooks", ".stop-hook-counter");

        public Scratch() => Directory.CreateDirectory(Path.Combine(Dir, "hooks"));

        public string WriteScript(string content)
        {
            var path = Path.Combine(Dir, "hooks", "stop-acceptance-check.sh");
            File.WriteAllText(path, content);
            MakeExecutable(path);
            return path;
        }

        public async Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(string scriptPath, string? stdin = null)
        {
            var psi = new ProcessStartInfo
            {
                FileName = "/bin/sh",
                WorkingDirectory = Dir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                UseShellExecute = false,
            };
            psi.ArgumentList.Add(scriptPath);
            psi.Environment["CLAUDE_CONFIG_DIR"] = Dir;

            using var proc = Process.Start(psi)!;

            if (!string.IsNullOrEmpty(stdin)) await proc.StandardInput.WriteAsync(stdin);
            proc.StandardInput.Close();

            var stdoutTask = proc.StandardOutput.ReadToEndAsync();
            var stderrTask = proc.StandardError.ReadToEndAsync();

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await proc.WaitForExitAsync(cts.Token);

            return (proc.ExitCode, await stdoutTask, await stderrTask);
        }

        public void Dispose()
        {
            try { Directory.Delete(Dir, recursive: true); } catch { /* best-effort */ }
        }
    }
}
