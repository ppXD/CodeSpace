using System.Linq.Expressions;
using System.Reflection;
using CodeSpace.Core.Services.Jobs;

namespace CodeSpace.IntegrationTests.Infrastructure.Jobs;

/// <summary>
/// Test double for <see cref="IBackgroundJobClient"/>. Records every Enqueue call so
/// integration tests can assert "the dispatcher attempted exactly one enqueue for run X"
/// without spinning up a real Hangfire server.
///
/// <para>The recorded <see cref="EnqueuedCall"/> entries carry the method + first argument
/// so most tests can simply check <c>client.Calls.Count(c => c.RunId == expectedRunId)</c>.
/// The actual method-call DOES NOT execute here — that's the whole point of a job-client
/// test double. Tests that want to drive the engine call it directly via
/// <c>engine.ExecuteRunAsync(runId)</c>, simulating Hangfire's worker.</para>
///
/// <para>Optionally, tests can set <see cref="ThrowOnEnqueue"/> to a value to force the
/// client to throw on the next call — this exercises the dispatcher's revert-on-throw
/// path (Pending → Enqueued → revert → Pending).</para>
/// </summary>
public sealed class InMemoryBackgroundJobClient : IBackgroundJobClient
{
    private readonly object _lock = new();
    private readonly List<EnqueuedCall> _calls = new();

    /// <summary>When non-null, the next Enqueue call throws this exception (then the field clears).</summary>
    public Exception? ThrowOnEnqueue { get; set; }

    /// <summary>Every Enqueue call observed during this fixture's lifetime. Tests can clear if needed.</summary>
    public IReadOnlyList<EnqueuedCall> Calls
    {
        get
        {
            lock (_lock) return _calls.ToList();
        }
    }

    public void Clear()
    {
        lock (_lock) _calls.Clear();
    }

    public string Enqueue<T>(Expression<Func<T, Task>> methodCall)
    {
        if (ThrowOnEnqueue is { } ex)
        {
            ThrowOnEnqueue = null;
            throw ex;
        }

        var (methodName, runId) = Decode(methodCall);
        var jobId = Guid.NewGuid().ToString("N");

        lock (_lock)
        {
            _calls.Add(new EnqueuedCall
            {
                JobId = jobId,
                ServiceType = typeof(T),
                MethodName = methodName,
                RunId = runId,
                EnqueuedAt = DateTimeOffset.UtcNow,
            });
        }

        return jobId;
    }

    /// <summary>
    /// Pull (methodName, firstGuidArg) out of the expression. Designed for the
    /// <c>IWorkflowEngine.ExecuteRunAsync(Guid runId, CancellationToken)</c> shape — tests
    /// can extract the runId from <see cref="EnqueuedCall.RunId"/>. For expressions that
    /// don't have a Guid first arg, <see cref="EnqueuedCall.RunId"/> stays null.
    /// </summary>
    private static (string MethodName, Guid? RunId) Decode<T>(Expression<Func<T, Task>> expression)
    {
        if (expression.Body is not MethodCallExpression call)
            return ("<non-method-call>", null);

        Guid? runId = null;
        if (call.Arguments.Count > 0)
        {
            try
            {
                var firstArg = Expression.Lambda(call.Arguments[0]).Compile().DynamicInvoke();
                if (firstArg is Guid guid) runId = guid;
            }
            catch
            {
                // Best-effort: if the arg isn't trivially evaluable, leave runId null.
            }
        }

        return (call.Method.Name, runId);
    }
}

public sealed record EnqueuedCall
{
    public required string JobId { get; init; }
    public required Type ServiceType { get; init; }
    public required string MethodName { get; init; }
    public required Guid? RunId { get; init; }
    public required DateTimeOffset EnqueuedAt { get; init; }
}
