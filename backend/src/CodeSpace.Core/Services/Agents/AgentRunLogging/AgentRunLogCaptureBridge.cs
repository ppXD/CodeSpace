using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents.Sandbox;
using CodeSpace.Messages.Agents;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.Agents.AgentRunLogging;

public sealed class AgentRunLogCaptureBridge : IAgentRunLogCaptureBridge
{
    private const int MinimumSegmentBytes = 256 * 1024;
    private const int MaximumSegmentBytes = 1024 * 1024;
    private const int MaximumReadsPerPoll = 8;
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan DefaultOperationTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan DefaultFinalizationBudget = TimeSpan.FromSeconds(30);

    /// <summary>The source proved complete but its own size cap cut it short: everything captured stays readable, and the stream terminalizes Truncated rather than claiming a whole capture it knows it does not have.</summary>
    private static readonly CaptureFailure Truncation = new("source-truncated", "The durable sandbox log source reached its spool size cap; the captured bytes are the head of a longer output.", AgentRunLogStreamState.Truncated);

    private readonly IAgentRunLogService _logs;
    private readonly IAgentRunLogStorageResolver _storage;
    private readonly IAgentRunLogCaptureRecoveryService _recovery;
    private readonly ILogger<AgentRunLogCaptureBridge> _logger;
    private readonly TimeSpan _operationTimeout;
    private readonly TimeSpan _finalizationBudget;

    public AgentRunLogCaptureBridge(IAgentRunLogService logs, IAgentRunLogStorageResolver storage, IAgentRunLogCaptureRecoveryService recovery, ILogger<AgentRunLogCaptureBridge> logger) : this(logs, storage, recovery, logger, new AgentRunLogCaptureBridgeOptions(DefaultOperationTimeout, DefaultFinalizationBudget)) { }

    internal AgentRunLogCaptureBridge(IAgentRunLogService logs, IAgentRunLogStorageResolver storage, IAgentRunLogCaptureRecoveryService recovery, ILogger<AgentRunLogCaptureBridge> logger, AgentRunLogCaptureBridgeOptions options)
    {
        if (options.OperationTimeout <= TimeSpan.Zero || options.FinalizationBudget <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(options));
        _logs = logs;
        _storage = storage;
        _recovery = recovery;
        _logger = logger;
        _operationTimeout = options.OperationTimeout;
        _finalizationBudget = options.FinalizationBudget;
    }

    public async Task<IAgentRunLogCaptureSession> OpenAsync(AgentRunLogCaptureOpenRequest request, CancellationToken cancellationToken)
    {
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        operation.CancelAfter(_operationTimeout);
        var captureToken = operation.Token;
        try
        {
            var descriptors = request.Source.DescribeLogs(request.Handle);
            if (!Valid(request, descriptors)) return new NoopCaptureSession(request.Handle);
            var captureSessionId = request.Handle.AgentRunLogCaptureSessionId!.Value;
            if (!await DeclareExpectedStreamsAsync(Declaration(request.TeamId, request.AgentRunId, request.WorkerFenceEpoch, captureSessionId, descriptors), captureToken).ConfigureAwait(false))
                return new NoopCaptureSession(request.Handle);
            var failure = new CaptureFailureContext(request.TeamId, request.AgentRunId, request.WorkerFenceEpoch, captureSessionId);
            AgentRunLogStorageResolution storage;
            try { storage = await _storage.ResolveAsync(request.TeamId, captureToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (captureToken.IsCancellationRequested) { throw; }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Agent run {RunId} log storage policy resolution failed", request.AgentRunId);
                storage = new AgentRunLogStorageResolution.Unavailable(AgentRunLogStorageProblemCode.ResolutionFailed);
            }
            var streams = new List<CaptureStream>(descriptors.Count);

            foreach (var descriptor in descriptors)
            {
                var opened = await _logs.OpenAsync(new AgentRunLogOpenRequest
                {
                    TeamId = request.TeamId, AgentRunId = request.AgentRunId, WorkerFenceEpoch = request.WorkerFenceEpoch,
                    CaptureSessionId = captureSessionId, StreamKind = descriptor.StreamKind, ContentType = descriptor.ContentType,
                    ContentEncoding = descriptor.ContentEncoding, CaptureSource = descriptor.CaptureSource,
                }, captureToken).ConfigureAwait(false);
                if (opened is not AgentRunLogOpenResult.Opened ready)
                {
                    _logger.LogWarning("Agent run {RunId} log stream {StreamKind} could not be opened: {Problem}", request.AgentRunId, descriptor.StreamKind, ((AgentRunLogOpenResult.Rejected)opened).Problem.Code);
                    continue;
                }
                if (storage is AgentRunLogStorageResolution.Unavailable unavailable)
                {
                    await FailQuietlyAsync(failure, ready.Metadata, new CaptureFailure($"storage-profile-{Code(unavailable.Code)}", "No Active valid storage route was authorized for the Agent Run log data class."), captureToken).ConfigureAwait(false);
                    continue;
                }
                if (ready.CaptureSourceBaseOffsetBytes < 0 || ready.Metadata.SourceOffsetBytes < ready.CaptureSourceBaseOffsetBytes)
                {
                    await FailQuietlyAsync(failure, ready.Metadata, new CaptureFailure("source-cursor-invalid", "The durable source cursor could not be reconciled with its persisted spool base."), captureToken).ConfigureAwait(false);
                    continue;
                }
                streams.Add(new CaptureStream(descriptor, ready.Metadata, (AgentRunLogStorageResolution.Ready)storage, ready.CaptureSourceBaseOffsetBytes, request.Redactor.CreateUtf8Stream()));
            }

            return streams.Count == 0 ? new NoopCaptureSession(request.Handle) : new CaptureSession(this, request, captureSessionId, streams);
        }
        catch (OperationCanceledException) when (captureToken.IsCancellationRequested)
        {
            if (!cancellationToken.IsCancellationRequested)
                _logger.LogWarning("Agent run {RunId} log capture preparation exceeded the shadow operation budget", request.AgentRunId);
            return new NoopCaptureSession(request.Handle);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Agent run {RunId} log capture could not be prepared; sandbox execution remains unchanged", request.AgentRunId);
            return new NoopCaptureSession(request.Handle);
        }
    }

    public async Task RecordGapAsync(AgentRunLogCaptureGapRequest request, CancellationToken cancellationToken)
    {
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        operation.CancelAfter(_operationTimeout);
        var captureToken = operation.Token;
        try
        {
            var descriptors = request.Source.DescribeLogs(request.Handle);
            if (!Valid(request, descriptors)) return;
            var sessionId = request.Handle.AgentRunLogCaptureSessionId!.Value;
            if (!await DeclareExpectedStreamsAsync(Declaration(request.TeamId, request.AgentRunId, request.WorkerFenceEpoch, sessionId, descriptors), captureToken).ConfigureAwait(false)) return;
            var failure = new CaptureFailureContext(request.TeamId, request.AgentRunId, request.WorkerFenceEpoch, sessionId);
            foreach (var descriptor in descriptors)
            {
                var opened = await _logs.OpenAsync(new AgentRunLogOpenRequest
                {
                    TeamId = request.TeamId, AgentRunId = request.AgentRunId, WorkerFenceEpoch = request.WorkerFenceEpoch,
                    CaptureSessionId = sessionId, StreamKind = descriptor.StreamKind, ContentType = descriptor.ContentType,
                    ContentEncoding = descriptor.ContentEncoding, CaptureSource = descriptor.CaptureSource,
                }, captureToken).ConfigureAwait(false);
                if (opened is AgentRunLogOpenResult.Opened ready)
                    await FailQuietlyAsync(failure, ready.Metadata, new CaptureFailure(request.ErrorCode, request.ErrorMessage), captureToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (captureToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Agent run {RunId} log capture gap could not be recorded", request.AgentRunId);
        }
    }

    public async Task CompleteRunAsync(Guid teamId, Guid agentRunId, long workerFenceEpoch, CancellationToken cancellationToken)
    {
        if (teamId == Guid.Empty || agentRunId == Guid.Empty || workerFenceEpoch <= 0) return;
        using var finalization = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        finalization.CancelAfter(_finalizationBudget);
        var captureToken = finalization.Token;
        IReadOnlyList<AgentRunLogCaptureHead> streams;
        try { streams = await _logs.ListCaptureHeadsAsync(teamId, agentRunId, captureToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (captureToken.IsCancellationRequested)
        {
            _logger.LogWarning("Agent run {RunId} log terminalization exceeded its shadow budget before streams could be listed", agentRunId);
            return;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Agent run {RunId} log streams could not be listed before terminalization", agentRunId);
            return;
        }

        foreach (var stream in streams.Where(value => value.Metadata.State == AgentRunLogStreamState.Open && IsProcessStream(value.Metadata.StreamKind)))
        {
            var failure = new CaptureFailureContext(teamId, agentRunId, workerFenceEpoch, stream.CaptureSessionId);
            if (stream.WorkerFenceEpoch != workerFenceEpoch)
            {
                _logger.LogWarning("Agent run {RunId} log stream {StreamId} retained stale fence {ObservedFence}; terminal fence is {ExpectedFence}", agentRunId, stream.Metadata.StreamId, stream.WorkerFenceEpoch, workerFenceEpoch);
                continue;
            }
            if (stream.CaptureFinalizedAt == null)
            {
                _logger.LogWarning("Agent run {RunId} log stream {StreamId} remains Open without a final source receipt for later reconciliation", agentRunId, stream.Metadata.StreamId);
                continue;
            }

            try
            {
                var result = await _logs.CompleteAsync(new AgentRunLogCompleteRequest
                {
                    TeamId = teamId, AgentRunId = agentRunId, StreamId = stream.Metadata.StreamId,
                    WorkerFenceEpoch = workerFenceEpoch, CaptureSessionId = stream.CaptureSessionId,
                    ExpectedRevision = stream.Metadata.Revision, OperationTimeout = _operationTimeout,
                }, captureToken).ConfigureAwait(false);
                if (result is AgentRunLogCompleteResult.Rejected rejected)
                {
                    if (rejected.Problem.IsTransient)
                        _logger.LogWarning("Agent run {RunId} log stream {StreamId} terminalization is transiently unavailable and remains Open for recovery: {Problem}", agentRunId, stream.Metadata.StreamId, rejected.Problem.Code);
                    else
                        await FailQuietlyAsync(failure, stream.Metadata, new CaptureFailure($"complete-{Code(rejected.Problem.Code)}", "The finalized Agent Run log could not be verified before terminalization."), captureToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (captureToken.IsCancellationRequested)
            {
                _logger.LogWarning("Agent run {RunId} log stream {StreamId} terminalization exceeded its shadow budget and remains reconcilable", agentRunId, stream.Metadata.StreamId);
                return;
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Agent run {RunId} log stream {StreamId} terminalization raised an untyped storage error and remains Open for recovery", agentRunId, stream.Metadata.StreamId);
            }
        }
    }

    private async Task CaptureLoopAsync(AgentRunLogCaptureOpenRequest request, Guid captureSessionId, IReadOnlyList<CaptureStream> streams, Task finish, CancellationToken cancellationToken)
    {
        try
        {
            while (!finish.IsCompleted)
            {
                foreach (var stream in streams.Where(value => !value.Terminal))
                    await PumpAsync(request, captureSessionId, stream, final: false, cancellationToken).ConfigureAwait(false);
                await Task.WhenAny(Task.Delay(PollInterval, cancellationToken), finish).ConfigureAwait(false);
            }
            while (streams.Any(value => !value.Terminal))
            {
                foreach (var stream in streams.Where(value => !value.Terminal))
                    await PumpAsync(request, captureSessionId, stream, final: true, cancellationToken).ConfigureAwait(false);
                if (streams.Any(value => !value.Terminal)) await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Agent run {RunId} shadow log capture loop failed", request.AgentRunId);
            await FailStreamsAsync(request, captureSessionId, streams, new CaptureFailure("capture-loop-exception", "The Agent Run log capture loop raised an unexpected error."), CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task PumpAsync(AgentRunLogCaptureOpenRequest request, Guid captureSessionId, CaptureStream stream, bool final, CancellationToken cancellationToken)
    {
        var reads = 0;
        while (!stream.Terminal && (final || reads < MaximumReadsPerPoll))
        {
            if (!await FlushPendingAsync(request, captureSessionId, stream, final, cancellationToken).ConfigureAwait(false)) return;
            var read = await request.Source.ReadAsync(new SandboxDurableLogReadRequest
            {
                Handle = request.Handle, SourceKey = stream.Descriptor.SourceKey, OffsetBytes = stream.LocalReadOffset,
                MinimumBytes = final ? 1 : MinimumSegmentBytes, MaximumBytes = MaximumSegmentBytes, FinalDrain = final,
            }, cancellationToken).ConfigureAwait(false);
            if (read is SandboxDurableLogReadResult.Unavailable unavailable)
            {
                if (!final && unavailable.Problem.IsRetryable) return;
                await FailStreamAsync(request, captureSessionId, stream, new CaptureFailure($"source-{Code(unavailable.Problem.Code)}", "The durable sandbox log source became unavailable before capture completed."), cancellationToken).ConfigureAwait(false);
                return;
            }
            if (read is SandboxDurableLogReadResult.NoData)
            {
                return;
            }
            if (read is SandboxDurableLogReadResult.EndOfSource end)
            {
                if (!final)
                {
                    await FailStreamAsync(request, captureSessionId, stream, new CaptureFailure("source-protocol-invalid", "The durable source emitted EOF outside the final-drain protocol."), cancellationToken).ConfigureAwait(false);
                    return;
                }
                var tail = stream.Redactor.Transform([], final: true);
                if (tail.SourceBytesConsumed > 0) stream.Pending = new PendingAppend(tail.Bytes, tail.SourceBytesConsumed);
                if (!await FlushPendingAsync(request, captureSessionId, stream, final: true, cancellationToken).ConfigureAwait(false)) return;
                var expectedLocal = stream.Metadata.SourceOffsetBytes - stream.SourceBaseOffset;
                if (stream.LocalReadOffset != expectedLocal)
                {
                    await FailStreamAsync(request, captureSessionId, stream, new CaptureFailure("source-cursor-gap", "The durable source ended before every observed source byte was committed."), cancellationToken).ConfigureAwait(false);
                    return;
                }
                if (await FinalizeSourceAsync(request, captureSessionId, stream, cancellationToken).ConfigureAwait(false) && end.Truncated)
                    await FailQuietlyAsync(new CaptureFailureContext(request.TeamId, request.AgentRunId, request.WorkerFenceEpoch, captureSessionId), stream.Metadata, Truncation, cancellationToken).ConfigureAwait(false);
                return;
            }

            var bytes = ((SandboxDurableLogReadResult.Available)read).Bytes;
            stream.LocalReadOffset += bytes.Length;
            var transformed = stream.Redactor.Transform(bytes.Span, final: false);
            if (transformed.SourceBytesConsumed > 0) stream.Pending = new PendingAppend(transformed.Bytes, transformed.SourceBytesConsumed);
            reads++;
        }
    }

    private async Task<bool> FlushPendingAsync(AgentRunLogCaptureOpenRequest request, Guid captureSessionId, CaptureStream stream, bool final, CancellationToken cancellationToken)
    {
        if (stream.Pending is not { } pending) return true;
        var attempt = 0;
        while (true)
        {
            AgentRunLogAppendResult result;
            try
            {
                result = await _logs.AppendAsync(new AgentRunLogAppendRequest
                {
                    TeamId = request.TeamId, AgentRunId = request.AgentRunId, StreamId = stream.Metadata.StreamId,
                    WorkerFenceEpoch = request.WorkerFenceEpoch, CaptureSessionId = captureSessionId,
                    ExpectedSegmentOrdinal = stream.Metadata.SegmentCount + 1, ExpectedOffsetBytes = stream.Metadata.TotalBytes,
                    ExpectedSourceOffsetBytes = stream.Metadata.SourceOffsetBytes, SourceLengthBytes = pending.SourceBytesConsumed,
                    StorageProfileId = stream.Storage.StorageProfileId, StorageProfileRevision = stream.Storage.StorageProfileRevision,
                    ActorId = request.ActorId, Bytes = pending.Bytes, OperationTimeout = _operationTimeout,
                }, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception)
            {
                if (!final) return false;
                if (attempt == 0) _logger.LogWarning(exception, "Agent run {RunId} log stream {StreamId} final append raised an untyped transient error; retrying inside the final-drain budget", request.AgentRunId, stream.Metadata.StreamId);
                await Task.Delay(AppendRetryDelay(++attempt), cancellationToken).ConfigureAwait(false);
                continue;
            }
            if (result is AgentRunLogAppendResult.Appended appended)
            {
                stream.Metadata = appended.Metadata;
                stream.Pending = null;
                return true;
            }
            var problem = ((AgentRunLogAppendResult.Rejected)result).Problem;
            if (problem.IsTransient)
            {
                if (!final) return false;
                await Task.Delay(AppendRetryDelay(++attempt), cancellationToken).ConfigureAwait(false);
                continue;
            }
            await FailStreamAsync(request, captureSessionId, stream, new CaptureFailure($"capture-{Code(problem.Code)}", "The Agent Run log segment could not be committed to durable storage."), cancellationToken).ConfigureAwait(false);
            return false;
        }
    }

    /// <summary>Commit the source's final-drain receipt. True only when the stream is now provably finalized — the caller's licence to write any further terminal fact about it.</summary>
    private async Task<bool> FinalizeSourceAsync(AgentRunLogCaptureOpenRequest request, Guid captureSessionId, CaptureStream stream, CancellationToken cancellationToken)
    {
        var result = await _logs.FinalizeSourceAsync(new AgentRunLogFinalizeSourceRequest
        {
            TeamId = request.TeamId, AgentRunId = request.AgentRunId, StreamId = stream.Metadata.StreamId,
            WorkerFenceEpoch = request.WorkerFenceEpoch, CaptureSessionId = captureSessionId,
            ExpectedRevision = stream.Metadata.Revision, ExpectedSourceOffsetBytes = stream.Metadata.SourceOffsetBytes,
        }, cancellationToken).ConfigureAwait(false);
        if (result is AgentRunLogFinalizeSourceResult.Finalized finalized)
        {
            stream.Metadata = finalized.Metadata;
            stream.Terminal = true;
            return true;
        }
        var problem = ((AgentRunLogFinalizeSourceResult.Rejected)result).Problem;
        if (problem.IsTransient) return false;
        await FailStreamAsync(request, captureSessionId, stream, new CaptureFailure($"finalize-{Code(problem.Code)}", "The Agent Run log source final-drain receipt could not be committed."), cancellationToken).ConfigureAwait(false);
        return false;
    }

    private async Task FailStreamAsync(AgentRunLogCaptureOpenRequest request, Guid captureSessionId, CaptureStream stream, CaptureFailure failure, CancellationToken cancellationToken)
    {
        stream.Terminal = true;
        await FailQuietlyAsync(new CaptureFailureContext(request.TeamId, request.AgentRunId, request.WorkerFenceEpoch, captureSessionId), stream.Metadata, failure, cancellationToken).ConfigureAwait(false);
    }

    private async Task FailStreamsAsync(AgentRunLogCaptureOpenRequest request, Guid captureSessionId, IReadOnlyList<CaptureStream> streams, CaptureFailure failure, CancellationToken cancellationToken)
    {
        foreach (var stream in streams.Where(value => !value.Terminal))
            await FailStreamAsync(request, captureSessionId, stream, failure, cancellationToken).ConfigureAwait(false);
    }

    private async Task FailQuietlyAsync(CaptureFailureContext context, AgentRunLogMetadata metadata, CaptureFailure failure, CancellationToken cancellationToken)
    {
        var current = metadata;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                var result = await _logs.FailCaptureAsync(new AgentRunLogFailCaptureRequest
                {
                    TeamId = context.TeamId, AgentRunId = context.AgentRunId, StreamId = current.StreamId,
                    WorkerFenceEpoch = context.WorkerFenceEpoch, CaptureSessionId = context.CaptureSessionId,
                    ExpectedRevision = current.Revision, ErrorCode = failure.Code, ErrorMessage = failure.Message,
                    TerminalState = failure.TerminalState,
                }, cancellationToken).ConfigureAwait(false);
                if (result is AgentRunLogFailCaptureResult.Failed) return;
                var problem = ((AgentRunLogFailCaptureResult.Rejected)result).Problem;
                if (problem.Code != AgentRunLogProblemCode.ConcurrentMutation)
                {
                    _logger.LogWarning("Agent run {RunId} log stream {StreamId} capture health was rejected: {Problem}", context.AgentRunId, current.StreamId, problem.Code);
                    return;
                }
                var heads = await _logs.ListCaptureHeadsAsync(context.TeamId, context.AgentRunId, cancellationToken).ConfigureAwait(false);
                var refreshed = heads.SingleOrDefault(value => value.Metadata.StreamId == current.StreamId);
                if (refreshed == null || refreshed.WorkerFenceEpoch != context.WorkerFenceEpoch || refreshed.CaptureSessionId != context.CaptureSessionId || refreshed.Metadata.State != AgentRunLogStreamState.Open)
                {
                    _logger.LogWarning("Agent run {RunId} log stream {StreamId} capture health lost its active claim while retrying", context.AgentRunId, current.StreamId);
                    return;
                }
                current = refreshed.Metadata;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Agent run {RunId} log stream {StreamId} capture health could not be persisted", context.AgentRunId, current.StreamId);
                return;
            }
        }
        _logger.LogWarning("Agent run {RunId} log stream {StreamId} capture health remained concurrently mutable after bounded retries", context.AgentRunId, current.StreamId);
    }

    private async Task<bool> DeclareExpectedStreamsAsync(AgentRunLogCaptureDeclarationRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _recovery.DeclareAsync(request, cancellationToken).ConfigureAwait(false);
            if (result is AgentRunLogCaptureDeclarationResult.Rejected rejected)
            {
                _logger.LogWarning("Agent run {RunId} expected log streams could not be declared: {Problem}", request.AgentRunId, rejected.Code);
                return false;
            }
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            // A total metadata outage cannot durably prove its own missing intent. Keep task execution byte-identical,
            // surface the honest boundary, and let any streams that can still open remain independently inspectable.
            _logger.LogWarning(exception, "Agent run {RunId} expected log stream declaration failed; no durable intent can be claimed for this outage", request.AgentRunId);
            return true;
        }
    }

    private static AgentRunLogCaptureDeclarationRequest Declaration(Guid teamId, Guid agentRunId, long workerFenceEpoch, Guid captureSessionId, IReadOnlyList<SandboxDurableLogDescriptor> descriptors) => new()
    {
        TeamId = teamId, AgentRunId = agentRunId, WorkerFenceEpoch = workerFenceEpoch, CaptureSessionId = captureSessionId,
        Streams = descriptors.Select(value => new AgentRunLogExpectedStream(value.StreamKind, value.ContentType, value.ContentEncoding, value.CaptureSource)).ToArray(),
    };

    private static bool Valid(AgentRunLogCaptureOpenRequest request, IReadOnlyList<SandboxDurableLogDescriptor> descriptors) => request.TeamId != Guid.Empty && request.AgentRunId != Guid.Empty && request.ActorId != Guid.Empty && request.WorkerFenceEpoch > 0 && request.Handle.AgentRunLogCaptureSessionId is { } sessionId && sessionId != Guid.Empty && descriptors.Count > 0 && descriptors.Select(value => value.SourceKey).Distinct(StringComparer.Ordinal).Count() == descriptors.Count && descriptors.Select(value => value.StreamKind).Distinct(StringComparer.Ordinal).Count() == descriptors.Count;
    private static bool Valid(AgentRunLogCaptureGapRequest request, IReadOnlyList<SandboxDurableLogDescriptor> descriptors) => request.TeamId != Guid.Empty && request.AgentRunId != Guid.Empty && request.WorkerFenceEpoch > 0 && request.Handle.AgentRunLogCaptureSessionId is { } sessionId && sessionId != Guid.Empty && request.ErrorCode is { Length: > 0 and <= 128 } && request.ErrorMessage is { Length: > 0 and <= 2048 } && descriptors.Count > 0 && descriptors.Select(value => value.SourceKey).Distinct(StringComparer.Ordinal).Count() == descriptors.Count && descriptors.Select(value => value.StreamKind).Distinct(StringComparer.Ordinal).Count() == descriptors.Count;
    private static bool IsProcessStream(string streamKind) => streamKind is AgentRunLogKinds.StandardOutput or AgentRunLogKinds.StandardError;
    private static TimeSpan AppendRetryDelay(int attempt) => TimeSpan.FromMilliseconds(Math.Min(1000, 50 * Math.Pow(2, Math.Min(Math.Max(attempt - 1, 0), 5))));
    private static string Code<T>(T value) where T : struct, Enum => string.Concat(value.ToString().Select((character, index) => char.IsUpper(character) && index > 0 ? $"-{char.ToLowerInvariant(character)}" : char.ToLowerInvariant(character).ToString()));

    private sealed class CaptureSession : IAgentRunLogCaptureSession
    {
        private readonly AgentRunLogCaptureBridge _owner;
        private readonly AgentRunLogCaptureOpenRequest _request;
        private readonly Guid _captureSessionId;
        private readonly IReadOnlyList<CaptureStream> _streams;
        private int _observed;

        public CaptureSession(AgentRunLogCaptureBridge owner, AgentRunLogCaptureOpenRequest request, Guid captureSessionId, IReadOnlyList<CaptureStream> streams)
        {
            _owner = owner;
            _request = request;
            Handle = request.Handle;
            _captureSessionId = captureSessionId;
            _streams = streams;
        }

        public SandboxHandle Handle { get; }

        public async Task<SandboxResult> ObserveAsync(Func<SandboxHandle, CancellationToken, Task<SandboxResult>> observer, CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref _observed, 1) != 0) throw new InvalidOperationException("A capture session can observe its durable source only once.");
            var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var captureCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var capture = _owner.CaptureLoopAsync(_request, _captureSessionId, _streams, finish.Task, captureCts.Token);
            try
            {
                var result = await observer(Handle, cancellationToken).ConfigureAwait(false);
                finish.TrySetResult();
                captureCts.CancelAfter(_owner._finalizationBudget);
                await capture.ConfigureAwait(false);
                if (_streams.Any(value => !value.Terminal))
                    _owner._logger.LogWarning("Agent run {RunId} source final drain exceeded its shadow budget and remains Open for reconciliation", _request.AgentRunId);
                return result;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                captureCts.Cancel();
                try { await capture.ConfigureAwait(false); } catch { }
                throw;
            }
            catch
            {
                captureCts.Cancel();
                try { await capture.ConfigureAwait(false); } catch { }
                using var failure = new CancellationTokenSource(_owner._operationTimeout);
                await _owner.FailStreamsAsync(_request, _captureSessionId, _streams, new CaptureFailure("observer-failed-before-terminal", "The durable sandbox observer failed before a terminal result proved source completeness."), failure.Token).ConfigureAwait(false);
                throw;
            }
        }
    }

    private sealed class NoopCaptureSession(SandboxHandle handle) : IAgentRunLogCaptureSession
    {
        public SandboxHandle Handle { get; } = handle;
        public Task<SandboxResult> ObserveAsync(Func<SandboxHandle, CancellationToken, Task<SandboxResult>> observer, CancellationToken cancellationToken) => observer(Handle, cancellationToken);
    }

    private sealed class CaptureStream
    {
        public CaptureStream(SandboxDurableLogDescriptor descriptor, AgentRunLogMetadata metadata, AgentRunLogStorageResolution.Ready storage, long sourceBaseOffset, SecretUtf8RedactionStream redactor)
        {
            Descriptor = descriptor;
            Metadata = metadata;
            Storage = storage;
            SourceBaseOffset = sourceBaseOffset;
            LocalReadOffset = metadata.SourceOffsetBytes - sourceBaseOffset;
            Redactor = redactor;
        }

        public SandboxDurableLogDescriptor Descriptor { get; }
        public AgentRunLogMetadata Metadata { get; set; }
        public AgentRunLogStorageResolution.Ready Storage { get; }
        public long SourceBaseOffset { get; }
        public long LocalReadOffset { get; set; }
        public SecretUtf8RedactionStream Redactor { get; }
        public PendingAppend? Pending { get; set; }
        public bool Terminal { get; set; }
    }

    private sealed record PendingAppend(ReadOnlyMemory<byte> Bytes, int SourceBytesConsumed);
    /// <summary>One terminal capture fact: the machine-readable code, the operator-readable reason, and which non-Completed state it lands the stream in (capture broke, vs. a source its own size cap cut short).</summary>
    private sealed record CaptureFailure(string Code, string Message, AgentRunLogStreamState TerminalState = AgentRunLogStreamState.CaptureFailed);
    private sealed record CaptureFailureContext(Guid TeamId, Guid AgentRunId, long WorkerFenceEpoch, Guid CaptureSessionId);
}

internal sealed record AgentRunLogCaptureBridgeOptions(TimeSpan OperationTimeout, TimeSpan FinalizationBudget);
