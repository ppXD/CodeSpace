using System.Text.Json;
using CodeSpace.Core.Services.Workflows.Artifacts;
using CodeSpace.Core.Services.Workflows.Lifecycle;
using CodeSpace.Core.Services.Workflows.Llm;
using CodeSpace.Core.Services.Workflows.Nodes;
using CodeSpace.Core.Services.Workflows.Runtime;
using Shouldly;
using LogLevel = CodeSpace.Core.Services.Workflows.Lifecycle.LogLevel;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// Pins the contract of <see cref="INodeObservability"/>. These tests use a hand-rolled
/// recording double instead of a mocking library so the call sequence is readable in plain
/// assertions.
///
/// The five invariants pinned here:
///   1. <c>TraceExternalCallAsync</c> emits started → completed on success.
///   2. <c>TraceExternalCallAsync</c> emits started → failed on throw, AND re-throws.
///   3. The started + completed/failed records share one correlation id (so the run-detail
///      UI can pair them).
///   4. <c>PersistArtifactAsync</c> delegates to <see cref="IArtifactStore.PutAsync"/> with
///      the bound team id + returns the canonical ref shape.
///   5. A failed call's <c>category</c> is the thrown <see cref="LlmApiException"/>'s classified
///      category name, or null for any other exception — never sniffed from the (possibly
///      redacted) message — so a consumer (the real-model gate) can tell a gateway/transport
///      fault apart from a genuine regression.
/// </summary>
[Trait("Category", "Unit")]
public class NodeObservabilityTests
{
    private static readonly Guid RunId = Guid.NewGuid();
    private static readonly Guid TeamId = Guid.NewGuid();
    private static readonly Guid ParentRecordId = Guid.NewGuid();
    private const string NodeId = "n1";

    [Fact]
    public async Task TraceExternalCallAsync_emits_started_then_completed_on_success()
    {
        var logger = new RecordingLogger();
        var store = new RecordingArtifactStore();
        var observability = new NodeObservability(logger, store, RunId, NodeId, TeamId, ParentRecordId);

        var result = await observability.TraceExternalCallAsync(
            target: "https://api.example.com/v1/things",
            method: "GET",
            requestPayload: null,
            action: _ => Task.FromResult(42),
            cancellationToken: CancellationToken.None);

        result.ShouldBe(42, "the action's return value MUST be propagated verbatim — observability is a side channel");
        logger.StartedCalls.Count.ShouldBe(1);
        logger.CompletedCalls.Count.ShouldBe(1);
        logger.FailedCalls.ShouldBeEmpty();

        logger.StartedCalls[0].Target.ShouldBe("https://api.example.com/v1/things");
        logger.StartedCalls[0].Method.ShouldBe("GET");
        logger.StartedCalls[0].ParentRecordId.ShouldBe(ParentRecordId,
            "parent_record_id MUST chain back to the enclosing node row so the timeline tree renders");
    }

    [Fact]
    public async Task TraceExternalCallAsync_emits_started_then_failed_on_throw_and_rethrows()
    {
        var logger = new RecordingLogger();
        var store = new RecordingArtifactStore();
        var observability = new NodeObservability(logger, store, RunId, NodeId, TeamId, ParentRecordId);

        var ex = await Should.ThrowAsync<InvalidOperationException>(async () =>
        {
            await observability.TraceExternalCallAsync<int>(
                target: "https://api.example.com",
                method: "POST",
                requestPayload: null,
                action: _ => throw new InvalidOperationException("boom"),
                cancellationToken: CancellationToken.None);
        });

        ex.Message.ShouldBe("boom",
            "the action's exception MUST propagate unchanged — observability is a side channel, not a swallower");

        logger.StartedCalls.Count.ShouldBe(1);
        logger.FailedCalls.Count.ShouldBe(1);
        logger.CompletedCalls.ShouldBeEmpty();

        logger.FailedCalls[0].Error.ShouldBe("boom",
            "external_call.failed payload MUST surface the original exception message");
    }

    [Theory]
    [InlineData(LlmErrorCategory.Transient)]
    [InlineData(LlmErrorCategory.RateLimited)]
    [InlineData(LlmErrorCategory.AuthFailed)]
    [InlineData(LlmErrorCategory.BadRequest)]
    [InlineData(LlmErrorCategory.ContextLengthExceeded)]
    [InlineData(LlmErrorCategory.ContentFiltered)]
    [InlineData(LlmErrorCategory.Malformed)]
    public async Task TraceExternalCallAsync_records_the_thrown_LlmApiExceptions_category_on_the_failed_call(LlmErrorCategory category)
    {
        var logger = new RecordingLogger();
        var store = new RecordingArtifactStore();
        var observability = new NodeObservability(logger, store, RunId, NodeId, TeamId, ParentRecordId);

        await Should.ThrowAsync<LlmApiException>(() => observability.TraceExternalCallAsync<int>(
            target: "https://api.example.com",
            method: "POST",
            requestPayload: null,
            action: _ => throw new LlmApiException("TestProvider", null, category, "boom"),
            cancellationToken: CancellationToken.None));

        logger.FailedCalls.Single().Category.ShouldBe(category.ToString(),
            "a classified LLM transport fault's category rides on the failed record as a structured field — never sniffed from the message");
    }

    [Fact]
    public async Task TraceExternalCallAsync_records_no_category_for_an_unclassified_exception()
    {
        var logger = new RecordingLogger();
        var store = new RecordingArtifactStore();
        var observability = new NodeObservability(logger, store, RunId, NodeId, TeamId, ParentRecordId);

        await Should.ThrowAsync<InvalidOperationException>(() => observability.TraceExternalCallAsync<int>(
            target: "https://api.example.com",
            method: "POST",
            requestPayload: null,
            action: _ => throw new InvalidOperationException("boom"),
            cancellationToken: CancellationToken.None));

        logger.FailedCalls.Single().Category.ShouldBeNull("a non-LlmApiException fault is not a classified transport failure — never a guessed category");
    }

    [Fact]
    public async Task TraceExternalCallAsync_records_no_category_on_a_cancellation()
    {
        var logger = new RecordingLogger();
        var store = new RecordingArtifactStore();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var observability = new NodeObservability(logger, store, RunId, NodeId, TeamId, ParentRecordId);

        await Should.ThrowAsync<OperationCanceledException>(() => observability.TraceExternalCallAsync<int>(
            target: "https://api.example.com",
            method: "POST",
            requestPayload: null,
            action: ct => throw new OperationCanceledException(ct),
            cancellationToken: cts.Token));

        logger.FailedCalls.Single().Category.ShouldBeNull("our own cancellation carries no transport classification");
    }

    [Fact]
    public async Task TraceExternalCallAsync_pairs_started_and_completed_with_same_correlation_id()
    {
        var logger = new RecordingLogger();
        var store = new RecordingArtifactStore();
        var observability = new NodeObservability(logger, store, RunId, NodeId, TeamId, ParentRecordId);

        await observability.TraceExternalCallAsync(
            target: "x",
            method: "GET",
            requestPayload: null,
            action: _ => Task.FromResult("ok"),
            cancellationToken: CancellationToken.None);

        logger.CompletedCalls[0].CorrelationId.ShouldBe(logger.StartedCalls[0].CorrelationId,
            "correlation_id MUST be identical across started+completed so the UI can pair them");
    }

    [Fact]
    public async Task TraceExternalCallAsync_passes_extractor_completion_to_completed_record()
    {
        var logger = new RecordingLogger();
        var store = new RecordingArtifactStore();
        var observability = new NodeObservability(logger, store, RunId, NodeId, TeamId, ParentRecordId);

        await observability.TraceExternalCallAsync(
            target: "x",
            method: "GET",
            requestPayload: null,
            action: _ => Task.FromResult("hello"),
            completionExtractor: result => new ExternalCallCompletion { StatusCode = 200 },
            cancellationToken: CancellationToken.None);

        logger.CompletedCalls[0].StatusCode.ShouldBe(200,
            "completionExtractor MUST be invoked to decorate the completed record with protocol-level outcome");
    }

    [Fact]
    public async Task TraceExternalCallAsync_redacts_persisted_target_request_response_and_error_without_mutating_execution()
    {
        const string secret = "secret-token-123";
        var logger = new RecordingLogger();
        var observability = new NodeObservability(new NodeObservationBinding(logger, new RecordingArtifactStore(), RunId, NodeId, TeamId, ParentRecordId, new PersistenceSecretRedactor([secret])));
        var rawResult = JsonSerializer.SerializeToElement(new { value = "response-" + secret });

        var returned = await observability.TraceExternalCallAsync(
            target: "https://api.example.test?token=" + secret,
            method: "POST",
            requestPayload: JsonSerializer.SerializeToElement(new { auth = secret }),
            action: _ => Task.FromResult(rawResult),
            completionExtractor: result => new ExternalCallCompletion { ResponsePayload = result },
            cancellationToken: CancellationToken.None);

        returned.GetProperty("value").GetString().ShouldContain(secret, Case.Sensitive, "observability must never alter the value returned to node execution");
        logger.StartedCalls.Single().Target.ShouldNotContain(secret);
        logger.StartedCalls.Single().RequestPayload!.Value.GetRawText().ShouldNotContain(secret);
        logger.CompletedCalls.Single().Payload!.Value.GetRawText().ShouldNotContain(secret);

        await Should.ThrowAsync<InvalidOperationException>(() => observability.TraceExternalCallAsync<int>(
            target: secret,
            method: "GET",
            requestPayload: null,
            action: _ => throw new InvalidOperationException("provider echoed " + secret),
            cancellationToken: CancellationToken.None));
        logger.FailedCalls.Single().Error.ShouldNotContain(secret);
        logger.FailedCalls.Single().Target.ShouldBe(PersistenceSecretRedactor.Marker);
    }

    /// <summary>
    /// A host shutdown or an operator cancel while the call is in flight. The cancellation branch used to persist the raw
    /// target while every other branch persisted the redacted one, so a <c>?api_key={{team.SECRET}}</c> reached the
    /// failed record every member reads through the records endpoint, the SSE tail and the timeline.
    /// </summary>
    [Fact]
    public async Task TraceExternalCallAsync_cancelled_mid_call_persists_the_redacted_target()
    {
        const string secret = "CANARY-cancel-call-7d1f0b";
        var logger = new RecordingLogger();
        var observability = new NodeObservability(new NodeObservationBinding(logger, new RecordingArtifactStore(), RunId, NodeId, TeamId, ParentRecordId, new PersistenceSecretRedactor([secret])));
        using var cancellation = new CancellationTokenSource();
        var inFlight = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var call = observability.TraceExternalCallAsync<int>(
            target: "https://api.example.test/hold?api_key=" + secret,
            method: "GET",
            requestPayload: null,
            action: async ct => { inFlight.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); return 0; },
            cancellationToken: cancellation.Token);
        await inFlight.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(() => call);
        var failed = logger.FailedCalls.ShouldHaveSingleItem("the cancelled call still leaves its failed record");
        failed.Target.ShouldBe("https://api.example.test/hold?api_key=" + PersistenceSecretRedactor.Marker);
        failed.Target.ShouldBe(logger.StartedCalls.Single().Target, "the failed record names the call exactly as the started record did");
    }

    [Fact]
    public async Task TraceExternalStreamAsync_cancelled_mid_stream_persists_the_redacted_target()
    {
        const string secret = "CANARY-cancel-stream-3e9a42";
        var logger = new RecordingLogger();
        var observability = new NodeObservability(new NodeObservationBinding(logger, new RecordingArtifactStore(), RunId, NodeId, TeamId, ParentRecordId, new PersistenceSecretRedactor([secret])));
        using var cancellation = new CancellationTokenSource();

        await using var events = observability.TraceExternalStreamAsync("https://api.example.test/stream?api_key=" + secret, "POST", null, HoldAfterFirstEvent, cancellation.Token).GetAsyncEnumerator();
        (await events.MoveNextAsync()).ShouldBeTrue();
        cancellation.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(async () => await events.MoveNextAsync());
        var failed = logger.FailedCalls.ShouldHaveSingleItem("the cancelled stream still leaves its failed record");
        failed.Target.ShouldNotContain(secret);
        failed.Target.ShouldBe(logger.StartedCalls.Single().Target, "the failed record names the stream exactly as the started record did");
    }

    private static async IAsyncEnumerable<int> HoldAfterFirstEvent([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        yield return 1;
        await Task.Delay(Timeout.Infinite, cancellationToken);
        yield return 2;
    }

    /// <summary>
    /// An artifact has no encrypted sidecar: the bytes stored are the bytes every member downloads. So a node's text or
    /// JSON is masked before it is stored, and opaque bytes, which exact matching cannot read, pass through untouched.
    /// </summary>
    [Theory]
    [InlineData("text/plain", "token=CANARY-artifact-55c1 in a log", "token=[REDACTED] in a log")]
    [InlineData("application/json", "{\"auth\":\"CANARY-artifact-55c1\",\"n\":1}", "{\"auth\":\"[REDACTED]\",\"n\":1}")]
    [InlineData("application/json", "{\"auth\":\"CANARY\\u002Dartifact\\u002D55c1\"}", "{\"auth\":\"[REDACTED]\"}")]   // an escaped spelling of the same value
    [InlineData("application/octet-stream", "token=CANARY-artifact-55c1", "token=CANARY-artifact-55c1")]
    [InlineData("text/plain", "nothing secret", "nothing secret")]
    public async Task PersistArtifactAsync_masks_text_and_json_before_storing_and_keeps_opaque_bytes(string contentType, string content, string expected)
    {
        var store = new RecordingArtifactStore();
        var observability = new NodeObservability(new NodeObservationBinding(new RecordingLogger(), store, RunId, NodeId, TeamId, ParentRecordId, new PersistenceSecretRedactor(["CANARY-artifact-55c1"])));

        var reference = await observability.PersistArtifactAsync(contentType, System.Text.Encoding.UTF8.GetBytes(content), CancellationToken.None);

        var stored = System.Text.Encoding.UTF8.GetString(store.PutCalls.Single().Bytes.Span);
        stored.ShouldBe(expected);
        reference.GetProperty("size_bytes").GetInt32().ShouldBe(System.Text.Encoding.UTF8.GetByteCount(stored), "the ref describes the bytes that were stored");
    }

    [Fact]
    public async Task PersistArtifactAsync_delegates_to_store_with_bound_team_id()
    {
        var logger = new RecordingLogger();
        var store = new RecordingArtifactStore();
        var observability = new NodeObservability(logger, store, RunId, NodeId, TeamId, ParentRecordId);

        var bytes = System.Text.Encoding.UTF8.GetBytes("artifact-content");
        var refJson = await observability.PersistArtifactAsync("text/plain", bytes, CancellationToken.None);

        store.PutCalls.Count.ShouldBe(1);
        store.PutCalls[0].TeamId.ShouldBe(TeamId,
            "PersistArtifactAsync MUST scope to the run's owning team — cross-tenant leak otherwise");
        store.PutCalls[0].ContentType.ShouldBe("text/plain");

        // The returned ref shape pins the operator-readable contract: artifact_id + size + content_type.
        var doc = refJson;
        doc.GetProperty("artifact_id").GetGuid().ShouldNotBe(Guid.Empty);
        doc.GetProperty("size_bytes").GetInt32().ShouldBe(bytes.Length);
        doc.GetProperty("content_type").GetString().ShouldBe("text/plain");
    }

    /// <summary>
    /// A git node's review call whose raw <c>/unapprove</c> GitLab refuses while quoting the token it was presented: the
    /// <c>external_call.failed</c> row any member reads must not carry that token. The node's run-scope redactor knows only
    /// the run's own secrets, never the connection's token, so it is the provider that has to strike it — driven here
    /// through the real auth resolver, GitLab provider and resilience layer, against a loopback GitLab.
    /// </summary>
    [Fact]
    public async Task TraceExternalCallAsync_records_a_git_refusal_without_the_token_the_provider_echoed()
    {
        const string token = "glpat-NODE-ECHO-CANARY-9f8e7d6c5b";
        using var gitLab = new CodeSpace.IntegrationTests.Webhooks.StubProviderHost();
        gitLab.Answer("POST", "/merge_requests/7/unapprove", request => new CodeSpace.IntegrationTests.Webhooks.StubProviderHost.StubReply(401, $"{{\"message\":\"401 Unauthorized (PRIVATE-TOKEN={request.PrivateTokenHeader})\"}}"));
        var (provider, context) = GitLabOver(gitLab.BaseUrl, token);
        var repository = new CodeSpace.Messages.Dtos.Providers.RemoteRepository { ExternalId = "4242", NamespacePath = "acme", Name = "api", FullPath = "acme/api", DefaultBranch = "main", Visibility = CodeSpace.Messages.Enums.RepositoryVisibility.Private, WebUrl = "https://gitlab.test/acme/api" };
        var review = new CodeSpace.Messages.Dtos.Providers.SubmitPullRequestReviewInput { Verdict = CodeSpace.Messages.Enums.PullRequestReviewVerdict.RequestChanges, Body = "needs work" };
        var logger = new RecordingLogger();
        var observability = new NodeObservability(new NodeObservationBinding(logger, new RecordingArtifactStore(), RunId, NodeId, TeamId, ParentRecordId, new PersistenceSecretRedactor(["a-run-secret-not-the-token"])));

        await Should.ThrowAsync<CodeSpace.Messages.Exceptions.ProviderApiException>(() => observability.TraceExternalCallAsync(target: "git.submit_review:acme/api:7", method: "submit_review", requestPayload: null, action: ct => provider.SubmitReviewAsync(context, repository, 7, review, ct), completionExtractor: _ => new ExternalCallCompletion(), cancellationToken: CancellationToken.None));

        gitLab.Requests.ShouldContain(request => request.PrivateTokenHeader == token, "fixture check: GitLab was presented the token, so its 401 quoted it");
        var failed = logger.FailedCalls.ShouldHaveSingleItem();
        failed.Error.ShouldContain("401 Unauthorized", Case.Sensitive, "GitLab's own words still explain the failure");
        failed.Error.ShouldNotContain(token, Case.Sensitive);
    }

    private static (CodeSpace.Core.Services.Providers.GitLab.GitLabRepositoryProvider Provider, CodeSpace.Core.Services.Providers.ProviderContext Context) GitLabOver(string baseUrl, string token)
    {
        var instance = new CodeSpace.Core.Persistence.Entities.ProviderInstance { Id = Guid.NewGuid(), TeamId = TeamId, Provider = CodeSpace.Messages.Enums.ProviderKind.GitLab, DisplayName = "loopback", BaseUrl = baseUrl };
        var credential = new CodeSpace.Core.Persistence.Entities.Credential { Id = Guid.NewGuid(), TeamId = TeamId, ProviderInstanceId = instance.Id, AuthType = CodeSpace.Messages.Enums.AuthType.Pat, DisplayName = "pat", EncryptedPayload = "unused", Status = CodeSpace.Messages.Enums.CredentialStatus.Active };
        var resolver = new CodeSpace.Core.Services.Providers.Auth.ProviderAuthResolver([new FixedGitLabToken(token)]);
        var resilience = new CodeSpace.Core.Services.Providers.Resilience.ExternalCallResilience(new CodeSpace.Core.Services.Providers.Errors.ProviderErrorMapperRegistry([new CodeSpace.Core.Services.Providers.GitLab.GitLabErrorMapper()]), Microsoft.Extensions.Logging.Abstractions.NullLogger<CodeSpace.Core.Services.Providers.Resilience.ExternalCallResilience>.Instance);
        var normalizer = new CodeSpace.Core.Services.Providers.GitLab.GitLabEventNormalizer(new CodeSpace.Core.Services.Providers.Events.ProviderEventSubscriptionRegistry([]));
        var provider = new CodeSpace.Core.Services.Providers.GitLab.GitLabRepositoryProvider(resolver, resilience, new CodeSpace.Core.Services.Providers.GitLab.GitLabSignatureVerifier(), normalizer, new CodeSpace.Core.Services.Providers.GitLab.GitLabWebhookRepositoryIdentifier());

        return (provider, new CodeSpace.Core.Services.Providers.ProviderContext(instance, credential));
    }

    private sealed class FixedGitLabToken(string token) : CodeSpace.Core.Services.Providers.Auth.IProviderAuthStrategy
    {
        public CodeSpace.Messages.Enums.ProviderKind Kind => CodeSpace.Messages.Enums.ProviderKind.GitLab;

        public CodeSpace.Messages.Enums.AuthType AuthType => CodeSpace.Messages.Enums.AuthType.Pat;

        public Task<CodeSpace.Core.Services.Providers.Auth.ResolvedAuth> ResolveAsync(CodeSpace.Core.Services.Providers.ProviderContext context, CancellationToken cancellationToken) => Task.FromResult(new CodeSpace.Core.Services.Providers.Auth.ResolvedAuth { Token = token });
    }

    // ─── Hand-rolled recording doubles ──────────────────────────────────────────

    private sealed class RecordingLogger : IRunRecordLogger
    {
        public List<(Guid RecordId, Guid CorrelationId, string Target, string Method, JsonElement? RequestPayload, Guid? ParentRecordId)> StartedCalls { get; } = new();
        public List<(Guid CorrelationId, int? StatusCode, JsonElement? Payload)> CompletedCalls { get; } = new();
        public List<(Guid CorrelationId, string Target, string Error, string? Category)> FailedCalls { get; } = new();

        public Task<(Guid RecordId, Guid CorrelationId)> ExternalCallStartedAsync(Guid runId, string? nodeId, string target, string method, JsonElement? requestPayload, Guid? parentRecordId, CancellationToken cancellationToken)
        {
            var recordId = Guid.NewGuid();
            var correlationId = Guid.NewGuid();
            StartedCalls.Add((recordId, correlationId, target, method, requestPayload, parentRecordId));
            return Task.FromResult((recordId, correlationId));
        }

        public Task ExternalCallCompletedAsync(Guid runId, string? nodeId, Guid correlationId, int? statusCode, JsonElement? responsePayload, TimeSpan duration, CancellationToken cancellationToken)
        {
            CompletedCalls.Add((correlationId, statusCode, responsePayload));
            return Task.CompletedTask;
        }

        public Task ExternalCallFailedAsync(Guid runId, string? nodeId, Guid correlationId, string target, string error, TimeSpan duration, string? category, CancellationToken cancellationToken)
        {
            FailedCalls.Add((correlationId, target, error, category));
            return Task.CompletedTask;
        }

        // ─── Unused members — these tests only exercise external_call.* ─────────
        public Task<Guid> RecordInteractionAsync(Guid runId, string recordType, string? nodeId, string iterationKey, Guid correlationId, Guid? parentRecordId, JsonElement payload, CancellationToken cancellationToken) => Task.FromResult(Guid.NewGuid());
        public Task RunQueuedAsync(Guid runId, string sourceType, Guid? actorId, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task RunStartedAsync(Guid runId, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ReleaseLoadedAsync(Guid runId, int version, string definitionHash, int nodeCount, int edgeCount, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ScopeResolvedAsync(Guid runId, int wfCount, int teamCount, int sysCount, int secretPathCount, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task VariablesSnapshottedAsync(Guid runId, int wfCount, int teamCount, string releaseHash, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task RunCompletedAsync(Guid runId, TimeSpan duration, bool outputsPresent, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task RunFailedAsync(Guid runId, string error, TimeSpan duration, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task RunCancelledAsync(Guid runId, TimeSpan duration, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task RunReplayedAsync(Guid runId, Guid? parentRunId, int snapshotCount, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task SupervisorRunRecoveredAsync(Guid runId, int attempt, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<Guid> NodeStartedAsync(Guid runId, string nodeId, string iterationKey, IReadOnlyDictionary<string, JsonElement> resolvedInputs, IReadOnlyDictionary<string, JsonElement> resolvedConfig, CancellationToken cancellationToken) => Task.FromResult(Guid.NewGuid());
        public Task<Guid> NodeCompletedAsync(Guid runId, string nodeId, string iterationKey, IReadOnlyDictionary<string, JsonElement> outputs, IReadOnlyList<string>? routingHints, TimeSpan duration, CancellationToken cancellationToken) => Task.FromResult(Guid.Empty);
        public Task NodeFailedAsync(Guid runId, string nodeId, string iterationKey, string error, TimeSpan duration, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task AttemptFailedAsync(Guid runId, string nodeId, string iterationKey, int attempt, int maxAttempts, string error, TimeSpan duration, double retryInSeconds, Guid? parentRecordId, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task NodeSkippedAsync(Guid runId, string nodeId, string iterationKey, string reason, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task NodeStorageUnavailableAsync(Guid runId, string nodeId, string iterationKey, string reason, string code, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task NodeSuspendedAsync(Guid runId, string nodeId, string iterationKey, string waitKind, DateTimeOffset? wakeAt, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task IterationStartedAsync(Guid runId, string nodeId, int itemCount, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task IterationCompletedAsync(Guid runId, string nodeId, int itemCount, TimeSpan duration, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task LogAsync(Guid runId, string? nodeId, LogLevel level, string message, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task WaitReissuedAsync(Guid runId, string nodeId, string iterationKey, string waitKind, Guid waitId, Guid byUserId, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class RecordingArtifactStore : IArtifactStore
    {
        public List<(Guid TeamId, ReadOnlyMemory<byte> Bytes, string ContentType)> PutCalls { get; } = new();

        public Task<Guid> PutAsync(Guid teamId, ReadOnlyMemory<byte> bytes, string contentType, CancellationToken cancellationToken)
        {
            PutCalls.Add((teamId, bytes, contentType));
            return Task.FromResult(Guid.NewGuid());
        }

        public Task<ArtifactBytes?> GetBytesAsync(Guid teamId, Guid artifactId, CancellationToken cancellationToken) => Task.FromResult<ArtifactBytes?>(null);
        public Task<ArtifactMetadata?> GetMetadataAsync(Guid teamId, Guid artifactId, CancellationToken cancellationToken) => Task.FromResult<ArtifactMetadata?>(null);
    }
}
