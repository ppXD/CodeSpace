using System.Text.Json;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// D3a: pins that the durable raw <see cref="AgentRunResult.Transcript"/> + its offloaded
/// <see cref="AgentRunResult.TranscriptArtifactId"/> are part of the PERSISTED result_jsonb contract —
/// they round-trip through the exact <see cref="AgentJson.Options"/> the run record is serialized with.
/// A regression that drops the field from serialization (e.g. a stray [JsonIgnore], or a missing default)
/// would silently lose the "replay the exact session" record; this fails loudly instead.
/// </summary>
[Trait("Category", "Unit")]
public class AgentRunResultTranscriptTests
{
    [Fact]
    public void Round_trips_an_inline_transcript_through_the_persistence_serializer()
    {
        var original = new AgentRunResult
        {
            Status = AgentRunStatus.Succeeded,
            ExitReason = "completed",
            Transcript = "line one\n\nline three\n",   // a blank middle line — faithfulness includes dropped lines
        };

        var roundTripped = JsonSerializer.Deserialize<AgentRunResult>(
            JsonSerializer.Serialize(original, AgentJson.Options), AgentJson.Options)!;

        roundTripped.Transcript.ShouldBe(original.Transcript, "the inline transcript survives serialize→deserialize byte-for-byte");
        roundTripped.TranscriptArtifactId.ShouldBeNull("an inline transcript carries no artifact ref");
    }

    [Fact]
    public void Round_trips_an_offloaded_transcript_ref_with_the_inline_cleared()
    {
        var artifactId = Guid.NewGuid();
        var original = new AgentRunResult
        {
            Status = AgentRunStatus.Succeeded,
            ExitReason = "completed",
            Transcript = "",                       // cleared once offloaded
            TranscriptArtifactId = artifactId,
        };

        var roundTripped = JsonSerializer.Deserialize<AgentRunResult>(
            JsonSerializer.Serialize(original, AgentJson.Options), AgentJson.Options)!;

        roundTripped.Transcript.ShouldBe("", "the offloaded transcript is cleared inline");
        roundTripped.TranscriptArtifactId.ShouldBe(artifactId, "the ref to the full transcript survives the round-trip");
    }

    [Fact]
    public void Defaults_to_an_empty_transcript_and_no_ref()
    {
        // A result the harness builds without a transcript (e.g. the executor never attached one) must
        // deserialize to the safe empty default, never null — consumers index it without a null guard.
        var result = new AgentRunResult { Status = AgentRunStatus.Succeeded, ExitReason = "completed" };

        result.Transcript.ShouldBe("");
        result.TranscriptArtifactId.ShouldBeNull();
    }

    [Fact]
    public void Round_trips_the_per_file_diffstat_through_the_persistence_serializer()
    {
        var original = new AgentRunResult
        {
            Status = AgentRunStatus.Succeeded,
            ExitReason = "completed",
            ChangedFiles = new[] { "src/auth.ts", "assets/logo.png" },
            FileStats = new[]
            {
                new FileDiffStat("src/auth.ts", 12, 3),
                new FileDiffStat("assets/logo.png", null, null),   // a binary file's counts persist as null, not 0
            },
        };

        var roundTripped = JsonSerializer.Deserialize<AgentRunResult>(
            JsonSerializer.Serialize(original, AgentJson.Options), AgentJson.Options)!;

        roundTripped.FileStats.Count.ShouldBe(2, "the per-file diffstat is part of the persisted result_jsonb contract");
        roundTripped.FileStats[0].ShouldBe(new FileDiffStat("src/auth.ts", 12, 3));
        roundTripped.FileStats[1].ShouldBe(new FileDiffStat("assets/logo.png", null, null), "a binary file's null counts survive — not coerced to 0");
    }

    [Fact]
    public void Defaults_to_an_empty_diffstat_never_null()
    {
        var result = JsonSerializer.Deserialize<AgentRunResult>(
            JsonSerializer.Serialize(new AgentRunResult { Status = AgentRunStatus.Succeeded, ExitReason = "completed" }, AgentJson.Options), AgentJson.Options)!;

        result.FileStats.ShouldBeEmpty("a result with no workspace defaults to an empty diffstat, never null — consumers index it without a guard");
    }
}
