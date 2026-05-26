using System.Linq.Expressions;
using Hangfire;
using CoreJobs = CodeSpace.Core.Services.Jobs;

namespace CodeSpace.Api.BackgroundJobs;

/// <summary>
/// Production implementation of <see cref="CoreJobs.IBackgroundJobClient"/> that delegates
/// to Hangfire's <c>BackgroundJob.Enqueue</c>. Lives in the API project because Hangfire
/// is an API-layer concern (storage configuration, dashboard, server) — Core owns only
/// the abstraction so it stays free of Hangfire dependencies.
///
/// <para>Tests substitute <c>InMemoryBackgroundJobClient</c> for the same interface; the
/// no-double-execution guarantee is proved by integration tests against the in-memory impl
/// because Hangfire's storage + retry behaviour is out of scope for our correctness claim
/// (we guarantee single-writer via the CAS, not via Hangfire's at-most-once semantics).</para>
/// </summary>
public sealed class HangfireBackgroundJobClient : CoreJobs.IBackgroundJobClient
{
    private readonly Hangfire.IBackgroundJobClient _hangfire;

    public HangfireBackgroundJobClient(Hangfire.IBackgroundJobClient hangfire) { _hangfire = hangfire; }

    public string Enqueue<T>(Expression<Func<T, Task>> methodCall) => _hangfire.Enqueue(methodCall);
}
