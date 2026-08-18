using System.Reflection;
using System.Text.Json;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Harnesses.Claude;
using CodeSpace.Core.Services.Agents.Harnesses.Codex;
using CodeSpace.Core.Services.Agents.Sandbox;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// 🟢 Unit: the DIFFERENTIAL + memory-shape pin for <see cref="AgentResultFold"/> — the bounded accumulator that
/// replaced the whole-run <c>List&lt;AgentEvent&gt;</c> the executor used to hold for the harness's result fold.
///
/// <para><b>Why differential.</b> The refactor's only acceptable outcome is BYTE-IDENTICAL results: for any event
/// stream + exit code, <c>BuildResult</c> must produce exactly what it produced before. So this file carries a FROZEN
/// mirror of the pre-change list-based reduction (<see cref="LegacyClaudeBuildResult"/> / <see cref="LegacyCodexBuildResult"/>,
/// transcribed verbatim from the harnesses at the commit before the fold landed) and asserts the production fold agrees
/// field-by-field over a representative multi-round stream. Unlike a Rule-12.5 mirror this one must NEVER be re-synced:
/// it is the frozen historical spec, and a divergence means the refactor changed behaviour — the exact regression this
/// test exists to catch. The mirror reduces via the readers' LIST overloads, which their own unit tests
/// (<c>AgentTokenUsageReaderTests</c>, <c>AgentSessionIdReaderTests</c>, <c>AgentModelReaderTests</c>,
/// <c>AgentTerminalOutcomeReaderTests</c>) pin independently; the fold reduces via the per-event overloads, so a
/// wrong accumulation direction (first-wins vs last-wins) fails here.</para>
///
/// <para><b>Why memory-shape.</b> The bug being fixed was unbounded retention — a ~2 GiB-stdout run OOM'd and landed
/// Failed("executor-error"). So retention is asserted structurally: the fold holds no event-shaped field at all, and
/// what it does hold does not grow with the event count.</para>
/// </summary>
[Trait("Category", "Unit")]
public class AgentResultFoldTests
{
    // ── Differential: the fold vs the frozen pre-change list reduction ───────────────────────────────

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(137)]
    public void The_claude_fold_matches_the_frozen_list_reduction_over_a_representative_stream(int exitCode)
    {
        var events = RepresentativeStream();

        new ClaudeCodeHarness().BuildResult(events, exitCode).ShouldMatch(LegacyClaudeBuildResult(events, exitCode));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(137)]
    public void The_codex_fold_matches_the_frozen_list_reduction_over_a_representative_stream(int exitCode)
    {
        var events = RepresentativeStream();

        new CodexHarness().BuildResult(events, exitCode).ShouldMatch(LegacyCodexBuildResult(events, exitCode));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Both_folds_match_the_frozen_list_reduction_when_the_harness_itself_reported_failure(int exitCode)
    {
        // exit 0 + a trailing Error is the "harness-reported-failure" branch AgentTerminalOutcomeReader decides —
        // the branch a last-terminal-kind accumulator gets wrong if it tracks the wrong kinds.
        var events = RepresentativeStream().Append(Event(AgentEventKind.Error, "gateway 429 mid-turn")).ToList();

        new ClaudeCodeHarness().BuildResult(events, exitCode).ShouldMatch(LegacyClaudeBuildResult(events, exitCode));
        new CodexHarness().BuildResult(events, exitCode).ShouldMatch(LegacyCodexBuildResult(events, exitCode));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Both_folds_match_the_frozen_list_reduction_for_an_empty_stream(int exitCode)
    {
        var events = Array.Empty<AgentEvent>();

        new ClaudeCodeHarness().BuildResult(events, exitCode).ShouldMatch(LegacyClaudeBuildResult(events, exitCode));
        new CodexHarness().BuildResult(events, exitCode).ShouldMatch(LegacyCodexBuildResult(events, exitCode));
    }

    [Fact]
    public void The_claude_fold_still_prefers_a_blank_final_summary_over_falling_through_to_the_assistant_message()
    {
        // The pre-change reduction picked the EVENT via ?? and then read .Text, so a FinalSummary with blank text
        // wins and the summary is blank — it never falls through to Completed/AssistantMessage. A naive
        // `LastTextOf(FinalSummary) ?? LastTextOf(AssistantMessage)` would silently "improve" that. It must not.
        var events = new[] { Event(AgentEventKind.AssistantMessage, "a useful message"), Event(AgentEventKind.FinalSummary, "") };

        new ClaudeCodeHarness().BuildResult(events, exitCode: 0).ShouldMatch(LegacyClaudeBuildResult(events, exitCode: 0));
        new CodexHarness().BuildResult(events, exitCode: 0).ShouldMatch(LegacyCodexBuildResult(events, exitCode: 0));
    }

    // ── Re-attach parity: the live tail and the re-attached tail fold identically ────────────────────

    [Fact]
    public void A_fold_accumulated_line_by_line_equals_one_built_from_the_whole_list()
    {
        // The live path (RunHarnessAsync) folds each parsed event as it streams; the re-attach path
        // (ReattachAndFoldAsync) folds the re-tailed spool the same way. Both reach the SAME MapSandboxResult,
        // so an incrementally-accumulated fold must be indistinguishable from one built over the whole stream.
        var events = RepresentativeStream();

        var streamed = new AgentResultFold();
        foreach (var e in events) streamed.Add(e);

        var sandbox = new SandboxResult { Status = SandboxStatus.Success, ExitCode = 0, Stdout = "", Stderr = "" };
        var harness = new ClaudeCodeHarness();

        AgentRunExecutor.MapSandboxResult(sandbox, harness, streamed)
            .ShouldMatch(AgentRunExecutor.MapSandboxResult(sandbox, harness, AgentResultFold.From(events)));
    }

    [Theory]
    [InlineData(SandboxStatus.TimedOut)]
    [InlineData(SandboxStatus.Stalled)]
    public void A_forced_terminal_reads_the_same_facts_off_the_fold_as_off_the_whole_list(SandboxStatus status)
    {
        // The forced-terminal branches never consult the harness — they read usage / session id / model directly.
        // Those three must come off the fold with the same first-wins / last-wins direction the readers used.
        var events = RepresentativeStream();
        var sandbox = new SandboxResult { Status = status, ExitCode = -1, Stdout = "", Stderr = "" };

        var result = AgentRunExecutor.MapSandboxResult(sandbox, harness: null!, AgentResultFold.From(events));

        result.SessionId.ShouldBe(AgentSessionIdReader.TryRead(events));
        result.Model.ShouldBe(AgentModelReader.TryRead(events));
        result.TokenUsage.ShouldBe(AgentTokenUsageReader.TryRead(events));
    }

    // ── Memory shape: retention is O(1) in the event count ───────────────────────────────────────────

    [Fact]
    public void The_fold_holds_no_field_that_can_retain_events()
    {
        // The bug was retention: `var events = new List<AgentEvent>()` for the WHOLE run. The structural guarantee
        // that it cannot come back is that no field of the fold is shaped to hold an AgentEvent at all.
        var eventBearing = FoldFields()
            .Where(f => MentionsEvent(f.FieldType))
            .Select(f => $"{f.Name}:{f.FieldType.Name}")
            .ToList();

        eventBearing.ShouldBeEmpty("AgentResultFold must retain O(1) reductions, never the events themselves — a field typed over AgentEvent reintroduces the whole-run retention this fold exists to remove");
    }

    [Fact]
    public void The_fold_retention_does_not_grow_with_the_event_count()
    {
        // Same kind/file mix, 200x the events: every retained collection must hold exactly as much.
        var small = RetainedCounts(AgentResultFold.From(SyntheticStream(1_000)));
        var large = RetainedCounts(AgentResultFold.From(SyntheticStream(200_000)));

        large.ShouldBe(small, "the fold's retained state must be O(1) in the event count — if a count grew, some reduction is still accumulating per event");
    }

    [Fact]
    public void Every_collection_the_fold_retains_is_observed_by_the_growth_guard()
    {
        // The growth guard compares only the fields RetainedCounts can see, so an unobservable field would shrink the
        // comparison to nothing and still pass. Pin the observed SET: a new retained collection must either appear here
        // deliberately or fail loudly, never vanish from the O(1) assertion.
        var observed = RetainedCounts(AgentResultFold.From(SyntheticStream(8))).Keys.OrderBy(n => n, StringComparer.Ordinal).ToArray();

        observed.ShouldBe(new[] { "_changedFileKeys", "_changedFiles", "_lastTextByKind" },
            customMessage: "a retained collection is missing from the growth guard — widen RetainedCounts (or justify the new field here) rather than letting it go unobserved");
    }

    [Fact]
    public void The_fold_keeps_only_the_distinct_changed_files_not_one_entry_per_event()
    {
        // The one retained collection that legitimately grows is ChangedFiles — bounded by the number of DISTINCT
        // files touched, never by the number of events. SyntheticStream re-touches the same 3 paths forever.
        var fold = AgentResultFold.From(SyntheticStream(200_000));

        fold.ChangedFiles.Count.ShouldBe(3);
    }

    /// <summary>
    /// Every retained ENUMERABLE field, by name → element count. Filters on <see cref="System.Collections.IEnumerable"/>
    /// rather than the non-generic <c>ICollection</c>, which <c>HashSet&lt;T&gt;</c> does NOT implement — that filter
    /// silently dropped the fold's dedupe set from the comparison, and would drop any future SortedSet / ImmutableArray
    /// / generic-only collection the same way, so the O(1) assertion failed OPEN. Counted by enumeration for the same
    /// reason. Private because the fold exposes no test-only surface.
    /// </summary>
    private static Dictionary<string, int> RetainedCounts(AgentResultFold fold) =>
        FoldFields()
            .Where(f => typeof(System.Collections.IEnumerable).IsAssignableFrom(f.FieldType) && f.FieldType != typeof(string))
            .ToDictionary(f => f.Name, f => ((System.Collections.IEnumerable)f.GetValue(fold)!).Cast<object>().Count());

    private static FieldInfo[] FoldFields() => typeof(AgentResultFold).GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

    private static bool MentionsEvent(Type type) =>
        type == typeof(AgentEvent) || type.GetGenericArguments().Any(MentionsEvent) || (type.IsArray && MentionsEvent(type.GetElementType()!));

    // ── Fixtures ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A multi-round stream carrying every fact the fold reduces: model + session id up front, two cumulative usage reports, duplicate + blank FileChanged texts, an Error that is later superseded, and a trailing FinalSummary.</summary>
    private static IReadOnlyList<AgentEvent> RepresentativeStream() => new[]
    {
        Json(AgentEventKind.Started, "Session started", """{"session_id":"sess-diff-001","model":"claude-opus-4"}"""),
        Event(AgentEventKind.AssistantMessage, "round 1: reading the repo"),
        Event(AgentEventKind.FileChanged, "src/Foo.cs"),
        Event(AgentEventKind.FileChanged, ""),
        Event(AgentEventKind.Error, "round 1 tool call failed"),
        Json(AgentEventKind.Completed, "Turn complete", """{"usage":{"input_tokens":1200,"output_tokens":340}}"""),
        Event(AgentEventKind.AssistantMessage, "round 2: applying the fix"),
        Event(AgentEventKind.FileChanged, "src/Foo.cs"),
        Event(AgentEventKind.FileChanged, "src/Bar.cs"),
        Json(AgentEventKind.Started, "Session started", """{"session_id":"sess-diff-LATER","model":"claude-haiku-4"}"""),
        Json(AgentEventKind.Completed, "Turn complete", """{"usage":{"input_tokens":4800,"output_tokens":990}}"""),
        Event(AgentEventKind.FinalSummary, "fixed the parser and added a regression test"),
    };

    /// <summary>A long stream with a FIXED distinct-file set — the memory-shape probe. The payload documents are held alive for the whole fold (a JsonElement dies with its JsonDocument).</summary>
    private static IReadOnlyList<AgentEvent> SyntheticStream(int count)
    {
        var files = new[] { "src/A.cs", "src/B.cs", "src/C.cs" };
        var kinds = new[] { AgentEventKind.AssistantMessage, AgentEventKind.Reasoning, AgentEventKind.ToolCall, AgentEventKind.CommandExecuted, AgentEventKind.Warning };
        var payload = JsonDocument.Parse("""{"usage":{"input_tokens":7,"output_tokens":3},"session_id":"sess-synth","model":"synth-model"}""").RootElement.Clone();

        var events = new List<AgentEvent>(count);

        for (var i = 0; i < count; i++)
            events.Add(i % 4 == 0
                ? new AgentEvent { Kind = AgentEventKind.FileChanged, Text = files[i % files.Length] }
                : new AgentEvent { Kind = kinds[i % kinds.Length], Text = $"step {i}", Data = payload });

        return events;
    }

    private static AgentEvent Event(AgentEventKind kind, string text) => new() { Kind = kind, Text = text };

    private static AgentEvent Json(AgentEventKind kind, string text, string json) => new() { Kind = kind, Text = text, Data = JsonDocument.Parse(json).RootElement.Clone() };

    // ── The FROZEN pre-change reduction. Transcribed verbatim; never re-sync it to production. ───────

    private static AgentRunResult LegacyClaudeBuildResult(IReadOnlyList<AgentEvent> events, int exitCode)
    {
        var changedFiles = events.Where(e => e.Kind == AgentEventKind.FileChanged).Select(e => e.Text).Where(t => t.Length > 0).Distinct().ToList();
        var summary = (events.LastOrDefault(e => e.Kind == AgentEventKind.FinalSummary)
                       ?? events.LastOrDefault(e => e.Kind == AgentEventKind.Completed)
                       ?? events.LastOrDefault(e => e.Kind == AgentEventKind.AssistantMessage))?.Text;

        var usage = AgentTokenUsageReader.TryRead(events);
        var sessionId = AgentSessionIdReader.TryRead(events);
        var model = AgentModelReader.TryRead(events);

        if (exitCode == 0 && !AgentTerminalOutcomeReader.ReportedFailure(events))
            return new AgentRunResult { Status = AgentRunStatus.Succeeded, ExitReason = "completed", Summary = summary, ChangedFiles = changedFiles, TokenUsage = usage, SessionId = sessionId, Model = model };

        var error = events.LastOrDefault(e => e.Kind == AgentEventKind.Error)?.Text
                    ?? (string.IsNullOrWhiteSpace(summary) ? null : summary)
                    ?? $"claude exited with code {SandboxExitCode.Describe(exitCode)}";

        var exitReason = exitCode != 0 ? "non-zero-exit" : "harness-reported-failure";

        return new AgentRunResult { Status = AgentRunStatus.Failed, ExitReason = exitReason, Summary = summary, ChangedFiles = changedFiles, Error = error, TokenUsage = usage, SessionId = sessionId, Model = model };
    }

    private static AgentRunResult LegacyCodexBuildResult(IReadOnlyList<AgentEvent> events, int exitCode)
    {
        var changedFiles = events.Where(e => e.Kind == AgentEventKind.FileChanged).Select(e => e.Text).Where(t => t.Length > 0).Distinct().ToList();
        var summary = (events.LastOrDefault(e => e.Kind == AgentEventKind.FinalSummary) ?? events.LastOrDefault(e => e.Kind == AgentEventKind.AssistantMessage))?.Text;

        var usage = AgentTokenUsageReader.TryRead(events);
        var sessionId = AgentSessionIdReader.TryRead(events);
        var model = AgentModelReader.TryRead(events);

        if (exitCode == 0 && !AgentTerminalOutcomeReader.ReportedFailure(events))
            return new AgentRunResult { Status = AgentRunStatus.Succeeded, ExitReason = "completed", Summary = summary, ChangedFiles = changedFiles, TokenUsage = usage, SessionId = sessionId, Model = model };

        var error = events.LastOrDefault(e => e.Kind == AgentEventKind.Error)?.Text
                    ?? (string.IsNullOrWhiteSpace(summary) ? null : summary)
                    ?? $"codex exited with code {SandboxExitCode.Describe(exitCode)}";

        var exitReason = exitCode != 0 ? "non-zero-exit" : "harness-reported-failure";

        return new AgentRunResult { Status = AgentRunStatus.Failed, ExitReason = exitReason, Summary = summary, ChangedFiles = changedFiles, Error = error, TokenUsage = usage, SessionId = sessionId, Model = model };
    }
}

/// <summary>Field-by-field equality for <see cref="AgentRunResult"/> — the record's generated Equals compares <c>ChangedFiles</c> by reference, which would pass a differential assertion vacuously.</summary>
internal static class AgentRunResultDifferentialAssertions
{
    public static void ShouldMatch(this AgentRunResult actual, AgentRunResult expected)
    {
        actual.Status.ShouldBe(expected.Status);
        actual.ExitReason.ShouldBe(expected.ExitReason);
        actual.Summary.ShouldBe(expected.Summary);
        actual.Error.ShouldBe(expected.Error);
        actual.SessionId.ShouldBe(expected.SessionId);
        actual.Model.ShouldBe(expected.Model);
        actual.TokenUsage.ShouldBe(expected.TokenUsage);
        actual.ChangedFiles.ShouldBe(expected.ChangedFiles);
    }
}
