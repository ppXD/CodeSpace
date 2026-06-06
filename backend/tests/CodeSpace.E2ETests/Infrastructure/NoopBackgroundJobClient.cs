using System.Linq.Expressions;
using CodeSpace.Core.Constants;
using CodeSpace.Core.Services.Jobs;
using Hangfire.Storage;

namespace CodeSpace.E2ETests.Infrastructure;

/// <summary>
/// E2E job client that runs nothing — every Enqueue/Schedule returns a fake id so the dispatcher's
/// run-creation path completes, but no real Hangfire job is created and no workflow engine executes.
/// The E2E tier asserts the HTTP contract + the synchronously-created <c>workflow_run</c> row, not
/// background execution (which is covered by the engine integration tests). Registered last in the
/// factory's <c>ConfigureTestContainer</c> so it wins over the real Hangfire-backed client.
/// </summary>
public sealed class NoopBackgroundJobClient : ICodeSpaceBackgroundJobClient
{
    public string Enqueue(Expression<Func<Task>> methodCall, string queue = HangfireConstants.DefaultQueue) => "noop";
    public string Enqueue<T>(Expression<Func<T, Task>> methodCall, string queue = HangfireConstants.DefaultQueue) => "noop";
    public string Enqueue<T>(Expression<Action> methodCall, string queue = HangfireConstants.DefaultQueue) => "noop";
    public string Enqueue<T>(Expression<Action<T>> methodCall, string queue = HangfireConstants.DefaultQueue) => "noop";
    public string Schedule(Expression<Func<Task>> methodCall, TimeSpan delay, string queue = HangfireConstants.DefaultQueue) => "noop";
    public string Schedule(Expression<Func<Task>> methodCall, DateTimeOffset enqueueAt, string queue = HangfireConstants.DefaultQueue) => "noop";
    public string Schedule<T>(Expression<Func<T, Task>> methodCall, TimeSpan delay, string queue = HangfireConstants.DefaultQueue) => "noop";
    public string Schedule<T>(Expression<Func<T, Task>> methodCall, DateTimeOffset enqueueAt, string queue = HangfireConstants.DefaultQueue) => "noop";
    public string ContinueJobWith(string parentJobId, Expression<Func<Task>> methodCall, string queue = HangfireConstants.DefaultQueue) => "noop";
    public string ContinueJobWith<T>(string parentJobId, Expression<Func<T, Task>> methodCall, string queue = HangfireConstants.DefaultQueue) => "noop";
    public void AddOrUpdateRecurringJob<T>(string recurringJobId, Expression<Func<T, Task>> methodCall, string cronExpression, TimeZoneInfo? timeZone = null, string queue = HangfireConstants.DefaultQueue) { }
    public bool DeleteJob(string jobId) => true;
    public void RemoveRecurringJobIfExists(string jobId) { }
    public List<RecurringJobDto> GetRecurringJobs() => new();
    public StateData? GetJobState(string jobId) => null;
}
