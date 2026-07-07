using CodeSpace.Core.Services.Agents.Harnesses.Codex;

namespace CodeSpace.IntegrationTests.Workflows.Infrastructure;

/// <summary>
/// The fake REVIEWER CLI for the S8 agent-based-review E2Es — a Codex-shaped binary whose verdict is a pure function
/// of the WORKSPACE IT WAS CLONED INTO: it greps the checkout for the planted flaw marker and emits the
/// <c>VERDICT:</c> final-message contract the production <c>AgentOutputReviewer</c> parses — disapproved WITH
/// evidence while the marker is present, approved once a revision removed it. This is the honest seam for the
/// reviewer's INTELLIGENCE only: the review run itself is a real <c>AgentRun</c> through the real executor, runner,
/// and git clone of the produced branch — the fake just decides what a competent reviewer would have concluded.
///
/// <para>POSIX <c>/bin/sh</c>; stateless; exits 0 either way (a disapproval is a VERDICT, not a process failure).
/// The grep deliberately targets tracked files (<c>--exclude-dir=.git</c>) — the verdict must reflect the produced
/// tree, not loose object encoding.</para>
/// </summary>
public sealed class ReviewVerdictFakeCli : IDisposable
{
    /// <summary>The planted flaw the reviewer hunts — the same marker the critic fakes reject, so one flawed artifact trips every review lane.</summary>
    public const string FlawMarker = DeterministicCriticLlmClient.RejectMarker;

    /// <summary>The disapproval's rationale — asserted verbatim by the E2Es (and fed back by the Improve revise round).</summary>
    public const string DisapproveRationale = "the produced tree still carries a placeholder hack";

    private readonly string _originalCommand;
    private readonly string _dir;

    public ReviewVerdictFakeCli()
    {
        _dir = Path.Combine(Path.GetTempPath(), "cs-reviewer-fakecli-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);

        var script = Path.Combine(_dir, "fake-reviewer.sh");
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

    /// <summary>Grep the CLONE for the flaw; emit the codex-style stream whose final agent message is the VERDICT contract line.</summary>
    private static string ScriptBody =>
        "#!/bin/sh\n" +
        "printf '{\"type\":\"agent_reasoning\",\"message\":\"Inspecting the produced tree\"}\\n'\n" +
        "if grep -rq --exclude-dir=.git '" + FlawMarker + "' .; then\n" +
        "  printf '{\"type\":\"agent_message\",\"message\":\"VERDICT: {\\\\\"approved\\\\\": false, \\\\\"rationale\\\\\": \\\\\"" + DisapproveRationale + "\\\\\", \\\\\"issues\\\\\": [{\\\\\"issue\\\\\": \\\\\"placeholder hack committed\\\\\", \\\\\"evidence\\\\\": \\\\\"grep found " + FlawMarker + " in the produced tree\\\\\", \\\\\"severity\\\\\": \\\\\"blocker\\\\\"}]}\"}\\n'\n" +
        "else\n" +
        "  printf '{\"type\":\"agent_message\",\"message\":\"VERDICT: {\\\\\"approved\\\\\": true, \\\\\"rationale\\\\\": \\\\\"clean and goal-aligned\\\\\", \\\\\"issues\\\\\": []}\"}\\n'\n" +
        "fi\n" +
        "printf '{\"type\":\"task_complete\",\"message\":\"completed\"}\\n'\n" +
        "exit 0\n";
}
