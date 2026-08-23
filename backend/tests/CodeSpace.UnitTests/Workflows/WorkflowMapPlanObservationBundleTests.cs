using CodeSpace.Core.Services.Workflows.Display;
using CodeSpace.Messages.Dtos.Workflows;
using Microsoft.AspNetCore.Http;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

[Trait("Category", "Unit")]
public sealed class WorkflowMapPlanObservationBundleTests
{
    [Fact]
    public async Task Same_team_and_run_share_one_read_while_distinct_identity_reads_independently()
    {
        var reader = new CountingReader();
        await using var bundle = new WorkflowMapPlanObservationBundle(reader, new HttpContextAccessor());
        var teamId = Guid.NewGuid();
        var runId = Guid.NewGuid();

        await bundle.GetAsync(runId, teamId, CancellationToken.None);
        await bundle.GetAsync(runId, teamId, CancellationToken.None);
        await bundle.GetAsync(Guid.NewGuid(), teamId, CancellationToken.None);

        reader.Count.ShouldBe(2);
        reader.Requests.ShouldAllBe(value => value.Scope == WorkflowRunViewScope.LineageMerged);
    }

    private sealed class CountingReader : IWorkflowMapPlanObservationReader
    {
        public int Count;
        public List<WorkflowMapPlanObservationRequest> Requests { get; } = new();

        public Task<WorkflowMapPlanObservation?> ReadAsync(WorkflowMapPlanObservationRequest request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Count);
            Requests.Add(request);
            return Task.FromResult<WorkflowMapPlanObservation?>(new WorkflowMapPlanObservation
            {
                RunId = request.RunId, Availability = WorkflowRunViewAvailability.Available, AnchorAt = DateTimeOffset.UnixEpoch,
                Planners = Array.Empty<WorkflowMapPlannerObservation>(),
            });
        }
    }
}
