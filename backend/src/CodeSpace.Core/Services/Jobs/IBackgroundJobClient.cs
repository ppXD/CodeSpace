using System.Linq.Expressions;

namespace CodeSpace.Core.Services.Jobs;

/// <summary>
/// Thin abstraction over Hangfire's <c>BackgroundJob.Enqueue</c>. Two reasons for the seam:
///
/// <list type="number">
///   <item><b>Testability</b> — integration tests register an in-memory recorder and assert
///         "the dispatcher attempted exactly one enqueue for this run id", without spinning
///         up a real Hangfire server. The CAS contract for "no double execution" is what
///         the tests verify; Hangfire's own retry semantics are out of scope for that.</item>
///   <item><b>Storage swap-out</b> — if we ever migrate from Hangfire to Quartz / a custom
///         runner / a managed service, only the implementation behind this interface changes.
///         Callers (<c>IWorkflowRunDispatcher</c>, future schedulers) don't notice.</item>
/// </list>
///
/// <para>The single method mirrors Hangfire's `Enqueue&lt;T&gt;(Expression&lt;Func&lt;T, Task&gt;&gt;)`
/// shape on purpose so the binding to Hangfire is one-line + tests can intercept the
/// expression to extract the called method + args.</para>
/// </summary>
public interface IBackgroundJobClient
{
    /// <summary>
    /// Enqueue a background job. Returns a provider-specific job id (used for diagnostics +
    /// future cancel/probe APIs). Throws if the underlying provider's enqueue fails — the
    /// caller (typically <c>IWorkflowRunDispatcher</c>) MUST handle the throw to revert any
    /// pre-enqueue state changes.
    /// </summary>
    /// <typeparam name="T">Service type the worker will resolve from DI to run the method.</typeparam>
    /// <param name="methodCall">Expression encoding the method + args. Stored serialised.</param>
    string Enqueue<T>(Expression<Func<T, Task>> methodCall);
}
