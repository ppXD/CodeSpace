using System.Reflection;
using System.Text.Json;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Harnesses.Claude;
using CodeSpace.Core.Services.Agents.Harnesses.Codex;
using CodeSpace.Messages.Agents;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// A DRIFT DETECTOR over the harness doubles this suite folds its scripted runs through.
///
/// <para>The defect it exists for cost two review rounds. A double's folder is handed the executor's accumulated
/// <see cref="AgentRunFacts"/> exactly as a production folder is, but the doubles were dropping them on the floor —
/// so a landing that recorded no session id and no token spend looked correct to every test in this suite, twice. A
/// fixture that reports MORE than production would be caught by an assertion failing; a fixture that reports LESS is
/// invisible, because the only thing it breaks is the test's ability to see a real regression.</para>
///
/// <para><b>Differential, not a hand-written list.</b> Both the production folder and the double are driven with the
/// SAME event stream and the same exit code, and every result property the production folder POPULATED must be
/// populated by the double too. So a production folder that gains a field reddens this the moment it does, with no
/// list here to keep in step — which is the whole point, since the reviewer's finding was that nothing in the suite
/// referenced <see cref="ClaudeCodeResultFolder"/> or <see cref="CodexResultFolder"/> at all.</para>
///
/// <para>Deliberately NOT asserting equality of VALUES: a double's summary and exit reasons are its own, and must
/// stay free to differ. What may not differ is which facts about a run reach the result at all.</para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class AgentFolderDoubleFidelityTests
{
    /// <summary>Properties whose absence from a double says nothing — they are not folded from the stream at all, but assigned by the executor's later enrichment steps (the git capture, the artifact offload, the acceptance grade, the budget).</summary>
    private static readonly IReadOnlySet<string> AssignedAfterTheFold = new HashSet<string>(StringComparer.Ordinal)
    {
        nameof(AgentRunResult.Patch), nameof(AgentRunResult.PatchArtifactId), nameof(AgentRunResult.PatchLossReason),
        nameof(AgentRunResult.FileStats), nameof(AgentRunResult.BaseSha), nameof(AgentRunResult.ChangeSetId),
        nameof(AgentRunResult.RepositoryResults), nameof(AgentRunResult.ProducedBranch), nameof(AgentRunResult.PublishError),
        nameof(AgentRunResult.PublishSkipReason), nameof(AgentRunResult.Transcript), nameof(AgentRunResult.TranscriptArtifactId),
        nameof(AgentRunResult.SessionTranscript), nameof(AgentRunResult.SessionTranscriptArtifactId),
        nameof(AgentRunResult.AcceptancePassed), nameof(AgentRunResult.AcceptanceDetail), nameof(AgentRunResult.Contradiction),
        nameof(AgentRunResult.ReviewFeedback), nameof(AgentRunResult.UnreviewedReason), nameof(AgentRunResult.PendingDecisionId),
        nameof(AgentRunResult.ReviseRounds), nameof(AgentRunResult.CompletionDisposition), nameof(AgentRunResult.Status),
        nameof(AgentRunResult.ExitReason), nameof(AgentRunResult.Error), nameof(AgentRunResult.Summary),
    };

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Every_fact_a_production_folder_reports_is_reported_by_the_scripted_double(int exitCode)
    {
        var events = FoldableStream();

        // Both production folders, so a field only one of them carries still counts.
        var production = new[]
        {
            Populated(new ClaudeCodeHarness().BuildResult(events, exitCode, diagnostics: "")),
            Populated(new CodexHarness().BuildResult(events, exitCode, diagnostics: "")),
        }.SelectMany(p => p).ToHashSet(StringComparer.Ordinal);

        var double_ = Populated(new ScriptedFoldingHarness().BuildResult(events, exitCode, diagnostics: ""));

        var dropped = production.Except(double_).Where(p => !AssignedAfterTheFold.Contains(p)).OrderBy(p => p, StringComparer.Ordinal).ToList();

        dropped.ShouldBeEmpty(
            "a production folder reports these and this suite's double does not, so a run that silently stopped recording them would still look correct here. " +
            "Set them on the double the way ClaudeCodeResultFolder / CodexResultFolder do:\n  " + string.Join("\n  ", dropped));
    }

    /// <summary>The names of every result property carrying something other than its default — "what this folder actually reported about the run".</summary>
    private static IReadOnlyCollection<string> Populated(AgentRunResult result) =>
        typeof(AgentRunResult).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetIndexParameters().Length == 0)
            .Where(p => NotDefault(p.GetValue(result)))
            .Select(p => p.Name)
            .ToHashSet(StringComparer.Ordinal);

    private static bool NotDefault(object? value) => value switch
    {
        null => false,
        string s => s.Length > 0,
        System.Collections.IEnumerable e => e.GetEnumerator().MoveNext(),
        _ => true,
    };

    /// <summary>
    /// A stream rich enough that a production folder reports everything it can: a session id, a model, a token usage,
    /// a changed file, and an assistant message to summarise. Shaped for the FALLBACK fact keys, which both production
    /// harnesses' own key tables are supersets of.
    /// </summary>
    private static IReadOnlyList<AgentEvent> FoldableStream() =>
    [
        WithData(AgentEventKind.AssistantMessage, "starting", new { session_id = "sess-drift-1", model = "test-model" }),
        new AgentEvent { Kind = AgentEventKind.FileChanged, Text = "src/app.ts" },
        new AgentEvent { Kind = AgentEventKind.AssistantMessage, Text = "did the thing" },
        WithData(AgentEventKind.AssistantMessage, "done", new { usage = new { input_tokens = 1200, output_tokens = 340 } }),
    ];

    private static AgentEvent WithData(AgentEventKind kind, string text, object data) =>
        new() { Kind = kind, Text = text, Data = JsonSerializer.SerializeToElement(data) };

    /// <summary>The folding shape this suite's scripted doubles share, kept here so the detector measures the SAME construction they use rather than a copy that could pass while they fail.</summary>
    private sealed class ScriptedFoldingHarness : IAgentHarness
    {
        public string Kind => "scripted";
        public string Version => "test";
        public IReadOnlyList<string> Models { get; } = new[] { "test-model" };

        public SandboxSpec BuildInvocation(AgentTask task) => new() { Command = "/bin/sh", Args = ["-c", "true"], TimeoutSeconds = task.TimeoutSeconds };

        public IReadOnlyList<AgentEvent> ParseEvents(string rawLine) => Array.Empty<AgentEvent>();

        public IAgentEventFolder CreateFolder() => ScriptedFolders.Result();
    }
}
