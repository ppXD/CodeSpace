using System.Text;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Workflows.Artifacts;
using CodeSpace.Core.Services.Workflows.ModelCalls;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Commands.Workflows;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Contracts;
using CodeSpace.Messages.Dtos.Workflows.ModelCalls;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows.ModelCalls;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class WorkflowRunModelCallReaderV2Tests
{
    private readonly PostgresFixture _fixture;

    public WorkflowRunModelCallReaderV2Tests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Stable_metadata_is_exactly_scoped_metadata_only_and_preserves_physical_attempts()
    {
        var world = await SeedProjectedCallAsync();
        var (foreignTeamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        using var scope = _fixture.BeginScope();
        var reader = scope.Resolve<IWorkflowRunModelCallReader>();

        var metadata = await reader.ReadByIdAsync(world.RunId, world.CallId, world.TeamId, CancellationToken.None);

        metadata.ShouldNotBeNull("metadata must not dereference the deliberately missing response artifact");
        metadata!.WorkflowRunModelCallId.ShouldBe(world.CallId);
        metadata.RunId.ShouldBe(world.RunId);
        metadata.CallOrdinal.ShouldBe(17);
        metadata.Purpose.ShouldBe("supervisor.decision/v1");
        metadata.CaptureCompleteness.ShouldBe(WorkflowRunCaptureCompleteness.Partial);
        metadata.Attempts.Select(value => value.AttemptOrdinal).ShouldBe([1, 2]);
        metadata.Attempts[0].Status.ShouldBe("Failed");
        metadata.Attempts[0].CaptureCompleteness.ShouldBe(WorkflowRunCaptureCompleteness.Corrupt);
        metadata.Attempts[0].SourceEvidence.ShouldBe(WorkflowRunModelCallSourceEvidence.StartedAndTerminal);
        metadata.Attempts[0].Bodies.Single(value => value.Body == WorkflowRunModelCallBody.AttemptError).ReferenceState
            .ShouldBe(WorkflowRunModelCallBodyReferenceState.Corrupt);
        metadata.Attempts[1].SourceEvidence.ShouldBe(WorkflowRunModelCallSourceEvidence.LateStartAttached);
        metadata.Attempts[1].Usage.InputTokens.ShouldBe(50_001);
        metadata.Attempts[1].Usage.OutputTokens.ShouldBe(1_234);
        metadata.Attempts[1].UnavailableFigures.ShouldBe([
            ModelCallFigures.CacheReadTokens,
            ModelCallFigures.CostAmount,
            ModelCallFigures.ReasoningTokens,
        ]);
        var missingResponse = metadata.Attempts[1].Bodies.Single(value => value.Body == WorkflowRunModelCallBody.AttemptResponse);
        missingResponse.ReferenceState.ShouldBe(WorkflowRunModelCallBodyReferenceState.Referenced);
        missingResponse.CaptureHealth.ShouldBe(WorkflowRunModelCallBodyCaptureHealth.Available,
            "stable metadata reports durable capture state without reading the deliberately missing object bytes");
        missingResponse.MaterializationFormat.ShouldBe(WorkflowRunModelCallBodyMaterializationFormats.ExternalArtifact);
        metadata.Bodies.Single().ReferenceState.ShouldBe(WorkflowRunModelCallBodyReferenceState.Partial);
        metadata.Bodies.Concat(metadata.Attempts.SelectMany(value => value.Bodies)).Where(value => value != missingResponse).ShouldAllBe(value =>
            value.CaptureHealth == null && value.MaterializationFormat == null,
            "stable bodies without durable capture intents must not invent materializer health or format");

        var compatibility = await reader.ReadMetadataAsync(world.RunId, world.TerminalSequence, world.TeamId, CancellationToken.None);
        compatibility.ShouldNotBeNull();
        compatibility!.WorkflowRunModelCallId.ShouldBe(world.CallId, "the sequence route exposes the stable id once projection admission catches up");
        compatibility.ProjectionState.ShouldBe(WorkflowRunModelCallProjectionState.Projected);
        compatibility.CaptureCompleteness.ShouldBe(WorkflowRunCaptureCompleteness.Partial);

        (await reader.ReadByIdAsync(Guid.NewGuid(), world.CallId, world.TeamId, CancellationToken.None)).ShouldBeNull();
        (await reader.ReadByIdAsync(world.RunId, world.CallId, foreignTeamId, CancellationToken.None)).ShouldBeNull();
    }

    [Fact]
    public async Task Stable_body_reads_are_bounded_and_never_promote_missing_or_uncaptured_bytes_to_available()
    {
        var world = await SeedProjectedCallAsync();
        using var scope = _fixture.BeginScope();
        var reader = scope.Resolve<IWorkflowRunModelCallReader>();

        var missing = await reader.ReadBodyAsync(new WorkflowRunModelCallBodyReadRequest(world.RunId, world.CallId, world.TeamId, WorkflowRunModelCallBody.AttemptResponse)
        {
            AttemptId = world.SucceededAttemptId,
            LimitBytes = 4096,
        }, CancellationToken.None);
        missing.ShouldNotBeNull();
        missing!.Availability.ShouldBe(WorkflowRunModelCallPartAvailability.PhysicalObjectMissing);
        missing.Text.ShouldBeNull();

        var captured = new StringBuilder();
        long offset = 0;
        var pages = 0;
        while (true)
        {
            var available = await reader.ReadBodyAsync(new WorkflowRunModelCallBodyReadRequest(world.RunId, world.CallId, world.TeamId, WorkflowRunModelCallBody.AttemptRequest)
            {
                AttemptId = world.FailedAttemptId,
                OffsetBytes = offset,
                LimitBytes = 4096,
            }, CancellationToken.None);
            available.ShouldNotBeNull();
            available!.Availability.ShouldBe(WorkflowRunModelCallPartAvailability.Available);
            available.CaptureCompleteness.ShouldBe(WorkflowRunCaptureCompleteness.Corrupt,
                "physical readability must not hide that the attempt capture itself is corrupt");
            available.ReturnedBytes.ShouldBeLessThanOrEqualTo(4096);
            captured.Append(available.Text);
            pages++;
            if (available.NextOffsetBytes is not { } next) break;
            next.ShouldBeGreaterThan(offset);
            offset = next;
        }
        pages.ShouldBeGreaterThan(20);
        captured.ToString().ShouldBe(world.CapturedRequest);

        var partial = await reader.ReadBodyAsync(new WorkflowRunModelCallBodyReadRequest(world.RunId, world.CallId, world.TeamId, WorkflowRunModelCallBody.LogicalRequest), CancellationToken.None);
        partial.ShouldNotBeNull();
        partial!.Availability.ShouldBe(WorkflowRunModelCallPartAvailability.CapturePartial);
        partial.Text.ShouldBeNull();

        var corrupt = await reader.ReadBodyAsync(new WorkflowRunModelCallBodyReadRequest(world.RunId, world.CallId, world.TeamId, WorkflowRunModelCallBody.AttemptError)
        {
            AttemptId = world.FailedAttemptId,
        }, CancellationToken.None);
        corrupt.ShouldNotBeNull();
        corrupt!.Availability.ShouldBe(WorkflowRunModelCallPartAvailability.CaptureCorrupt);
        corrupt.Text.ShouldBeNull();

        var invalid = await reader.ReadBodyAsync(new WorkflowRunModelCallBodyReadRequest(world.RunId, world.CallId, world.TeamId,
            WorkflowRunModelCallBody.AttemptResponse), CancellationToken.None);
        invalid.ShouldNotBeNull();
        invalid!.Availability.ShouldBe(WorkflowRunModelCallPartAvailability.InvalidBodyReference);

        (await reader.ReadBodyAsync(new WorkflowRunModelCallBodyReadRequest(world.RunId, world.CallId, world.TeamId, WorkflowRunModelCallBody.AttemptResponse)
        {
            AttemptId = Guid.NewGuid(),
        }, CancellationToken.None)).ShouldBeNull("an attempt outside the exact call scope is indistinguishable from absent");
    }

    private async Task<ReaderWorld> SeedProjectedCallAsync()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        Guid workflowId;
        using (var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin))
        {
            workflowId = await scope.Resolve<MediatR.IMediator>().Send(new CreateWorkflowCommand
            {
                Name = "model-call-reader-v2-" + Guid.NewGuid().ToString("N")[..8],
                Definition = WorkflowsTestSeed.MinimalDefinition(),
                Activations = new List<WorkflowActivationInput>(),
                Enabled = true,
            });
        }

        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);
        var correlationId = Guid.NewGuid();
        var started1 = Record(runId, WorkflowRunRecordTypes.InteractionStarted, correlationId);
        var terminal1 = Record(runId, WorkflowRunRecordTypes.InteractionFailed, correlationId);
        var started2 = Record(runId, WorkflowRunRecordTypes.InteractionStarted, correlationId);
        var terminal2 = Record(runId, WorkflowRunRecordTypes.InteractionCompleted, correlationId);
        var missingArtifactId = Guid.NewGuid();
        var capturedRequest = string.Concat(Enumerable.Repeat("模型請求🙂\n", 18_000));
        var availableBytes = Encoding.UTF8.GetBytes(capturedRequest);
        var callId = Guid.NewGuid();
        var failedAttemptId = Guid.NewGuid();
        var succeededAttemptId = Guid.NewGuid();

        using var seedScope = _fixture.BeginScope();
        var db = seedScope.Resolve<CodeSpaceDbContext>();
        var availableArtifactId = await seedScope.Resolve<IArtifactStore>().PutAsync(teamId, availableBytes, "application/json", CancellationToken.None);
        db.WorkflowRunRecord.AddRange(started1, terminal1, started2, terminal2);
        db.WorkflowArtifact.Add(
            new WorkflowArtifact
            {
                Id = missingArtifactId,
                TeamId = teamId,
                Sha256 = new string('a', 64),
                ContentType = "application/json",
                SizeBytes = 200_000,
                StorageUrl = new Uri(Path.Combine(CodeSpace.Core.Settings.DurableRoots.ArtifactStore(CodeSpace.Core.Settings.RuntimeSettings.Current.ArtifactStoreDirectory),
                    "aa", "aa", Guid.NewGuid().ToString("N"))).AbsoluteUri,
            });
        db.WorkflowRunModelCall.Add(new WorkflowRunModelCall
        {
            Id = callId,
            TeamId = teamId,
            WorkflowRunId = runId,
            NodeId = "sup",
            IterationKey = "sup#turn1",
            CallOrdinal = 17,
            SourceKind = WorkflowRunModelCallProjector.SourceKind,
            SourceCorrelationId = correlationId,
            Purpose = "supervisor.decision/v1",
            RequestedProvider = "auto",
            RequestedModel = "reasoner",
            CaptureSource = WorkflowRunModelCallProjector.SourceKind,
            CaptureCompleteness = WorkflowRunCaptureCompleteness.Partial,
        });
        db.WorkflowRunModelCallAttempt.AddRange(
            new WorkflowRunModelCallAttempt
            {
                Id = failedAttemptId,
                TeamId = teamId,
                WorkflowRunId = runId,
                ModelCallId = callId,
                AttemptOrdinal = 1,
                SourceStartedRecordId = started1.Id,
                SourceTerminalRecordId = terminal1.Id,
                SourceEvidenceRevision = 1,
                EffectiveProvider = "provider-a",
                EffectiveModel = "model-a",
                RequestArtifactId = availableArtifactId,
                Status = "Failed",
                ErrorCode = "Transport",
                CaptureSource = WorkflowRunModelCallProjector.SourceKind,
                CaptureCompleteness = WorkflowRunCaptureCompleteness.Corrupt,
                StartedAt = started1.OccurredAt,
                CompletedAt = terminal1.OccurredAt,
            },
            new WorkflowRunModelCallAttempt
            {
                Id = succeededAttemptId,
                TeamId = teamId,
                WorkflowRunId = runId,
                ModelCallId = callId,
                AttemptOrdinal = 2,
                SourceStartedRecordId = started2.Id,
                SourceTerminalRecordId = terminal2.Id,
                SourceEvidenceRevision = 2,
                EffectiveProvider = "provider-b",
                EffectiveModel = "model-b",
                ResponseArtifactId = missingArtifactId,
                Status = "Succeeded",
                FinishReason = "stop",
                CaptureSource = WorkflowRunModelCallProjector.SourceKind,
                CaptureCompleteness = WorkflowRunCaptureCompleteness.Partial,
                InputTokens = 50_001,
                OutputTokens = 1_234,
                UnavailableFigures = [ModelCallFigures.CacheReadTokens, ModelCallFigures.CostAmount, ModelCallFigures.ReasoningTokens],
                StartedAt = started2.OccurredAt,
                CompletedAt = terminal2.OccurredAt,
            });
        db.WorkflowRunModelCallBodyCapture.Add(new WorkflowRunModelCallBodyCapture
        {
            Id = Guid.NewGuid(),
            TeamId = teamId,
            WorkflowRunId = runId,
            ModelCallId = callId,
            ModelCallAttemptId = succeededAttemptId,
            BodyKind = WorkflowRunModelCallBodyKind.AttemptResponse,
            SourceKind = WorkflowRunModelCallProjector.SourceKind,
            SourceRecordId = terminal2.Id,
            SourceProperty = "output",
            State = WorkflowRunModelCallBodyCaptureState.Available,
            ArtifactId = missingArtifactId,
            SourceSha256 = new string('a', 64),
            SizeBytes = 200_000,
            ContentType = "application/json",
            MaterializationFormat = WorkflowRunModelCallBodyMaterializationFormats.ExternalArtifact,
            NextMaterializationAt = terminal2.OccurredAt,
            Revision = 1,
            CreatedAt = terminal2.OccurredAt,
            LastModifiedAt = terminal2.OccurredAt,
            TerminalAt = terminal2.OccurredAt,
        });
        await db.SaveChangesAsync();
        return new ReaderWorld
        {
            RunId = runId,
            TeamId = teamId,
            CallId = callId,
            FailedAttemptId = failedAttemptId,
            SucceededAttemptId = succeededAttemptId,
            TerminalSequence = terminal2.Sequence,
            CapturedRequest = capturedRequest,
        };
    }

    private static WorkflowRunRecord Record(Guid runId, string recordType, Guid correlationId) => new()
    {
        Id = Guid.NewGuid(),
        RunId = runId,
        RecordType = recordType,
        NodeId = "sup",
        IterationKey = "sup#turn1",
        CorrelationId = correlationId,
        OccurredAt = DateTimeOffset.UtcNow,
        PayloadJson = "{}",
    };

    private sealed record ReaderWorld
    {
        public required Guid RunId { get; init; }
        public required Guid TeamId { get; init; }
        public required Guid CallId { get; init; }
        public required Guid FailedAttemptId { get; init; }
        public required Guid SucceededAttemptId { get; init; }
        public required long TerminalSequence { get; init; }
        public required string CapturedRequest { get; init; }
    }
}
