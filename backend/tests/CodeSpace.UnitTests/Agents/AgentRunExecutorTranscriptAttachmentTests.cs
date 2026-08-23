using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Workflows.Artifacts;
using CodeSpace.Core.Services.Workflows.Artifacts.Exceptions;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Failures;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

[Trait("Category", "Unit")]
public sealed class AgentRunExecutorTranscriptAttachmentTests : IDisposable
{
    private readonly string _directory = AgentRunExecutor.TranscriptSpillDirectory(Guid.NewGuid());
    private static readonly AgentRunResult Original = new() { Status = AgentRunStatus.Succeeded, ExitReason = "completed", Summary = "unchanged" };

    [Fact]
    public async Task A_retained_transcript_keeps_the_old_inline_shape_without_requiring_streaming_capability()
    {
        await using var transcript = new AgentTranscriptSpool(_directory, 1_000_000);
        await transcript.AppendLineAsync("small exact π", CancellationToken.None);
        var store = new WholeOnlyArtifactStore();

        var attached = await AgentRunExecutor.AttachTranscriptAsync(store, Original, Guid.NewGuid(), transcript, CancellationToken.None);

        attached.Transcript.ShouldBe($"small exact π{Environment.NewLine}");
        attached.TranscriptArtifactId.ShouldBeNull();
        attached.Summary.ShouldBe(Original.Summary);
        store.WholePutCalls.ShouldBe(0, "the <=threshold path remains the old inline result and does not touch artifact storage");
    }

    [Fact]
    public async Task A_spilled_transcript_uses_only_the_stream_face_and_preserves_result_and_artifact_identity()
    {
        await using var transcript = await SpilledAsync();
        var store = new RecordingStreamingStore();

        var attached = await AgentRunExecutor.AttachTranscriptAsync(store, Original, Guid.NewGuid(), transcript, CancellationToken.None);

        attached.Transcript.ShouldBe("");
        attached.TranscriptArtifactId.ShouldBe(store.ArtifactId);
        attached.Summary.ShouldBe(Original.Summary);
        store.WholePutCalls.ShouldBe(0, "a spilled transcript must never fall through the whole-memory IArtifactStore face");
        store.StreamPutCalls.ShouldBe(1);
        store.FirstRead.ShouldBe(store.SecondRead, "identity admission and placement can reopen the sealed source without drift");
        store.FirstRead.ShouldBe(System.Text.Encoding.UTF8.GetBytes($"long enough to spill{Environment.NewLine}"));
        transcript.OpenReadCount.ShouldBe(2);
    }

    [Fact]
    public async Task A_spilled_transcript_fails_typed_when_the_store_lacks_streaming_capability()
    {
        await using var transcript = await SpilledAsync();
        var store = new WholeOnlyArtifactStore();

        var failure = await Should.ThrowAsync<ArtifactStreamingWriteUnavailableException>(() =>
            AgentRunExecutor.AttachTranscriptAsync(store, Original, Guid.NewGuid(), transcript, CancellationToken.None));

        failure.RequiredCapability.ShouldBe(typeof(IArtifactStreamStore));
        ((IFailure)failure).Kind.ShouldBe(FailureKind.Internal);
        ((IFailure)failure).Code.ShouldBe(FailureCodes.Internal);
        store.WholePutCalls.ShouldBe(0, "missing capability is explicit; it must not trigger the old ReadAll + whole-byte fallback");
    }

    [Fact]
    public async Task Storage_cancellation_propagates_without_unsealing_or_deleting_the_spilled_source_early()
    {
        await using var transcript = await SpilledAsync();
        var store = new RecordingStreamingStore { Cancel = true };

        await Should.ThrowAsync<OperationCanceledException>(() =>
            AgentRunExecutor.AttachTranscriptAsync(store, Original, Guid.NewGuid(), transcript, CancellationToken.None));

        store.Request.ShouldNotBeNull();
        System.Text.Encoding.UTF8.GetString(await RecordingStreamingStore.ReadAsync(store.Request!.Source)).ShouldBe($"long enough to spill{Environment.NewLine}",
            "Attach awaits the streaming store before the executor scope disposes the spool; cancellation cannot turn it into an empty record");
    }

    [Fact]
    public void Spilled_attachment_contains_no_whole_payload_escape_hatch()
    {
        var source = File.ReadAllText(Path.Combine(FindRepoRoot(), "backend", "src", "CodeSpace.Core", "Services", "Agents", "AgentRunExecutor.cs"));
        var start = source.IndexOf("internal static async Task<AgentRunResult> AttachTranscriptAsync", StringComparison.Ordinal);
        var end = source.IndexOf("/// <summary>The revise-round announcement", start, StringComparison.Ordinal);
        start.ShouldBeGreaterThanOrEqualTo(0);
        end.ShouldBeGreaterThan(start);
        var attachment = source[start..end];

        attachment.ShouldContain(nameof(IArtifactStreamStore), Case.Sensitive);
        attachment.ShouldNotContain("ReadAllAsync", Case.Sensitive, "spilled completion must not allocate the transcript again");
        attachment.ShouldNotContain("ToArray", Case.Sensitive);
        attachment.ShouldNotContain("MemoryStream", Case.Sensitive);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); } catch { }
    }

    private async Task<AgentTranscriptSpool> SpilledAsync()
    {
        var transcript = new AgentTranscriptSpool(_directory, 8);
        await transcript.AppendLineAsync("long enough to spill", CancellationToken.None);
        return transcript;
    }

    private static string FindRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !Directory.Exists(Path.Combine(current.FullName, "backend", "src", "CodeSpace.Core"))) current = current.Parent;
        return current?.FullName ?? throw new DirectoryNotFoundException("Could not locate repository root from test output.");
    }

    private class WholeOnlyArtifactStore : IArtifactStore
    {
        public int WholePutCalls { get; private set; }
        public Task<Guid> PutAsync(Guid teamId, ReadOnlyMemory<byte> bytes, string contentType, CancellationToken cancellationToken)
        {
            WholePutCalls++;
            return Task.FromResult(Guid.NewGuid());
        }
        public Task<ArtifactBytes?> GetBytesAsync(Guid teamId, Guid artifactId, CancellationToken cancellationToken) => Task.FromResult<ArtifactBytes?>(null);
        public Task<ArtifactMetadata?> GetMetadataAsync(Guid teamId, Guid artifactId, CancellationToken cancellationToken) => Task.FromResult<ArtifactMetadata?>(null);
    }

    private sealed class RecordingStreamingStore : WholeOnlyArtifactStore, IArtifactStreamStore
    {
        public Guid ArtifactId { get; } = Guid.NewGuid();
        public int StreamPutCalls { get; private set; }
        public ArtifactStreamWriteRequest? Request { get; private set; }
        public byte[]? FirstRead { get; private set; }
        public byte[]? SecondRead { get; private set; }
        public bool Cancel { get; init; }

        public async Task<Guid> PutAsync(ArtifactStreamWriteRequest request, CancellationToken cancellationToken)
        {
            StreamPutCalls++;
            Request = request;
            if (Cancel) throw new OperationCanceledException(cancellationToken);
            FirstRead = await ReadAsync(request.Source);
            SecondRead = await ReadAsync(request.Source);
            FirstRead.LongLength.ShouldBe(request.Source.LengthBytes);
            return ArtifactId;
        }

        internal static async Task<byte[]> ReadAsync(IArtifactWriteSource source)
        {
            await using var content = await source.OpenReadAsync(CancellationToken.None);
            using var output = new MemoryStream();
            await content.CopyToAsync(output);
            return output.ToArray();
        }
    }
}
