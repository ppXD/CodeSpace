using CodeSpace.Core.Services.Agents;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// The universal invariants every <see cref="IAgentHarness"/> must satisfy, regardless of which CLI it
/// adapts: it advertises a kind / version / models, builds a runnable invocation, NEVER throws on junk
/// input (returns null for non-event lines), and folds exit codes to the right terminal status.
/// Harness-specific parsing (the native-type → <see cref="AgentEventKind"/> table) stays in that
/// harness's own tests; this base is the shared floor every harness inherits by subclassing.
/// </summary>
public abstract class AgentHarnessContractTests
{
    protected abstract IAgentHarness Harness { get; }

    private AgentTask MinimalTask() => new()
    {
        Goal = "do something",
        Harness = Harness.Kind,
        Model = Harness.Models.Count > 0 ? Harness.Models[0] : "model",
        TimeoutSeconds = 600,
    };

    [Fact]
    public void Kind_is_non_empty() => Harness.Kind.ShouldNotBeNullOrWhiteSpace();

    [Fact]
    public void Version_is_non_empty() => Harness.Version.ShouldNotBeNullOrWhiteSpace();

    [Fact]
    public void Advertises_at_least_one_model() => Harness.Models.ShouldNotBeEmpty();

    [Fact]
    public void BuildInvocation_produces_a_runnable_spec()
    {
        var spec = Harness.BuildInvocation(MinimalTask());

        spec.Command.ShouldNotBeNullOrWhiteSpace();
        spec.TimeoutSeconds.ShouldBe(600);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json {")]
    [InlineData("a plain log line, not a structured event")]
    public void ParseEvents_returns_no_events_for_non_event_lines_and_never_throws(string line) =>
        Harness.ParseEvents(line).ShouldBeEmpty();

    [Fact]
    public void BuildResult_maps_exit_zero_to_succeeded() =>
        Harness.BuildResult(Array.Empty<AgentEvent>(), 0).Status.ShouldBe(AgentRunStatus.Succeeded);

    [Fact]
    public void BuildResult_maps_nonzero_exit_to_failed() =>
        Harness.BuildResult(Array.Empty<AgentEvent>(), 1).Status.ShouldBe(AgentRunStatus.Failed);

    /// <summary>A native session-bearing line (Claude's result / Codex's thread.started) + the id it carries — the contract every harness must surface so a rerun can CONTINUE the prior conversation (P3.1a).</summary>
    protected abstract (string Line, string ExpectedId) SessionIdLine { get; }

    [Fact]
    public void BuildResult_surfaces_the_harness_session_id_from_its_session_bearing_line()
    {
        var (line, expectedId) = SessionIdLine;

        Harness.BuildResult(Harness.ParseEvents(line), exitCode: 0).SessionId
            .ShouldBe(expectedId, "every harness must surface its CLI session/thread id so a rerun can CONTINUE the prior conversation");
    }

    /// <summary>
    /// The CONTINUE round-trip that makes conversation-resume coherent for ANY harness implementing the optional
    /// <see cref="IAgentSessionTranscript"/> capability (Rule 7): whatever path its <c>BuildInvocation</c> RESTORES a
    /// transcript to, its capture-locate MUST find on disk. This is generic over the two on-disk shapes — Claude's
    /// computed <c>projects/&lt;cwd&gt;/&lt;id&gt;.jsonl</c> and Codex's globbed <c>sessions/…/rollout-…&lt;id&gt;</c> — because
    /// it asserts the RELATION (restore ⟹ locatable), not a specific layout. A capture/restore key mismatch (the P3
    /// cold-start bug) fails here for every harness. Harnesses without the capability are a clean no-op.
    /// </summary>
    [Fact]
    public void A_restored_session_transcript_is_relocatable_by_the_capture_locator()
    {
        if (Harness is not IAgentSessionTranscript resumable) return;   // the capability is optional

        const string sessionId = "sess-contract-roundtrip";
        var configHome = Path.Combine(Path.GetTempPath(), "cs-harness-contract-" + Guid.NewGuid().ToString("N"));
        var cwd = Path.Combine(configHome, "ws");   // a resolved path the harness keys its layout on (or ignores)
        Directory.CreateDirectory(cwd);

        try
        {
            var continueTask = MinimalTask() with { WorkspaceDirectory = cwd, ResumeFromSessionId = sessionId, RestoredTranscript = "{\"turn\":1}\n" };

            // Materialize the harness's restore ConfigHomeFiles into the config home — what the runner does at launch.
            foreach (var file in Harness.BuildInvocation(continueTask).ConfigHomeFiles)
            {
                var dest = Path.Combine(configHome, file.RelativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.WriteAllText(dest, file.Content);
            }

            var located = resumable.SessionTranscriptRelativePath(configHome, cwd, sessionId);

            located.ShouldNotBeNull("a harness that RESTORES a transcript must re-LOCATE it for capture — else capture+restore key on different paths and a continue cold-starts");
            File.Exists(Path.Combine(configHome, located!)).ShouldBeTrue("the located path resolves to the file the harness restored");
        }
        finally
        {
            try { Directory.Delete(configHome, recursive: true); } catch { /* best-effort temp cleanup */ }
        }
    }
}
