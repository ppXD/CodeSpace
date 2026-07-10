using System.Diagnostics;
using System.Text.Json;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Harnesses.Claude;
using CodeSpace.Core.Services.Agents.Harnesses.Codex;
using CodeSpace.Core.Services.Agents.Sandbox.Runners;
using CodeSpace.Messages.Agents;
using Shouldly;

namespace CodeSpace.IntegrationTests.Agents;

/// <summary>
/// 🟢 High-fidelity proof (Rule 12) that the in-loop acceptance Stop hook is actually INVOCABLE the way the real
/// CLIs invoke it. <see cref="InLoopAcceptanceHookScriptFlowTests"/> proves the generated script's own logic, but it
/// writes its own copy, chmods it, and runs it as <c>/bin/sh &lt;path&gt;</c> (interpreter form, no +x needed) — which is
/// exactly how a missing execute bit stayed invisible: the REAL wiring (settings.json / hooks.json) invokes the
/// script by direct command path under <c>sh -c</c>, which execs the FILE and dies with 126 when it isn't executable,
/// a non-blocking hook error both CLIs swallow. This suite materializes the files through the REAL
/// <see cref="LocalProcessRunner.WriteConfigHomeFiles"/> path and executes the EXACT command string from the
/// harness's own generated hook config — no chmod, no interpreter shortcut.
/// </summary>
[Trait("Category", "Integration")]
public sealed class StopHookMaterializationFlowTests
{
    [Fact]
    public async Task The_claude_hook_materialized_by_the_real_runner_is_executable_via_the_settings_json_command()
    {
        if (OperatingSystem.IsWindows()) return;

        using var scratch = new Scratch();
        var spec = new ClaudeCodeHarness().BuildInvocation(Task(ClaudeCodeHarness.HarnessKind, scratch.Workspace));

        LocalProcessRunner.WriteConfigHomeFiles(spec.ConfigHomeFiles, scratch.ConfigHome);

        var command = ReadStopHookCommand(scratch.ConfigHome, "settings.json");
        var result = await scratch.RunViaShellAsync(command, "CLAUDE_CONFIG_DIR");

        result.ExitCode.ShouldNotBe(126, $"exit 126 = 'Permission denied' — the materialized script lacks +x, so the CLI's hook invocation silently never runs the acceptance command. stderr: {result.Stderr}");
        result.ExitCode.ShouldBe(0, $"the acceptance command is 'true', so an invocable hook lets the stop through. stderr: {result.Stderr}");
    }

    [Fact]
    public async Task The_codex_hook_materialized_by_the_real_runner_is_executable_via_the_hooks_json_command()
    {
        if (OperatingSystem.IsWindows()) return;

        using var scratch = new Scratch();
        var spec = new CodexHarness().BuildInvocation(Task(CodexHarness.HarnessKind, scratch.Workspace));

        LocalProcessRunner.WriteConfigHomeFiles(spec.ConfigHomeFiles, scratch.ConfigHome);

        var command = ReadStopHookCommand(scratch.ConfigHome, "hooks.json");
        var result = await scratch.RunViaShellAsync(command, "CODEX_HOME");

        result.ExitCode.ShouldNotBe(126, $"exit 126 = 'Permission denied' — the materialized script lacks +x, so the CLI's hook invocation silently never runs the acceptance command. stderr: {result.Stderr}");
        result.ExitCode.ShouldBe(0, $"the acceptance command is 'true', so an invocable hook lets the stop through. stderr: {result.Stderr}");
    }

    /// <summary>An acceptance-bearing task whose check ('true') passes, so the only failure mode left is invocability itself.</summary>
    private static AgentTask Task(string harness, string workspace) => new()
    {
        Goal = "Fix the failing billing tests",
        Harness = harness,
        Model = "m",
        WorkspaceDirectory = workspace,
        TimeoutSeconds = 900,
        Acceptance = new SupervisorAcceptanceSpec { Command = new[] { "true" } },
    };

    /// <summary>The command string the harness itself wired at hooks.Stop — executed verbatim, exactly as the CLI does.</summary>
    private static string ReadStopHookCommand(string configHome, string wiringFile)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(configHome, wiringFile)));
        return doc.RootElement.GetProperty("hooks").GetProperty("Stop")[0].GetProperty("hooks")[0].GetProperty("command").GetString()!;
    }

    private sealed class Scratch : IDisposable
    {
        public string Dir { get; } = Path.Combine(Path.GetTempPath(), "cs-hookmat-" + Guid.NewGuid().ToString("N"));
        public string ConfigHome => Path.Combine(Dir, "home");
        public string Workspace => Path.Combine(Dir, "ws");

        public Scratch()
        {
            Directory.CreateDirectory(ConfigHome);
            Directory.CreateDirectory(Workspace);
        }

        public async Task<(int ExitCode, string Stdout, string Stderr)> RunViaShellAsync(string command, string configHomeEnvVar)
        {
            var psi = new ProcessStartInfo
            {
                FileName = "/bin/sh",
                WorkingDirectory = Workspace,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                UseShellExecute = false,
            };
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(command);
            psi.Environment[configHomeEnvVar] = ConfigHome;

            using var proc = Process.Start(psi)!;
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
