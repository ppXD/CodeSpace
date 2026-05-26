using System.Linq.Expressions;
using CodeSpace.Core.Constants;
using CodeSpace.Core.DependencyInjection;
using Hangfire;
using Hangfire.States;
using Hangfire.Storage;

namespace CodeSpace.Core.Services.Jobs;

/// <summary>
/// Abstraction over Hangfire's job/recurring APIs. Hides the framework so callers stay
/// portable + integration tests can substitute an in-memory recorder to assert dispatch
/// behaviour without spinning up a real Hangfire server.
/// </summary>
public interface ICodeSpaceBackgroundJobClient : IScopedDependency
{
    string Enqueue(Expression<Func<Task>> methodCall, string queue = HangfireConstants.DefaultQueue);
    string Enqueue<T>(Expression<Func<T, Task>> methodCall, string queue = HangfireConstants.DefaultQueue);
    string Enqueue<T>(Expression<Action> methodCall, string queue = HangfireConstants.DefaultQueue);
    string Enqueue<T>(Expression<Action<T>> methodCall, string queue = HangfireConstants.DefaultQueue);

    string Schedule(Expression<Func<Task>> methodCall, TimeSpan delay, string queue = HangfireConstants.DefaultQueue);
    string Schedule(Expression<Func<Task>> methodCall, DateTimeOffset enqueueAt, string queue = HangfireConstants.DefaultQueue);
    string Schedule<T>(Expression<Func<T, Task>> methodCall, TimeSpan delay, string queue = HangfireConstants.DefaultQueue);
    string Schedule<T>(Expression<Func<T, Task>> methodCall, DateTimeOffset enqueueAt, string queue = HangfireConstants.DefaultQueue);

    string ContinueJobWith(string parentJobId, Expression<Func<Task>> methodCall, string queue = HangfireConstants.DefaultQueue);
    string ContinueJobWith<T>(string parentJobId, Expression<Func<T, Task>> methodCall, string queue = HangfireConstants.DefaultQueue);

    void AddOrUpdateRecurringJob<T>(string recurringJobId, Expression<Func<T, Task>> methodCall, string cronExpression, TimeZoneInfo? timeZone = null, string queue = HangfireConstants.DefaultQueue);

    bool DeleteJob(string jobId);
    void RemoveRecurringJobIfExists(string jobId);

    List<RecurringJobDto> GetRecurringJobs();
    StateData? GetJobState(string jobId);
}

public class CodeSpaceBackgroundJobClient : ICodeSpaceBackgroundJobClient
{
    private readonly Func<IBackgroundJobClient> _backgroundJobClientFunc;
    private readonly Func<IRecurringJobManager> _recurringJobManagerFunc;

    public CodeSpaceBackgroundJobClient(
        Func<IBackgroundJobClient> backgroundJobClientFunc,
        Func<IRecurringJobManager> recurringJobManagerFunc)
    {
        _backgroundJobClientFunc = backgroundJobClientFunc;
        _recurringJobManagerFunc = recurringJobManagerFunc;
    }

    public string Enqueue(Expression<Func<Task>> methodCall, string queue = HangfireConstants.DefaultQueue) =>
        _backgroundJobClientFunc().Create(methodCall, new EnqueuedState(queue));

    public string Enqueue<T>(Expression<Func<T, Task>> methodCall, string queue = HangfireConstants.DefaultQueue) =>
        _backgroundJobClientFunc().Create(methodCall, new EnqueuedState(queue));

    public string Enqueue<T>(Expression<Action> methodCall, string queue = HangfireConstants.DefaultQueue) =>
        _backgroundJobClientFunc().Create(methodCall, new EnqueuedState(queue));

    public string Enqueue<T>(Expression<Action<T>> methodCall, string queue = HangfireConstants.DefaultQueue) =>
        _backgroundJobClientFunc().Create(methodCall, new EnqueuedState(queue));

    public string Schedule(Expression<Func<Task>> methodCall, TimeSpan delay, string queue = HangfireConstants.DefaultQueue) =>
        _backgroundJobClientFunc().Schedule(queue, methodCall, delay);

    public string Schedule(Expression<Func<Task>> methodCall, DateTimeOffset enqueueAt, string queue = HangfireConstants.DefaultQueue) =>
        _backgroundJobClientFunc().Schedule(queue, methodCall, enqueueAt);

    public string Schedule<T>(Expression<Func<T, Task>> methodCall, TimeSpan delay, string queue = HangfireConstants.DefaultQueue) =>
        _backgroundJobClientFunc().Schedule(queue, methodCall, delay);

    public string Schedule<T>(Expression<Func<T, Task>> methodCall, DateTimeOffset enqueueAt, string queue = HangfireConstants.DefaultQueue) =>
        _backgroundJobClientFunc().Schedule(queue, methodCall, enqueueAt);

    public string ContinueJobWith(string parentJobId, Expression<Func<Task>> methodCall, string queue = HangfireConstants.DefaultQueue) =>
        _backgroundJobClientFunc().ContinueJobWith(parentJobId, methodCall, new EnqueuedState(queue));

    public string ContinueJobWith<T>(string parentJobId, Expression<Func<T, Task>> methodCall, string queue = HangfireConstants.DefaultQueue) =>
        _backgroundJobClientFunc().ContinueJobWith(parentJobId, methodCall, new EnqueuedState(queue));

    public void AddOrUpdateRecurringJob<T>(string recurringJobId, Expression<Func<T, Task>> methodCall, string cronExpression, TimeZoneInfo? timeZone = null, string queue = HangfireConstants.DefaultQueue) =>
        _recurringJobManagerFunc().AddOrUpdate(recurringJobId, queue, methodCall, cronExpression, new RecurringJobOptions
        {
            TimeZone = timeZone ?? TimeZoneInfo.Utc
        });

    public bool DeleteJob(string jobId) => _backgroundJobClientFunc().Delete(jobId);

    public void RemoveRecurringJobIfExists(string jobId) => _recurringJobManagerFunc().RemoveIfExists(jobId);

    public List<RecurringJobDto> GetRecurringJobs() => JobStorage.Current.GetConnection().GetRecurringJobs();

    public StateData? GetJobState(string jobId) => JobStorage.Current.GetConnection().GetStateData(jobId);
}
