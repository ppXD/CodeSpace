using CodeSpace.Core.Services.Agents.Harnesses.Claude;
using CodeSpace.Core.Services.Agents.Harnesses.Codex;

namespace CodeSpace.IntegrationTests.Workflows.Infrastructure;

/// <summary>
/// What a fake CLI has to do to keep the "the spawned agent is a TRUSTED test fake" premise TRUE on a LIVE-BRAIN lane.
///
/// <para>A fake arms a harness command env var, and a codex-only fake used to be enough — the supervisor's agent profile
/// defaults to <c>codex-cli</c>. It is not enough any more. On the real-model lanes the brain credential is Anthropic, and
/// <c>HarnessModelReconciler</c> REWRITES an authored <c>codex-cli</c> to <c>claude-code</c> so the agent can actually
/// authenticate — so a codex-only fake leaves every spawned agent running the REAL <c>claude</c> binary on the gateway.
/// That is how run 33972713055's report-only multi-repo arm ended up driving real CLI sessions (41 reconcile warnings),
/// one of which wedged for the agent's full 1h default and took the whole 120-min job down with it.</para>
///
/// <para>The fix has two halves, and a fake needs BOTH: arm <c>CodexHarness.CommandEnvVar</c> AND
/// <c>ClaudeCodeHarness.CommandEnvVar</c> (so whichever harness the reconciler picks resolves to the script), and serve
/// the DIALECT of whichever harness invoked it (so the stream the script prints actually parses). <see cref="Dialects"/>
/// is that second half; <see cref="BothHarnessKinds"/> is the declaration the live arms check the run against via
/// <c>RealModelGate.ClassifyHarnessControl</c>, which is what stops the premise rotting silently a third time.</para>
/// </summary>
public static class FakeAgentCliDialect
{
    /// <summary>The harness kinds a fake that arms BOTH env vars stands in for — the set a live arm checks its spawned agent runs against (<c>RealModelGate.ClassifyHarnessControl</c>). An agent on any other kind ran a real CLI.</summary>
    public static readonly IReadOnlyList<string> BothHarnessKinds = new[] { CodexHarness.HarnessKind, ClaudeCodeHarness.HarnessKind };

    /// <summary>
    /// The harness kinds whose command env var CURRENTLY resolves to one of this suite's fakes — read from the live
    /// process env rather than from a fake's own declaration, so the answer is what the runner would actually spawn.
    /// EMPTY when no fake is armed, which is the honest "this arm expects the REAL binary" case (the real-coding arm).
    /// This is what lets a live arm check control WITHOUT every arm threading its fake's declaration through.
    /// </summary>
    public static IReadOnlyList<string> ArmedFakeHarnessKinds() =>
        BothHarnessKinds.Where(kind => FakeAgentCliMarker.IsFakeCli(Environment.GetEnvironmentVariable(CommandEnvVarFor(kind)))).ToList();

    /// <summary>The command env var a harness kind resolves its binary from.</summary>
    private static string CommandEnvVarFor(string kind) =>
        kind == ClaudeCodeHarness.HarnessKind ? ClaudeCodeHarness.CommandEnvVar : CodexHarness.CommandEnvVar;

    /// <summary>
    /// A POSIX shell function, <c>claude_goal</c>, that prints the goal a Claude invocation carries on stdin, byte for
    /// byte. Claude's stdin is not the goal: it is one stream-json user message whose FIRST text block is the goal
    /// (<c>ClaudeCodeHarness.PromptMessage</c>), so a fake that read it as text would act on the JSON. Codex's stdin is
    /// the goal itself.
    ///
    /// <para>Plain <c>awk</c>, because a fake runs on /bin/sh with nothing else assumed (no jq, no python). It decodes the
    /// one JSON string the harness writes — every escape System.Text.Json emits, <c>\uXXXX</c> and surrogate pairs
    /// included, re-encoded as UTF-8 — under <c>LC_ALL=C</c> so every awk treats the text as bytes. A stdin that is not
    /// such a message yields nothing, so a harness that stopped sending one fails a fake loudly instead of feeding it
    /// JSON. Pinned against the real encoder by <c>SubtaskAwareFakeCliDriftTests</c>.</para>
    /// </summary>
    public const string ClaudeGoalFunction = """
        claude_goal() {
        LC_ALL=C awk '
        function hex(h,  n, k) { n = 0; for (k = 1; k <= 4; k++) n = n * 16 + index("0123456789abcdef", tolower(substr(h, k, 1))) - 1; return n }
        function utf8(c) {
          if (c < 128) printf "%c", c; else if (c < 2048) printf "%c%c", 192 + int(c / 64), 128 + c % 64; else if (c < 65536) printf "%c%c%c", 224 + int(c / 4096), 128 + int(c / 64) % 64, 128 + c % 64; else printf "%c%c%c%c", 240 + int(c / 262144), 128 + int(c / 4096) % 64, 128 + int(c / 64) % 64, 128 + c % 64
        }
        {
          head = "\"content\":[{\"type\":\"text\",\"text\":\""
          p = index($0, head)
          if (p == 0) next
          s = substr($0, p + length(head)); n = length(s)
          for (i = 1; i <= n; i++) {
            ch = substr(s, i, 1)
            if (ch == "\"") exit
            if (ch != "\\") { printf "%s", ch; continue }
            e = substr(s, ++i, 1)
            if (e == "n") printf "\n"; else if (e == "r") printf "\r"; else if (e == "t") printf "\t"; else if (e == "b") printf "\b"; else if (e == "f") printf "\f"; else if (e != "u") printf "%s", e
            else {
              c = hex(substr(s, i + 1, 4)); i += 4
              if (c >= 55296 && c < 56320 && substr(s, i + 1, 2) == "\\u") { c = 65536 + (c - 55296) * 1024 + hex(substr(s, i + 3, 4)) - 56320; i += 6 }
              utf8(c)
            }
          }
        }'
        }

        """;

    /// <summary>
    /// Sets <c>$goal</c> to the goal the invoking harness handed over on stdin — read as text for Codex (argv starts with
    /// <c>exec</c>), decoded out of its stream-json message for Claude (<see cref="ClaudeGoalFunction"/>). A fake that
    /// arms <c>ClaudeCodeHarness.CommandEnvVar</c> reads its goal through this, never <c>$(cat)</c>.
    /// </summary>
    public const string ReadGoal = ClaudeGoalFunction + "if [ \"$1\" = 'exec' ]; then goal=\"$(cat)\"; else goal=\"$(claude_goal)\"; fi\n";

    /// <summary>The goal a Claude spec carries: the first text block of the one stream-json user message on its stdin — what the CLI hands the model, for a test that asserts on the goal a launch was given.</summary>
    public static string ClaudeGoal(string? standardInput)
    {
        using var message = System.Text.Json.JsonDocument.Parse(standardInput ?? throw new ArgumentNullException(nameof(standardInput)));

        return message.RootElement.GetProperty("message").GetProperty("content")[0].GetProperty("text").GetString()!;
    }

    /// <summary>
    /// Wrap a fake's event tail so ONE script serves both harnesses, branching on the single discriminator that is a
    /// property of the invocation: Codex's argv always starts with <c>exec</c>, Claude's with <c>--print</c>. The
    /// <paramref name="codexLines"/> stay BYTE-IDENTICAL to the fake's pre-existing tail, so every codex consumer and
    /// Rule-12.5 drift pin is unperturbed; the claude tail is derived here.
    /// </summary>
    /// <param name="codexLines">The fake's existing codex-shaped JSONL lines, each already newline-terminated.</param>
    /// <param name="claudeSummaryFormat">The printf FORMAT for the summary text both dialects must fold to. Claude folds <c>FinalSummary ?? Completed ?? AssistantMessage</c> while Codex SKIPS Completed, so the <c>result</c> line's <c>result</c> property echoes this same text — never a literal "completed" — or the two dialects silently fold different summaries.</param>
    /// <param name="printfArgs">The argument suffix for those printfs (default the escaped goal). Pass <c>""</c> for a format with no conversion specification — POSIX leaves printf's behaviour UNSPECIFIED when a conversion-free format is handed arguments.</param>
    /// <param name="isError">Whether the claude <c>result</c> line reports a FAILED run (<c>is_error</c>), for a fake that stands in for an agent that could not complete.</param>
    public static string Dialects(string codexLines, string claudeSummaryFormat, string printfArgs = " \"$esc\"", bool isError = false) =>
        "if [ \"$1\" = 'exec' ]; then\n"
      + codexLines
      + "else\n"
      + "printf '{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"" + claudeSummaryFormat + "\"}]}}\\n'" + printfArgs + "\n"
      + "printf '{\"type\":\"result\",\"subtype\":\"" + (isError ? "error_during_execution" : "success") + "\",\"result\":\"" + claudeSummaryFormat + "\",\"is_error\":" + (isError ? "true" : "false") + "}\\n'" + printfArgs + "\n"
      + "fi\n";
}
