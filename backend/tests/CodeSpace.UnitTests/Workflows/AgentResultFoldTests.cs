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
/// 🟢 Unit: the DIFFERENTIAL + memory-shape pin for the per-harness result folders — the O(1) accumulators that
/// replaced the whole-run <c>List&lt;AgentEvent&gt;</c> the executor used to hold, and that each harness now OWNS
/// (<see cref="IAgentHarness.CreateFolder"/>) instead of sharing one concrete type on the seam.
///
/// <para><b>Why differential.</b> Neither the bounded-fold change nor this ownership inversion may alter a single
/// field: for any event stream + exit code, the folded result must be exactly what the pre-fold whole-list reduction
/// produced. So this file carries a FROZEN mirror of that reduction (<see cref="LegacyClaudeBuildResult"/> /
/// <see cref="LegacyCodexBuildResult"/>, transcribed verbatim from the harnesses at the commit before the fold
/// landed) and asserts the production folders agree field-by-field over a representative multi-round stream. Unlike
/// a Rule-12.5 mirror this one must NEVER be re-synced: it is the frozen historical spec, and a divergence means a
/// refactor changed behaviour — the exact regression this test exists to catch. The mirror reduces via the readers'
/// LIST overloads, which their own unit tests (<c>AgentTokenUsageReaderTests</c>, <c>AgentSessionIdReaderTests</c>,
/// <c>AgentModelReaderTests</c>, <c>AgentTerminalOutcomeReaderTests</c>) pin independently; the folders reduce via
/// the per-event overloads, so a wrong accumulation direction (first-wins vs last-wins) fails here.</para>
///
/// <para><b>Why memory-shape.</b> The bug being fixed was unbounded retention — a ~2 GiB-stdout run OOM'd and landed
/// Failed("executor-error"). So retention is asserted structurally, and TRANSITIVELY: a folder composes its
/// reduction, so a guard that saw only the folder's own declared fields would find one object reference and pass
/// vacuously.</para>
///
/// <para><b>Why a third folder.</b> The inversion's payoff is that a harness needing a reduction no other harness
/// has changes nothing shared. <see cref="ToolCallCountingFolder"/> is that harness, declared entirely in this file.</para>
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
    public void A_folder_accumulated_line_by_line_equals_one_built_from_the_whole_list()
    {
        // The live path (RunHarnessAsync) folds each parsed event as it streams; the re-attach path
        // (ReattachAndFoldAsync) folds the re-tailed spool the same way. Both reach the SAME MapSandboxResult,
        // so an incrementally-accumulated folder must be indistinguishable from one built over the whole stream.
        var events = RepresentativeStream();
        var harness = new ClaudeCodeHarness();

        var streamed = harness.CreateFolder();
        var streamedFacts = new AgentRunFacts();
        foreach (var e in events) { streamed.Add(e); streamedFacts.Add(e); }

        var sandbox = new SandboxResult { Status = SandboxStatus.Success, ExitCode = 0, Stdout = "", Stderr = "" };

        AgentRunExecutor.MapSandboxResult(sandbox, streamed, streamedFacts)
            .ShouldMatch(AgentRunExecutor.MapSandboxResult(sandbox, harness.Folded(events), AgentRunFacts.From(events)));
    }

    [Theory]
    [InlineData(SandboxStatus.TimedOut)]
    [InlineData(SandboxStatus.Stalled)]
    public void A_forced_terminal_reads_the_same_facts_off_the_accumulator_as_off_the_whole_list(SandboxStatus status)
    {
        // The forced-terminal branches never consult the harness — they read usage / session id / model off the
        // executor's own AgentRunFacts, which is why the folder can be null here. Those three must come off it with
        // the same first-wins / last-wins direction the readers used.
        var events = RepresentativeStream();
        var sandbox = new SandboxResult { Status = status, ExitCode = -1, Stdout = "", Stderr = "" };

        var result = AgentRunExecutor.MapSandboxResult(sandbox, folder: null!, AgentRunFacts.From(events));

        result.SessionId.ShouldBe(AgentSessionIdReader.TryRead(events));
        result.Model.ShouldBe(AgentModelReader.TryRead(events));
        result.TokenUsage.ShouldBe(AgentTokenUsageReader.TryRead(events));
    }

    // ── The inversion's payoff: a private reduction costs no shared type ─────────────────────────────

    [Fact]
    public void A_harness_whose_folder_keeps_a_reduction_no_other_harness_has_changes_no_shared_type()
    {
        // ToolCallCountingFolder retains a counter NOTHING in Core knows about, declared entirely at the bottom of
        // this file. That it compiles and folds at all IS the property under test: while every harness shared one
        // concrete accumulator, the only home for such a reduction was a new field on that shared type — which is
        // exactly how a last-text-of-any-kind field neither production harness read got there. Now it costs nothing.
        var events = new[]
        {
            Event(AgentEventKind.ToolCall, "grep"),
            Event(AgentEventKind.AssistantMessage, "thinking"),
            Event(AgentEventKind.ToolCall, "edit"),
        };

        new ToolCallCountingHarness().BuildResult(events, exitCode: 0).Summary.ShouldBe("2 tool calls");
    }

    // ── Memory shape: retention is O(1) in the event count ───────────────────────────────────────────

    [Fact]
    public void No_accumulator_the_executor_holds_for_a_whole_run_can_retain_events()
    {
        // The bug was retention: `var events = new List<AgentEvent>()` for the WHOLE run. The structural guarantee
        // that it cannot come back is that no field REACHABLE from what the executor holds for the run's duration —
        // each harness's folder, plus the run-facts accumulator — is shaped to hold an AgentEvent. Reachable, not
        // declared: a folder that composes its reduction must not be able to hide the retention one level down.
        foreach (var (name, type) in RunLongAccumulators())
            EventBearingFields(type).ToList()
                .ShouldBeEmpty($"{name} must retain O(1) reductions, never the events themselves — a field typed over AgentEvent reintroduces the whole-run retention this seam exists to remove");
    }

    [Fact]
    public void Folder_retention_does_not_grow_with_the_event_count()
    {
        // Same kind/file mix, 200x the events: every retained collection must hold exactly as much.
        foreach (var harness in ProductionHarnesses())
        {
            var small = RetainedCounts(harness.Folded(SyntheticStream(1_000)));
            var large = RetainedCounts(harness.Folded(SyntheticStream(200_000)));

            large.ShouldBe(small, $"{harness.Kind}: the folder's retained state must be O(1) in the event count — if a count grew, some reduction is still accumulating per event");
        }
    }

    [Fact]
    public void Every_collection_a_production_folder_retains_is_observed_by_the_growth_guard()
    {
        // The growth guard compares only the fields RetainedCounts can see, so an unobservable field would shrink the
        // comparison to nothing and still pass. Pin the observed SET by dotted PATH: a new retained collection — one
        // a folder declares itself, or one it hides inside a composed reduction — must either appear here
        // deliberately or fail loudly, never vanish from the O(1) assertion.
        foreach (var harness in ProductionHarnesses())
            RetainedCounts(harness.Folded(SyntheticStream(8))).Keys.OrderBy(n => n, StringComparer.Ordinal).ToArray()
                .ShouldBe(new[] { "_fold._changedFileKeys", "_fold._changedFiles", "_fold._lastTextByKind" },
                    customMessage: $"{harness.Kind}: a retained collection is missing from the growth guard — widen RetainedCounts, or give this folder its own expectation when its reduction legitimately differs, rather than letting it go unobserved");
    }

    [Fact]
    public void A_folder_keeps_only_the_distinct_changed_files_not_one_entry_per_event()
    {
        // The one retained collection that legitimately grows is ChangedFiles — bounded by the number of DISTINCT
        // files touched, never by the number of events. SyntheticStream re-touches the same 3 paths forever.
        foreach (var harness in ProductionHarnesses())
            harness.BuildResult(SyntheticStream(200_000), exitCode: 0).ChangedFiles.Count.ShouldBe(3, $"{harness.Kind} must keep the distinct files, not one entry per event");
    }

    /// <summary>The accumulators the executor holds for a whole run's duration: each production harness's own folder, and the run-facts accumulator it drives alongside them.</summary>
    private static IEnumerable<(string Name, Type Type)> RunLongAccumulators()
    {
        foreach (var harness in ProductionHarnesses()) yield return (harness.Kind, harness.CreateFolder().GetType());

        yield return (nameof(AgentRunFacts), typeof(AgentRunFacts));
    }

    private static IEnumerable<IAgentHarness> ProductionHarnesses() => new IAgentHarness[] { new ClaudeCodeHarness(), new CodexHarness() };

    /// <summary>
    /// Every retained ENUMERABLE field reachable from the accumulator, by dotted field PATH → element count. Walks
    /// TRANSITIVELY: a folder composes its reduction, so counting only its own declared fields would see one object
    /// reference and report nothing retained — the O(1) assertion would pass vacuously. Filters on
    /// <see cref="System.Collections.IEnumerable"/> rather than the non-generic <c>ICollection</c>, which
    /// <c>HashSet&lt;T&gt;</c> does NOT implement — that filter silently dropped the fold's dedupe set from the
    /// comparison, and would drop any future SortedSet / ImmutableArray / generic-only collection the same way, so
    /// the assertion failed OPEN. Counted by enumeration for the same reason.
    /// </summary>
    private static Dictionary<string, int> RetainedCounts(object accumulator)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);

        Walk(accumulator, prefix: "", visited: new HashSet<object>(ReferenceEqualityComparer.Instance), counts);

        return counts;
    }

    /// <summary>Depth-first over the instance's fields. A collection is a LEAF — its elements are the retention, not more fields to walk; anything else non-null and non-scalar is walked into, so a composed reduction cannot hide behind one object reference.</summary>
    private static void Walk(object instance, string prefix, HashSet<object> visited, Dictionary<string, int> counts)
    {
        foreach (var field in Fields(instance.GetType()))
        {
            var value = field.GetValue(instance);
            if (value is null || IsScalar(value.GetType())) continue;

            var path = prefix + field.Name;

            if (value is System.Collections.IEnumerable collection)
            {
                counts[path] = collection.Cast<object>().Count();
                continue;
            }

            if (visited.Add(value)) Walk(value, path + ".", visited, counts);
        }
    }

    /// <summary>Stop the walk at a value that cannot itself declare a retained field. Judged on the RUNTIME type, so a boxed enum reads as an enum rather than as the nullable that declared it — <c>System.Int32</c> declaring an <c>int</c> field is an infinite descent otherwise.</summary>
    private static bool IsScalar(Type type) => type.IsPrimitive || type.IsEnum || type.IsPointer || type == typeof(string) || typeof(Delegate).IsAssignableFrom(type);

    /// <summary>
    /// Every field reachable from this accumulator TYPE whose type is shaped to hold an <see cref="AgentEvent"/>.
    /// TYPE-based, not instance-based, so a field that happens to be null in a probe stream cannot hide.
    ///
    /// <para>The walk descends into a BCL container's CodeSpace-declared type ARGUMENTS, not only into CodeSpace-declared
    /// fields. Stopping at the field's own declared type would fail OPEN for the shape that matters most:
    /// <c>List&lt;SomeWrapper&gt;</c> is declared in System.Private.CoreLib, so a wrapper holding an
    /// <see cref="AgentEvent"/> would never be opened and the guard would report nothing while the retention was real.</para>
    /// </summary>
    private static IEnumerable<string> EventBearingFields(Type type, string prefix = "", HashSet<Type>? visited = null)
    {
        visited ??= new HashSet<Type>();
        if (!visited.Add(type)) yield break;

        foreach (var field in Fields(type))
        {
            if (MentionsEvent(field.FieldType)) yield return $"{prefix}{field.Name}:{field.FieldType.Name}";

            foreach (var carried in CarriedTypes(field.FieldType))
                foreach (var nested in EventBearingFields(carried, $"{prefix}{field.Name}.", visited)) yield return nested;
        }
    }

    [Fact]
    public void The_type_walk_finds_an_event_retained_behind_a_bcl_container()
    {
        // The guard's own failure mode: a List<T> is declared in System.Private.CoreLib, so a walk that stops at the
        // FIELD's declared type never opens T and reports nothing while the retention is real. Pin that it opens the
        // type arguments — otherwise the O(1) claim rests on a guard that passes because it looked nowhere.
        EventBearingFields(typeof(EventHidingBehindABclContainer))
            .ShouldNotBeEmpty("an AgentEvent reachable through a BCL container's type argument must be reported, not walked past");
    }

    /// <summary>The shape the type walk used to be blind to — retention that is real but reachable only through a BCL container's type argument.</summary>
    private sealed class EventHidingBehindABclContainer
    {
        private readonly List<EventWrapper> _wrapped = new();

        public int Count => _wrapped.Count;
    }

    private sealed class EventWrapper
    {
        private readonly AgentEvent? _event;

        public EventWrapper(AgentEvent? value) => _event = value;

        public bool HasValue => _event != null;
    }

    /// <summary>The CodeSpace-declared types a field can actually reach: itself when it is one, plus the element / type-argument types a BCL container carries. A BCL leaf carrying nothing CodeSpace-declared reaches nothing.</summary>
    private static IEnumerable<Type> CarriedTypes(Type type)
    {
        if (IsScalar(type)) yield break;

        if (IsCodeSpaceDeclared(type)) yield return type;

        if (type.IsArray && type.GetElementType() is { } element && !IsScalar(element)) yield return element;

        foreach (var argument in type.GetGenericArguments().Where(a => !IsScalar(a))) yield return argument;
    }

    private static FieldInfo[] Fields(Type type) => type.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

    private static bool IsCodeSpaceDeclared(Type type) => type.Assembly.GetName().Name?.StartsWith("CodeSpace", StringComparison.Ordinal) == true;

    private static bool MentionsEvent(Type type) =>
        type == typeof(AgentEvent) || type.GetGenericArguments().Any(MentionsEvent) || (type.IsArray && MentionsEvent(type.GetElementType()!));

    // ── A third harness, owning a reduction Core knows nothing about ────────────────────────────────

    /// <summary>A harness whose folder keeps a tool-call count NEITHER production harness has. Declared entirely here — the point is that Core needs no edit for it.</summary>
    private sealed class ToolCallCountingHarness : IAgentHarness
    {
        public string Kind => "tool-call-counting";
        public string Version => "test";
        public IReadOnlyList<string> Models { get; } = new[] { "m" };

        public SandboxSpec BuildInvocation(AgentTask task) => new() { Command = "x" };

        public IReadOnlyList<AgentEvent> ParseEvents(string rawLine) => Array.Empty<AgentEvent>();

        public IAgentEventFolder CreateFolder() => new ToolCallCountingFolder();
    }

    /// <summary>The reduction that could not exist without touching a shared type before the ownership inversion: an O(1) counter belonging to exactly one harness.</summary>
    private sealed class ToolCallCountingFolder : IAgentEventFolder
    {
        private int _toolCalls;

        public void Add(AgentEvent normalized)
        {
            if (normalized.Kind == AgentEventKind.ToolCall) _toolCalls++;
        }

        public AgentRunResult BuildResult(AgentRunFacts facts, int exitCode) => new() { Status = AgentRunStatus.Succeeded, ExitReason = "completed", Summary = $"{_toolCalls} tool calls" };
    }

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
