using CodeSpace.Core.Jobs;
using CodeSpace.Core.Jobs.RecurringJobs;
using CodeSpace.Core.Services.Workflows.ToolCalls;
using CodeSpace.Messages.Commands.Workflows;
using MediatR;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows.ToolCalls;

[Trait("Category", "Unit")]
public sealed class WorkflowRunToolCallProjectorContractTests
{
    [Fact]
    public async Task Projection_is_scoped_bounded_and_recurs_through_the_generic_job_contract()
    {
        typeof(IWorkflowRunToolCallProjector).GetInterfaces().Select(value => value.Name).ShouldContain("IScopedDependency");
        typeof(IWorkflowRunToolCallProjector).GetMethods().Select(value => value.Name).ShouldBe(["SweepAsync"]);
        typeof(WorkflowRunToolCallProjectionRecurringJob).GetInterfaces().ShouldContain(typeof(IRecurringJob));
        new ProjectWorkflowRunToolCallsCommand().BatchSize.ShouldBe(250);

        var mediator = new RecordingMediator();
        var job = new WorkflowRunToolCallProjectionRecurringJob(mediator);
        job.JobId.ShouldBe(nameof(WorkflowRunToolCallProjectionRecurringJob));
        job.CronExpression.ShouldBe("* * * * *");
        await job.Execute();
        mediator.Sent.ShouldHaveSingleItem().ShouldBeOfType<ProjectWorkflowRunToolCallsCommand>();
    }

    [Fact]
    public void Diagnostic_result_names_bounded_observations_instead_of_cumulative_claims()
    {
        var result = new WorkflowRunToolCallProjectionResult { CallsProjected = 2, DiagnosticRowsObserved = 8 };
        result.CallsProjected.ShouldBe(2);
        result.DiagnosticRowsObserved.ShouldBe(8);
        typeof(WorkflowRunToolCallProjectionResult).GetProperties().Select(value => value.Name)
            .ShouldAllBe(name => name == nameof(WorkflowRunToolCallProjectionResult.CallsProjected)
                || name.EndsWith("Observed", StringComparison.Ordinal));
    }

    [Fact]
    public void Candidate_contract_is_bounded_terminal_only_and_never_selects_authority_or_body_columns()
    {
        var sql = WorkflowRunToolCallProjector.CandidateSql;
        sql.ShouldContain("LIMIT @batch_size");
        sql.ShouldContain("status IN ('Succeeded', 'Failed', 'Denied', 'Expired')");
        sql.ShouldContain("admission_ordinal IS NOT NULL");
        sql.ShouldContain("tool_kind <> 'decision.request'");
        sql.ShouldNotContain("ROW_NUMBER");
        sql.ShouldNotContain("OFFSET @");
        sql.ShouldNotContain("result_jsonb");
        sql.ShouldNotContain("error");
        sql.ShouldNotContain("input_hash");
        sql.ShouldNotContain("idempotency_key");
        sql.ShouldNotContain("approval_token");
        sql.ShouldNotContain("decision_envelope");
    }

    private sealed class RecordingMediator : IMediator
    {
        public List<object> Sent { get; } = [];

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            Sent.Add(request);
            return Task.FromResult(default(TResponse)!);
        }

        public Task<object?> Send(object request, CancellationToken cancellationToken = default)
        {
            Sent.Add(request);
            return Task.FromResult<object?>(null);
        }

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest
        {
            Sent.Add(request);
            return Task.CompletedTask;
        }

        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task Publish(object notification, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default) where TNotification : INotification => throw new NotSupportedException();
    }
}
