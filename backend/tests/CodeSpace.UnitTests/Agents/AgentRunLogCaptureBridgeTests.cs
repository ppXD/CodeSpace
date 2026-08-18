using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.AgentRunLogging;
using CodeSpace.Core.Services.Agents.Sandbox;
using CodeSpace.Messages.Agents;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

[Trait("Category", "Unit")]
public sealed class AgentRunLogCaptureBridgeTests
{
    private static readonly Guid TeamId = Guid.NewGuid();
    private static readonly Guid RunId = Guid.NewGuid();
    private static readonly Guid ActorId = Guid.NewGuid();
    private static readonly Guid ProfileId = Guid.NewGuid();

    [Fact]
    public async Task Captures_bounded_large_stdout_and_stderr_redacts_across_segments_and_preserves_result_identity()
    {
        var logs = new FakeLogService { CurrentFence = 1 };
        var source = new FakeLogSource();
        var secret = "sk-boundary-secret";
        var stdout = Enumerable.Repeat((byte)'a', 2 * 1024 * 1024 + 217).ToArray();
        Encoding.UTF8.GetBytes(secret).CopyTo(stdout, 1024 * 1024 - 4);
        var stderr = new byte[] { 0xff, 0xfe, (byte)'E', (byte)'R', (byte)'R', 0x80 };
        source.Set("stdout", stdout);
        source.Set("stderr", stderr);
        var bridge = Bridge(logs);
        var expected = Result();

        var capture = await bridge.OpenAsync(Request(source, fence: 1, Guid.NewGuid(), new SecretRedactor(new[] { secret })), CancellationToken.None);
        var observed = await capture.ObserveAsync((_, _) => Task.FromResult(expected), CancellationToken.None);
        await bridge.CompleteRunAsync(TeamId, RunId, 1, CancellationToken.None);

        observed.ShouldBeSameAs(expected, "shadow capture cannot replace or reinterpret the harness result");
        logs.Bytes(AgentRunLogKinds.StandardOutput).ShouldBe(new SecretRedactor(new[] { secret }).CreateUtf8Stream().Transform(stdout, final: true).Bytes.ToArray());
        logs.Bytes(AgentRunLogKinds.StandardError).ShouldBe(stderr);
        logs.AppendSizes.ShouldAllBe(size => size <= 1024 * 1024);
        logs.AppendSizes.Count.ShouldBeLessThan(8, "multi-megabyte output is batched, never written per token or line");
        logs.Heads.ShouldAllBe(head => head.Metadata.State == AgentRunLogStreamState.Completed && head.CaptureFinalizedAt != null);
        logs.Heads.ShouldAllBe(head => head.Metadata.ContentType == AgentRunLogRepresentations.PlainTextContentType && head.Metadata.ContentEncoding == AgentRunLogRepresentations.Utf8ContentEncoding);
    }

    [Fact]
    public async Task Two_spools_under_one_run_and_fence_get_distinct_sessions_and_one_contiguous_stream()
    {
        var logs = new FakeLogService { CurrentFence = 7 };
        var bridge = Bridge(logs);
        var firstSource = new FakeLogSource();
        firstSource.Set("stdout", "first"u8.ToArray());
        firstSource.Set("stderr", []);
        var firstSession = Guid.NewGuid();
        var first = await bridge.OpenAsync(Request(firstSource, 7, firstSession), CancellationToken.None);
        await first.ObserveAsync((_, _) => Task.FromResult(Result()), CancellationToken.None);

        var secondSource = new FakeLogSource();
        secondSource.Set("stdout", "second"u8.ToArray());
        secondSource.Set("stderr", []);
        var secondSession = Guid.NewGuid();
        var second = await bridge.OpenAsync(Request(secondSource, 7, secondSession), CancellationToken.None);
        await second.ObserveAsync((_, _) => Task.FromResult(Result()), CancellationToken.None);

        logs.Bytes(AgentRunLogKinds.StandardOutput).ShouldBe("firstsecond"u8.ToArray());
        logs.OpenedSessions.ShouldContain(firstSession);
        logs.OpenedSessions.ShouldContain(secondSession);
        logs.OpenedSessions.Distinct().Count().ShouldBe(2);
        logs.SourceBasesBySession[(AgentRunLogKinds.StandardOutput, firstSession)].ShouldBe(0);
        logs.SourceBasesBySession[(AgentRunLogKinds.StandardOutput, secondSession)].ShouldBe(5);
    }

    [Fact]
    public async Task Cancelled_observer_leaves_open_source_and_higher_fence_reattach_rereads_a_safe_secret_boundary_without_duplicate_or_gap()
    {
        var logs = new FakeLogService { CurrentFence = 1 };
        var bridge = Bridge(logs);
        var source = new FakeLogSource();
        var secret = "cross-restart-secret";
        var redactor = new SecretRedactor(new[] { secret });
        var prefix = Enumerable.Repeat((byte)'p', 300 * 1024).Concat(Encoding.UTF8.GetBytes(secret[..7])).ToArray();
        source.Set("stdout", prefix);
        source.Set("stderr", []);
        var sessionId = Guid.NewGuid();
        var first = await bridge.OpenAsync(Request(source, 1, sessionId, redactor), CancellationToken.None);
        using (var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(600)))
        {
            await Should.ThrowAsync<OperationCanceledException>(() => first.ObserveAsync(async (_, token) => { await Task.Delay(Timeout.InfiniteTimeSpan, token); return Result(); }, cts.Token));
        }
        logs.Heads.Single(head => head.Metadata.StreamKind == AgentRunLogKinds.StandardOutput).CaptureFinalizedAt.ShouldBeNull();

        var suffix = Encoding.UTF8.GetBytes(secret[7..]).Concat("tail"u8.ToArray()).ToArray();
        source.Set("stdout", prefix.Concat(suffix).ToArray());
        logs.CurrentFence = 2;
        var expected = Result();
        var second = await bridge.OpenAsync(Request(source, 2, sessionId, redactor), CancellationToken.None);
        var observed = await second.ObserveAsync((_, _) => Task.FromResult(expected), CancellationToken.None);

        observed.ShouldBeSameAs(expected);
        var completeSource = prefix.Concat(suffix).ToArray();
        logs.Bytes(AgentRunLogKinds.StandardOutput).ShouldBe(redactor.CreateUtf8Stream().Transform(completeSource, final: true).Bytes.ToArray());
        logs.Heads.Single(head => head.Metadata.StreamKind == AgentRunLogKinds.StandardOutput).CaptureFinalizedAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task Stale_fence_and_deterministic_corruption_never_change_harness_result_and_failure_is_durable()
    {
        var staleLogs = new FakeLogService { CurrentFence = 2 };
        var source = new FakeLogSource();
        source.Set("stdout", "ignored"u8.ToArray());
        source.Set("stderr", []);
        var expected = Result();
        var stale = await Bridge(staleLogs).OpenAsync(Request(source, 1, Guid.NewGuid()), CancellationToken.None);
        (await stale.ObserveAsync((_, _) => Task.FromResult(expected), CancellationToken.None)).ShouldBeSameAs(expected);
        staleLogs.AppendSizes.ShouldBeEmpty();

        var failedLogs = new FakeLogService { CurrentFence = 1, FailAppend = true };
        var failedBridge = Bridge(failedLogs);
        var failed = await failedBridge.OpenAsync(Request(source, 1, Guid.NewGuid()), CancellationToken.None);
        (await failed.ObserveAsync((_, _) => Task.FromResult(expected), CancellationToken.None)).ShouldBeSameAs(expected);
        failedLogs.Heads.Single(head => head.Metadata.StreamKind == AgentRunLogKinds.StandardOutput).Metadata.State.ShouldBe(AgentRunLogStreamState.CaptureFailed);
    }

    [Fact]
    public async Task Observer_exception_is_rethrown_unchanged_and_unfinalized_stream_can_never_be_completed()
    {
        var logs = new FakeLogService { CurrentFence = 1 };
        var source = new FakeLogSource();
        source.Set("stdout", "partial"u8.ToArray());
        source.Set("stderr", []);
        var bridge = Bridge(logs);
        var capture = await bridge.OpenAsync(Request(source, 1, Guid.NewGuid()), CancellationToken.None);
        var expected = new InvalidOperationException("observer failed");

        var observed = await Should.ThrowAsync<InvalidOperationException>(() => capture.ObserveAsync((_, _) => Task.FromException<SandboxResult>(expected), CancellationToken.None));
        observed.ShouldBeSameAs(expected);
        await bridge.CompleteRunAsync(TeamId, RunId, 1, CancellationToken.None);
        logs.Heads.ShouldAllBe(head => head.Metadata.State == AgentRunLogStreamState.CaptureFailed);
        logs.CompletedCount.ShouldBe(0);
    }

    [Fact]
    public async Task Storage_policy_resolution_exception_is_a_durable_typed_gap_and_does_not_change_the_result()
    {
        var logs = new FakeLogService { CurrentFence = 1 };
        var source = new FakeLogSource();
        source.Set("stdout", "must-not-be-read"u8.ToArray());
        source.Set("stderr", []);
        var bridge = new AgentRunLogCaptureBridge(logs, new ThrowingStorageResolver(), new FakeRecoveryService(), NullLogger<AgentRunLogCaptureBridge>.Instance);
        var expected = Result();

        var capture = await bridge.OpenAsync(Request(source, 1, Guid.NewGuid()), CancellationToken.None);
        var observed = await capture.ObserveAsync((_, _) => Task.FromResult(expected), CancellationToken.None);

        observed.ShouldBeSameAs(expected);
        logs.AppendSizes.ShouldBeEmpty();
        logs.Heads.ShouldAllBe(head => head.Metadata.State == AgentRunLogStreamState.CaptureFailed && head.Metadata.ErrorCode == "storage-profile-resolution-failed");
    }

    [Fact]
    public async Task Missing_storage_route_is_durable_capture_health_and_does_not_change_the_harness_result()
    {
        var logs = new FakeLogService { CurrentFence = 1 };
        var source = new FakeLogSource();
        source.Set("stdout", "must-not-be-read"u8.ToArray());
        source.Set("stderr", []);
        var bridge = new AgentRunLogCaptureBridge(logs, new UnavailableStorageResolver(AgentRunLogStorageProblemCode.Missing), new FakeRecoveryService(), NullLogger<AgentRunLogCaptureBridge>.Instance);
        var expected = Result();

        var capture = await bridge.OpenAsync(Request(source, 1, Guid.NewGuid()), CancellationToken.None);
        var observed = await capture.ObserveAsync((_, _) => Task.FromResult(expected), CancellationToken.None);

        observed.ShouldBeSameAs(expected);
        logs.AppendSizes.ShouldBeEmpty();
        logs.Heads.ShouldAllBe(head => head.Metadata.State == AgentRunLogStreamState.CaptureFailed && head.Metadata.ErrorCode == "storage-profile-missing");
    }

    [Fact]
    public async Task Explicit_unreadable_source_gap_is_durable_without_reading_any_native_bytes()
    {
        var logs = new FakeLogService { CurrentFence = 3 };
        var source = new FakeLogSource();
        source.Set("stdout", "credential-secret"u8.ToArray());
        source.Set("stderr", []);
        var sessionId = Guid.NewGuid();
        var bridge = Bridge(logs);

        await bridge.RecordGapAsync(new AgentRunLogCaptureGapRequest
        {
            TeamId = TeamId, AgentRunId = RunId, WorkerFenceEpoch = 3,
            Handle = Request(source, 3, sessionId).Handle, Source = source,
            ErrorCode = "redactor-unavailable", ErrorMessage = "The redactor was unavailable.",
        }, CancellationToken.None);

        logs.AppendSizes.ShouldBeEmpty();
        logs.Heads.ShouldAllBe(head => head.Metadata.State == AgentRunLogStreamState.CaptureFailed && head.Metadata.ErrorCode == "redactor-unavailable");
    }

    [Fact]
    public async Task Capture_health_reloads_the_monotonic_head_after_a_concurrent_revision_and_is_not_silently_lost()
    {
        var logs = new FakeLogService { CurrentFence = 1, ConcurrentFailCaptureOnce = true };
        var source = new FakeLogSource();
        source.Set("stdout", []);
        source.Set("stderr", []);
        var bridge = new AgentRunLogCaptureBridge(logs, new ThrowingStorageResolver(), new FakeRecoveryService(), NullLogger<AgentRunLogCaptureBridge>.Instance);

        await bridge.OpenAsync(Request(source, 1, Guid.NewGuid()), CancellationToken.None);

        logs.Heads.ShouldAllBe(head => head.Metadata.State == AgentRunLogStreamState.CaptureFailed);
        logs.FailCaptureCalls.ShouldBeGreaterThan(2, "one stream's first CAS lost, then retried from its durable head; the other stream also recorded its gap");
    }

    [Fact]
    public async Task Blocking_capture_backend_is_cancelled_by_one_total_shadow_budget_without_changing_the_sandbox_result()
    {
        var logs = new FakeLogService { CurrentFence = 1, BlockAppend = true };
        var source = new FakeLogSource();
        source.Set("stdout", Enumerable.Repeat((byte)'x', 300 * 1024).ToArray());
        source.Set("stderr", []);
        var bridge = new AgentRunLogCaptureBridge(logs, new ReadyStorageResolver(), new FakeRecoveryService(), NullLogger<AgentRunLogCaptureBridge>.Instance, new AgentRunLogCaptureBridgeOptions(TimeSpan.FromMilliseconds(40), TimeSpan.FromMilliseconds(150)));
        var expected = Result();
        var watch = Stopwatch.StartNew();

        var session = await bridge.OpenAsync(Request(source, 1, Guid.NewGuid()), CancellationToken.None);
        var observed = await session.ObserveAsync((_, _) => Task.FromResult(expected), CancellationToken.None);

        watch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(2), "one total finalization budget bounds a provider that never completes, instead of N segments multiplying the provider default");
        observed.ShouldBeSameAs(expected);
        logs.Heads.ShouldAllBe(head => head.Metadata.State == AgentRunLogStreamState.Open && head.CaptureFinalizedAt == null, "timeout remains durably reconcilable Open state, never incomplete-but-Completed");
        logs.ObservedOperationTimeout.ShouldBe(TimeSpan.FromMilliseconds(40));
    }

    [Fact]
    public async Task Final_drain_retries_one_transient_append_and_preserves_the_complete_source()
    {
        var logs = new FakeLogService { CurrentFence = 1, RetryableAppendFailures = 1 };
        var source = new FakeLogSource();
        source.Set("stdout", "eventually-durable"u8.ToArray());
        source.Set("stderr", []);
        var bridge = new AgentRunLogCaptureBridge(logs, new ReadyStorageResolver(), new FakeRecoveryService(), NullLogger<AgentRunLogCaptureBridge>.Instance,
            new AgentRunLogCaptureBridgeOptions(TimeSpan.FromMilliseconds(40), TimeSpan.FromMilliseconds(300)));
        var expected = Result();

        var observed = await (await bridge.OpenAsync(Request(source, 1, Guid.NewGuid()), CancellationToken.None))
            .ObserveAsync((_, _) => Task.FromResult(expected), CancellationToken.None);

        observed.ShouldBeSameAs(expected);
        logs.AppendAttempts.ShouldBeGreaterThanOrEqualTo(2);
        logs.Bytes(AgentRunLogKinds.StandardOutput).ShouldBe("eventually-durable"u8.ToArray());
        logs.Heads.Single(value => value.Metadata.StreamKind == AgentRunLogKinds.StandardOutput).CaptureFinalizedAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task Final_drain_budget_exhaustion_on_transient_append_leaves_an_open_recoverable_stream()
    {
        var logs = new FakeLogService { CurrentFence = 1, RetryableAppendFailures = int.MaxValue };
        var recovery = new FakeRecoveryService();
        var source = new FakeLogSource();
        source.Set("stdout", "still-in-native-spool"u8.ToArray());
        source.Set("stderr", []);
        var bridge = new AgentRunLogCaptureBridge(logs, new ReadyStorageResolver(), recovery, NullLogger<AgentRunLogCaptureBridge>.Instance,
            new AgentRunLogCaptureBridgeOptions(TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(120)));
        var expected = Result();

        var observed = await (await bridge.OpenAsync(Request(source, 1, Guid.NewGuid()), CancellationToken.None))
            .ObserveAsync((_, _) => Task.FromResult(expected), CancellationToken.None);

        observed.ShouldBeSameAs(expected);
        var stdout = logs.Heads.Single(value => value.Metadata.StreamKind == AgentRunLogKinds.StandardOutput);
        stdout.Metadata.State.ShouldBe(AgentRunLogStreamState.Open);
        stdout.CaptureFinalizedAt.ShouldBeNull();
        stdout.Metadata.ErrorCode.ShouldBeNull();
        recovery.Declarations.ShouldHaveSingleItem("the durable intent remains available to terminal recovery");
    }

    [Fact]
    public async Task Transient_no_data_never_authorizes_finalization_and_returns_on_the_shadow_budget()
    {
        var logs = new FakeLogService { CurrentFence = 1 };
        var source = new FakeLogSource { EmitEndOfSource = false };
        source.Set("stdout", []);
        source.Set("stderr", []);
        var bridge = new AgentRunLogCaptureBridge(logs, new ReadyStorageResolver(), new FakeRecoveryService(), NullLogger<AgentRunLogCaptureBridge>.Instance, new AgentRunLogCaptureBridgeOptions(TimeSpan.FromMilliseconds(40), TimeSpan.FromMilliseconds(100)));
        var expected = Result();

        var observed = await (await bridge.OpenAsync(Request(source, 1, Guid.NewGuid()), CancellationToken.None))
            .ObserveAsync((_, _) => Task.FromResult(expected), CancellationToken.None);

        observed.ShouldBeSameAs(expected);
        logs.Heads.ShouldAllBe(head => head.Metadata.State == AgentRunLogStreamState.Open && head.CaptureFinalizedAt == null);
    }

    [Fact]
    public async Task Expected_streams_are_declared_even_when_every_stream_open_fails()
    {
        var logs = new FakeLogService { CurrentFence = 1, RejectAllOpens = true };
        var recovery = new FakeRecoveryService();
        var source = new FakeLogSource();
        source.Set("stdout", []);
        source.Set("stderr", []);
        var bridge = new AgentRunLogCaptureBridge(logs, new ReadyStorageResolver(), recovery, NullLogger<AgentRunLogCaptureBridge>.Instance);

        var session = await bridge.OpenAsync(Request(source, 1, Guid.NewGuid()), CancellationToken.None);
        var expected = Result();
        (await session.ObserveAsync((_, _) => Task.FromResult(expected), CancellationToken.None)).ShouldBeSameAs(expected);

        logs.Heads.ShouldBeEmpty("a fully failed Open cannot pretend a zero-byte stream existed");
        var declaration = recovery.Declarations.ShouldHaveSingleItem();
        declaration.Streams.Select(value => value.StreamKind).ShouldBe(new[] { AgentRunLogKinds.StandardOutput, AgentRunLogKinds.StandardError });
    }

    [Fact]
    public async Task Total_intent_database_outage_is_an_honest_non_durable_boundary_and_never_changes_the_harness_result()
    {
        var logs = new FakeLogService { CurrentFence = 1 };
        var recovery = new FakeRecoveryService { ThrowOnDeclare = true };
        var source = new FakeLogSource();
        source.Set("stdout", []);
        source.Set("stderr", []);
        var bridge = new AgentRunLogCaptureBridge(logs, new ReadyStorageResolver(), recovery, NullLogger<AgentRunLogCaptureBridge>.Instance);
        var expected = Result();

        var observed = await (await bridge.OpenAsync(Request(source, 1, Guid.NewGuid()), CancellationToken.None))
            .ObserveAsync((_, _) => Task.FromResult(expected), CancellationToken.None);

        observed.ShouldBeSameAs(expected);
        recovery.DeclarationAttempts.ShouldBe(1);
        recovery.Declarations.ShouldBeEmpty("a database outage cannot truthfully fabricate a durable capture intent");
    }

    [Fact]
    public async Task Typed_intent_rejection_never_opens_a_stream_outside_its_durable_expected_identity()
    {
        var logs = new FakeLogService { CurrentFence = 1 };
        var recovery = new FakeRecoveryService { RejectedDeclaration = AgentRunLogCaptureDeclarationProblem.IdentityConflict };
        var source = new FakeLogSource();
        source.Set("stdout", "must-not-open"u8.ToArray());
        source.Set("stderr", []);
        var bridge = new AgentRunLogCaptureBridge(logs, new ReadyStorageResolver(), recovery, NullLogger<AgentRunLogCaptureBridge>.Instance);
        var expected = Result();

        var observed = await (await bridge.OpenAsync(Request(source, 1, Guid.NewGuid()), CancellationToken.None))
            .ObserveAsync((_, _) => Task.FromResult(expected), CancellationToken.None);

        observed.ShouldBeSameAs(expected);
        recovery.DeclarationAttempts.ShouldBe(1);
        logs.Heads.ShouldBeEmpty("a typed declaration rejection must fail closed before any stream is opened");
    }

    [Fact]
    public async Task A_truncated_source_terminalizes_its_stream_as_Truncated_with_every_captured_byte_kept()
    {
        // The spool size cap drops bytes the agent wrote. That loss must land as the FIRST-CLASS Truncated capture
        // state (already modelled + already surfaced by the read API), never be laundered into a clean Completed.
        var logs = new FakeLogService { CurrentFence = 1 };
        var source = new FakeLogSource { TruncatedAtEnd = true };
        source.Set("stdout", "capped-head"u8.ToArray());
        source.Set("stderr", []);
        var bridge = Bridge(logs);

        await (await bridge.OpenAsync(Request(source, 1, Guid.NewGuid()), CancellationToken.None)).ObserveAsync((_, _) => Task.FromResult(Result()), CancellationToken.None);
        await bridge.CompleteRunAsync(TeamId, RunId, 1, CancellationToken.None);

        logs.Bytes(AgentRunLogKinds.StandardOutput).ShouldBe("capped-head"u8.ToArray(), "everything the source DID yield is still captured — truncation loses the tail, not the head");
        logs.Heads.ShouldAllBe(head => head.Metadata.State == AgentRunLogStreamState.Truncated, "a capped source is Truncated, never Completed");
        logs.Heads.ShouldAllBe(head => head.Metadata.ErrorCode == "source-truncated");
        logs.CompletedCount.ShouldBe(0, "terminalization must not also claim a complete capture for a source it knows was cut short");
    }

    private static AgentRunLogCaptureBridge Bridge(FakeLogService logs) => new(logs, new ReadyStorageResolver(), new FakeRecoveryService(), NullLogger<AgentRunLogCaptureBridge>.Instance);

    private static AgentRunLogCaptureOpenRequest Request(FakeLogSource source, long fence, Guid sessionId, SecretRedactor? redactor = null) => new()
    {
        TeamId = TeamId, AgentRunId = RunId, ActorId = ActorId, WorkerFenceEpoch = fence,
        Handle = new SandboxHandle { Kind = "fake", ProcessId = 1, SpoolDirectory = "/opaque", Deadline = DateTimeOffset.MaxValue, AgentRunLogCaptureSessionId = sessionId },
        Source = source, Redactor = redactor ?? SecretRedactor.None,
    };

    private static SandboxResult Result() => new() { Status = SandboxStatus.Success, ExitCode = 0, Stdout = "legacy", Stderr = "legacy-error" };

    private sealed class ReadyStorageResolver : IAgentRunLogStorageResolver
    {
        public Task<AgentRunLogStorageResolution> ResolveAsync(Guid teamId, CancellationToken cancellationToken) =>
            Task.FromResult<AgentRunLogStorageResolution>(new AgentRunLogStorageResolution.Ready(ProfileId, 1));
    }

    private sealed class ThrowingStorageResolver : IAgentRunLogStorageResolver
    {
        public Task<AgentRunLogStorageResolution> ResolveAsync(Guid teamId, CancellationToken cancellationToken) => throw new IOException("resolver backend failed");
    }

    private sealed class UnavailableStorageResolver(AgentRunLogStorageProblemCode code) : IAgentRunLogStorageResolver
    {
        public Task<AgentRunLogStorageResolution> ResolveAsync(Guid teamId, CancellationToken cancellationToken) =>
            Task.FromResult<AgentRunLogStorageResolution>(new AgentRunLogStorageResolution.Unavailable(code));
    }

    private sealed class FakeRecoveryService : IAgentRunLogCaptureRecoveryService
    {
        public bool ThrowOnDeclare { get; init; }
        public AgentRunLogCaptureDeclarationProblem? RejectedDeclaration { get; init; }
        public int DeclarationAttempts { get; private set; }
        public List<AgentRunLogCaptureDeclarationRequest> Declarations { get; } = [];

        public Task<AgentRunLogCaptureDeclarationResult> DeclareAsync(AgentRunLogCaptureDeclarationRequest request, CancellationToken cancellationToken)
        {
            DeclarationAttempts++;
            if (ThrowOnDeclare) throw new IOException("capture-intent database unavailable");
            if (RejectedDeclaration is { } problem)
                return Task.FromResult<AgentRunLogCaptureDeclarationResult>(new AgentRunLogCaptureDeclarationResult.Rejected(problem));
            Declarations.Add(request);
            return Task.FromResult<AgentRunLogCaptureDeclarationResult>(new AgentRunLogCaptureDeclarationResult.Declared(request.Streams.Count, 0));
        }

        public Task<AgentRunLogCaptureRecoverySummary> ReconcileAsync(CancellationToken cancellationToken) => Task.FromResult(new AgentRunLogCaptureRecoverySummary(0, 0, 0, 0, 0, 0));
    }

    private sealed class FakeLogSource : ISandboxDurableLogSource
    {
        private readonly ConcurrentDictionary<string, byte[]> _sources = new(StringComparer.Ordinal);
        public bool EmitEndOfSource { get; init; } = true;
        public bool TruncatedAtEnd { get; init; }

        public IReadOnlyList<SandboxDurableLogDescriptor> DescribeLogs(SandboxHandle handle) =>
        [
            new("stdout", AgentRunLogKinds.StandardOutput, AgentRunLogRepresentations.PlainTextContentType, AgentRunLogRepresentations.Utf8ContentEncoding, "fake-spool/v1"),
            new("stderr", AgentRunLogKinds.StandardError, AgentRunLogRepresentations.PlainTextContentType, AgentRunLogRepresentations.Utf8ContentEncoding, "fake-spool/v1"),
        ];

        public Task<SandboxDurableLogReadResult> ReadAsync(SandboxDurableLogReadRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_sources.TryGetValue(request.SourceKey, out var bytes))
                return Task.FromResult<SandboxDurableLogReadResult>(new SandboxDurableLogReadResult.Unavailable(new SandboxDurableLogProblem(SandboxDurableLogProblemCode.SourceMissing)));
            if (request.OffsetBytes > bytes.LongLength)
                return Task.FromResult<SandboxDurableLogReadResult>(new SandboxDurableLogReadResult.Unavailable(new SandboxDurableLogProblem(SandboxDurableLogProblemCode.SourceReset)));
            var available = bytes.LongLength - request.OffsetBytes;
            if (available == 0 && request.FinalDrain && EmitEndOfSource)
                return Task.FromResult<SandboxDurableLogReadResult>(new SandboxDurableLogReadResult.EndOfSource(TruncatedAtEnd));
            if (available == 0 || !request.FinalDrain && available < request.MinimumBytes)
                return Task.FromResult<SandboxDurableLogReadResult>(new SandboxDurableLogReadResult.NoData());
            var length = (int)Math.Min(available, request.MaximumBytes);
            return Task.FromResult<SandboxDurableLogReadResult>(new SandboxDurableLogReadResult.Available(bytes.AsMemory((int)request.OffsetBytes, length)));
        }

        public void Set(string key, byte[] bytes) => _sources[key] = bytes;
    }

    private sealed class FakeLogService : IAgentRunLogService
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, StreamHead> _streams = new(StringComparer.Ordinal);
        public long CurrentFence { get; set; }
        public bool RejectAllOpens { get; set; }
        public bool FailAppend { get; set; }
        public bool BlockAppend { get; set; }
        public int RetryableAppendFailures { get; set; }
        public bool ConcurrentFailCaptureOnce { get; set; }
        public TimeSpan? ObservedOperationTimeout { get; private set; }
        public int FailCaptureCalls { get; private set; }
        public int AppendAttempts { get; private set; }
        public List<int> AppendSizes { get; } = [];
        public List<Guid> OpenedSessions { get; } = [];
        public Dictionary<(string Kind, Guid Session), long> SourceBasesBySession { get; } = [];
        public int CompletedCount { get; private set; }
        public IReadOnlyList<AgentRunLogCaptureHead> Heads { get { lock (_gate) return _streams.Values.Select(value => value.Head).ToArray(); } }

        public Task<AgentRunLogOpenResult> OpenAsync(AgentRunLogOpenRequest request, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                if (RejectAllOpens) return Task.FromResult<AgentRunLogOpenResult>(RejectOpen(AgentRunLogProblemCode.BackendUnavailable));
                if (request.WorkerFenceEpoch != CurrentFence) return Task.FromResult<AgentRunLogOpenResult>(RejectOpen(AgentRunLogProblemCode.StaleWorker));
                if (!_streams.TryGetValue(request.StreamKind, out var stream))
                {
                    var metadata = Metadata(Guid.NewGuid(), request);
                    stream = new StreamHead(new AgentRunLogCaptureHead(metadata, request.WorkerFenceEpoch, request.CaptureSessionId, 0, null));
                    _streams.Add(request.StreamKind, stream);
                    RecordOpen(request.StreamKind, request.CaptureSessionId, 0);
                    return Task.FromResult<AgentRunLogOpenResult>(Opened(stream.Head, false, false));
                }
                if (stream.Head.Metadata.State != AgentRunLogStreamState.Open) return Task.FromResult<AgentRunLogOpenResult>(RejectOpen(AgentRunLogProblemCode.StreamTerminal));
                if (stream.Head.CaptureSessionId == request.CaptureSessionId)
                {
                    var reclaimed = request.WorkerFenceEpoch > stream.Head.WorkerFenceEpoch;
                    if (request.WorkerFenceEpoch < stream.Head.WorkerFenceEpoch) return Task.FromResult<AgentRunLogOpenResult>(RejectOpen(AgentRunLogProblemCode.StaleWorker));
                    if (reclaimed) stream.Head = stream.Head with { WorkerFenceEpoch = request.WorkerFenceEpoch, Metadata = stream.Head.Metadata with { Revision = stream.Head.Metadata.Revision + 1 } };
                    RecordOpen(request.StreamKind, request.CaptureSessionId, stream.Head.CaptureSourceBaseOffsetBytes);
                    return Task.FromResult<AgentRunLogOpenResult>(Opened(stream.Head, !reclaimed, reclaimed));
                }
                if (stream.Head.CaptureFinalizedAt == null) return Task.FromResult<AgentRunLogOpenResult>(RejectOpen(AgentRunLogProblemCode.CaptureClaimConflict));
                var next = stream.Head with
                {
                    WorkerFenceEpoch = request.WorkerFenceEpoch, CaptureSessionId = request.CaptureSessionId,
                    CaptureSourceBaseOffsetBytes = stream.Head.Metadata.SourceOffsetBytes, CaptureFinalizedAt = null,
                    Metadata = stream.Head.Metadata with { Revision = stream.Head.Metadata.Revision + 1 },
                };
                stream.Head = next;
                RecordOpen(request.StreamKind, request.CaptureSessionId, next.CaptureSourceBaseOffsetBytes);
                return Task.FromResult<AgentRunLogOpenResult>(Opened(next, false, true));
            }
        }

        public Task<AgentRunLogAppendResult> AppendAsync(AgentRunLogAppendRequest request, CancellationToken cancellationToken)
        {
            ObservedOperationTimeout = request.OperationTimeout;
            if (BlockAppend) return BlockAppendAsync(cancellationToken);
            lock (_gate)
            {
                var stream = Find(request.StreamId);
                AppendAttempts++;
                if (RetryableAppendFailures > 0)
                {
                    RetryableAppendFailures--;
                    return Task.FromResult<AgentRunLogAppendResult>(new AgentRunLogAppendResult.Rejected(new AgentRunLogProblem(AgentRunLogProblemCode.BackendUnavailable, true)));
                }
                if (FailAppend) return Task.FromResult<AgentRunLogAppendResult>(new AgentRunLogAppendResult.Rejected(new AgentRunLogProblem(AgentRunLogProblemCode.ArtifactCorrupt)));
                if (!Owns(stream, request.WorkerFenceEpoch, request.CaptureSessionId)) return Task.FromResult<AgentRunLogAppendResult>(new AgentRunLogAppendResult.Rejected(new AgentRunLogProblem(AgentRunLogProblemCode.StaleWorker)));
                if (stream.Head.Metadata.TotalBytes != request.ExpectedOffsetBytes || stream.Head.Metadata.SourceOffsetBytes != request.ExpectedSourceOffsetBytes)
                    return Task.FromResult<AgentRunLogAppendResult>(new AgentRunLogAppendResult.Rejected(new AgentRunLogProblem(AgentRunLogProblemCode.NonContiguous)));
                var start = stream.Head.Metadata.TotalBytes;
                stream.Bytes.AddRange(request.Bytes.ToArray());
                AppendSizes.Add(request.Bytes.Length);
                stream.Head = stream.Head with
                {
                    Metadata = stream.Head.Metadata with
                    {
                        Revision = stream.Head.Metadata.Revision + 1, SegmentCount = stream.Head.Metadata.SegmentCount + 1,
                        TotalBytes = stream.Head.Metadata.TotalBytes + request.Bytes.Length,
                        SourceOffsetBytes = stream.Head.Metadata.SourceOffsetBytes + request.SourceLengthBytes,
                    },
                };
                var receipt = new AgentRunLogSegmentReceipt(Guid.NewGuid(), request.ExpectedSegmentOrdinal, start, request.Bytes.Length, request.ExpectedSourceOffsetBytes, request.SourceLengthBytes, Guid.NewGuid());
                return Task.FromResult<AgentRunLogAppendResult>(new AgentRunLogAppendResult.Appended(stream.Head.Metadata, receipt, false));
            }
        }

        private static async Task<AgentRunLogAppendResult> BlockAppendAsync(CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new UnreachableException();
        }

        public Task<AgentRunLogFinalizeSourceResult> FinalizeSourceAsync(AgentRunLogFinalizeSourceRequest request, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                var stream = Find(request.StreamId);
                if (!Owns(stream, request.WorkerFenceEpoch, request.CaptureSessionId)) return Task.FromResult<AgentRunLogFinalizeSourceResult>(new AgentRunLogFinalizeSourceResult.Rejected(new AgentRunLogProblem(AgentRunLogProblemCode.StaleWorker)));
                if (stream.Head.CaptureFinalizedAt != null) return Task.FromResult<AgentRunLogFinalizeSourceResult>(new AgentRunLogFinalizeSourceResult.Finalized(stream.Head.Metadata, true));
                if (stream.Head.Metadata.Revision != request.ExpectedRevision || stream.Head.Metadata.SourceOffsetBytes != request.ExpectedSourceOffsetBytes)
                    return Task.FromResult<AgentRunLogFinalizeSourceResult>(new AgentRunLogFinalizeSourceResult.Rejected(new AgentRunLogProblem(AgentRunLogProblemCode.ConcurrentMutation)));
                var now = DateTimeOffset.UtcNow;
                stream.Head = stream.Head with { CaptureFinalizedAt = now, Metadata = stream.Head.Metadata with { Revision = stream.Head.Metadata.Revision + 1, LastModifiedAt = now } };
                return Task.FromResult<AgentRunLogFinalizeSourceResult>(new AgentRunLogFinalizeSourceResult.Finalized(stream.Head.Metadata, false));
            }
        }

        public Task<AgentRunLogCompleteResult> CompleteAsync(AgentRunLogCompleteRequest request, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                var stream = Find(request.StreamId);
                if (stream.Head.CaptureFinalizedAt == null) return Task.FromResult<AgentRunLogCompleteResult>(new AgentRunLogCompleteResult.Rejected(new AgentRunLogProblem(AgentRunLogProblemCode.SourceNotFinalized)));
                var now = DateTimeOffset.UtcNow;
                stream.Head = stream.Head with { Metadata = stream.Head.Metadata with { State = AgentRunLogStreamState.Completed, Revision = stream.Head.Metadata.Revision + 1, CompletedAt = now, LastModifiedAt = now } };
                CompletedCount++;
                return Task.FromResult<AgentRunLogCompleteResult>(new AgentRunLogCompleteResult.Completed(stream.Head.Metadata));
            }
        }

        public Task<AgentRunLogFailCaptureResult> FailCaptureAsync(AgentRunLogFailCaptureRequest request, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                var stream = Find(request.StreamId);
                FailCaptureCalls++;
                if (ConcurrentFailCaptureOnce)
                {
                    ConcurrentFailCaptureOnce = false;
                    stream.Head = stream.Head with { Metadata = stream.Head.Metadata with { Revision = stream.Head.Metadata.Revision + 1 } };
                    return Task.FromResult<AgentRunLogFailCaptureResult>(new AgentRunLogFailCaptureResult.Rejected(new AgentRunLogProblem(AgentRunLogProblemCode.ConcurrentMutation, true)));
                }
                if (stream.Head.Metadata.State == request.TerminalState)
                    return Task.FromResult<AgentRunLogFailCaptureResult>(new AgentRunLogFailCaptureResult.Failed(stream.Head.Metadata, true));
                var now = DateTimeOffset.UtcNow;
                stream.Head = stream.Head with { Metadata = stream.Head.Metadata with { State = request.TerminalState, Revision = stream.Head.Metadata.Revision + 1, ErrorCode = request.ErrorCode, CompletedAt = now, LastModifiedAt = now } };
                return Task.FromResult<AgentRunLogFailCaptureResult>(new AgentRunLogFailCaptureResult.Failed(stream.Head.Metadata, false));
            }
        }

        public Task<AgentRunLogMetadataResult> GetMetadataAsync(Guid teamId, Guid streamId, CancellationToken cancellationToken) =>
            Task.FromResult<AgentRunLogMetadataResult>(new AgentRunLogMetadataResult.Found(Find(streamId).Head.Metadata));
        public Task<IReadOnlyList<AgentRunLogMetadata>> ListMetadataAsync(Guid teamId, Guid agentRunId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<AgentRunLogMetadata>>(Heads.Select(value => value.Metadata).ToArray());
        public Task<IReadOnlyList<AgentRunLogCaptureHead>> ListCaptureHeadsAsync(Guid teamId, Guid agentRunId, CancellationToken cancellationToken) =>
            Task.FromResult(Heads);
        public Task<AgentRunLogRangeResult> ReadRangeAsync(AgentRunLogRangeRequest request, CancellationToken cancellationToken) => throw new NotImplementedException();
        public byte[] Bytes(string kind) { lock (_gate) return _streams[kind].Bytes.ToArray(); }

        private StreamHead Find(Guid streamId) => _streams.Values.Single(value => value.Head.Metadata.StreamId == streamId);
        private bool Owns(StreamHead stream, long fence, Guid session) => CurrentFence == fence && stream.Head.WorkerFenceEpoch == fence && stream.Head.CaptureSessionId == session;
        private void RecordOpen(string kind, Guid session, long sourceBase) { OpenedSessions.Add(session); SourceBasesBySession[(kind, session)] = sourceBase; }
        private static AgentRunLogOpenResult.Rejected RejectOpen(AgentRunLogProblemCode code) => new(new AgentRunLogProblem(code));
        private static AgentRunLogOpenResult.Opened Opened(AgentRunLogCaptureHead head, bool already, bool reclaimed) => new(head.Metadata, already, reclaimed) { CaptureSourceBaseOffsetBytes = head.CaptureSourceBaseOffsetBytes, CaptureFinalizedAt = head.CaptureFinalizedAt };
        private static AgentRunLogMetadata Metadata(Guid id, AgentRunLogOpenRequest request)
        {
            var now = DateTimeOffset.UtcNow;
            return new AgentRunLogMetadata(id, RunId, request.StreamKind, request.ContentType, request.ContentEncoding, request.CaptureSource, request.Retention, AgentRunLogStreamState.Open, 1, 0, 0, 0, null, now, now, null, null);
        }

        private sealed class StreamHead(AgentRunLogCaptureHead head)
        {
            public AgentRunLogCaptureHead Head { get; set; } = head;
            public List<byte> Bytes { get; } = [];
        }
    }
}
