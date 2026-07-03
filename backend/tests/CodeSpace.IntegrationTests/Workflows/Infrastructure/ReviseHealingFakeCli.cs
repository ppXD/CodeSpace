using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Harnesses.Codex;

namespace CodeSpace.IntegrationTests.Workflows.Infrastructure;

/// <summary>
/// A fake Codex CLI for the S6 revise-loop fan-out E2Es whose behaviour is a pure function of its GOAL — the
/// draft-then-heal sibling of <see cref="FailFirstThenSucceedFakeCli"/>. A FIRST-round goal (no revise prefix) writes
/// FLAWED work into the fixed <see cref="FileName"/>: content that fails a <c>grep -q revised</c> acceptance check AND
/// carries the critic fake's <see cref="DeterministicCriticLlmClient.RejectMarker"/>. A goal carrying the executor's
/// pinned <see cref="AgentRunExecutor.ReviseInstructionPrefix"/> — the composed revise instruction — writes the FIXED
/// content. So the SAME agent binary, driven only by what the revise loop feeds back, heals both gate kinds: the
/// objective check flips to pass, and the planted flaw the reviewer rejects disappears.
///
/// <para>POSIX <c>/bin/sh</c> only; exits 0 both rounds (the failure signal is the GATE's, not the process's).
/// Stateless across runs — bwrap-safe, and two fan-out branches revising concurrently can't interfere.</para>
/// </summary>
public sealed class ReviseHealingFakeCli : IDisposable
{
    /// <summary>The fixed file every round writes — fixed so the acceptance check (<c>grep -q revised feature.txt</c>) and the revise round grade the SAME artifact the first round produced.</summary>
    public const string FileName = "feature.txt";

    /// <summary>First-round content: flunks the check AND carries the reviewer's reject marker.</summary>
    public const string DraftContent = "draft " + DeterministicCriticLlmClient.RejectMarker;

    /// <summary>Revise-round content: passes the check, no marker.</summary>
    public const string RevisedContent = "revised clean";

    public const string SummaryPrefix = "DONE: ";

    private readonly string _originalCommand;
    private readonly string _dir;

    public ReviseHealingFakeCli()
    {
        _dir = Path.Combine(Path.GetTempPath(), "cs-revise-fakecli-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);

        var script = Path.Combine(_dir, "fake-codex.sh");
        File.WriteAllText(script, ScriptBody);
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

        _originalCommand = Environment.GetEnvironmentVariable(CodexHarness.CommandEnvVar) ?? "";
        Environment.SetEnvironmentVariable(CodexHarness.CommandEnvVar, script);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(CodexHarness.CommandEnvVar, _originalCommand.Length == 0 ? null : _originalCommand);
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    /// <summary>Resolve the goal (Codex's last positional arg); write draft or revised content into the fixed file keyed on the revise prefix; emit the codex-style success stream either way.</summary>
    private static string ScriptBody =>
        "#!/bin/sh\n" +
        "goal=\"\"\n" +
        "for goal in \"$@\"; do :; done\n" +
        "esc=$(printf '%s' \"$goal\" | sed 's/\\\\/\\\\\\\\/g; s/\"/\\\\\"/g' | tr '\\n' ' ')\n" +
        "case \"$goal\" in\n" +
        "  \"" + AgentRunExecutor.ReviseInstructionPrefix + "\"*)\n" +
        "    printf '" + RevisedContent + "\\n' > \"" + FileName + "\"\n" +
        "    ;;\n" +
        "  *)\n" +
        "    printf '" + DraftContent + "\\n' > \"" + FileName + "\"\n" +
        "    ;;\n" +
        "esac\n" +
        "printf '{\"type\":\"agent_reasoning\",\"message\":\"Working on: %s\"}\\n' \"$esc\"\n" +
        "printf '{\"type\":\"agent_message\",\"message\":\"" + SummaryPrefix + "%s\"}\\n' \"$esc\"\n" +
        "printf '{\"type\":\"task_complete\",\"message\":\"completed\"}\\n'\n" +
        "exit 0\n";
}
