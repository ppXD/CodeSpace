using CodeSpace.Core.Services.Workflows.Llm;
using CodeSpace.Messages.Agents;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.Supervisor.Deciders;

/// <summary>
/// Scoped, bounded retry around the supervisor brain call — the stop-bleed for "a transient gateway blip on the
/// decision call terminalizes the whole durable run". Wraps any <see cref="ISupervisorDecider"/> and retries ONLY a
/// transient INFRA fault: an <see cref="LlmApiException"/> the transport already classified retryable
/// (<see cref="LlmErrorCategory.Transient"/> / <see cref="LlmErrorCategory.RateLimited"/>), plus its OWN per-attempt
/// timeout (a wedged-but-silent gateway). It NEVER retries a deterministic outcome: a model capability-miss is already
/// swallowed to a clean stop INSIDE the decider (so it returns as a decision, not an exception, and passes straight
/// through here), and an <see cref="LlmErrorCategory.AuthFailed"/> / a spawn-repo-model clamp propagates un-retried —
/// re-asking the brain after a deterministic error would only re-author the same bad outcome.
///
/// <para>Registered as the INNERMOST <c>ISupervisorDecider</c> decorator (inside the critic), so a transient is
/// recovered before the critic ever reviews a decision and the retry budget covers exactly the brain call — not the
/// critic's separate review call. After the bound is exhausted it re-throws the transient (a final timeout is surfaced
/// as a <see cref="LlmErrorCategory.Transient"/> <see cref="LlmApiException"/>) so the engine sees a single failed
/// brain call and terminalizes the run on the supervisor node's default single attempt — just after N bounded in-call
/// attempts instead of one (an operator-set node-level RetryPolicy, if any, would compound the engine's node retries
/// on top of this in-call retry). A run that is genuinely cancelled (the outer
/// token) is never retried. The happy path (a decision returned within the per-attempt budget) returns the inner
/// decision UNCHANGED — only a transient fault or a wedged call engages the timeout/retry; an operator can fully
/// disable the bound via the env overrides (MaxAttempts = 1 with the timeout set to the inherited 600s budget). A
/// plain class, wired via Autofac <c>RegisterDecorator</c>.</para>
/// </summary>
public sealed class RetryingSupervisorDeciderDecorator : ISupervisorDecider
{
    private readonly ISupervisorDecider _inner;
    private readonly SupervisorDecisionRetryOptions _options;
    private readonly ILogger<RetryingSupervisorDeciderDecorator> _logger;

    public RetryingSupervisorDeciderDecorator(ISupervisorDecider inner, SupervisorDecisionRetryOptions options, ILogger<RetryingSupervisorDeciderDecorator> logger)
    {
        _inner = inner;
        _options = options;
        _logger = logger;
    }

    public async Task<SupervisorDecision> DecideAsync(SupervisorTurnContext context, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            using var perAttempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            perAttempt.CancelAfter(_options.PerCallTimeout);

            try
            {
                return await _inner.DecideAsync(context, perAttempt.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // OUR per-attempt deadline fired (the run itself was NOT cancelled) — a wedged/silent gateway. On the
                // final attempt, surface it as a transient so the engine terminalizes it like any other infra fault.
                if (attempt >= _options.MaxAttempts)
                    throw new LlmApiException("supervisor-decision", statusCode: null, LlmErrorCategory.Transient, $"The supervisor decision call did not return within {_options.PerCallTimeout.TotalSeconds:0}s on any of {_options.MaxAttempts} attempt(s).");

                _logger.LogWarning("Supervisor brain call timed out after {TimeoutSeconds}s on turn {Turn}; retrying ({Attempt}/{Max})", _options.PerCallTimeout.TotalSeconds, context.TurnNumber, attempt, _options.MaxAttempts);

                await BackoffAsync(attempt, retryAfter: null, cancellationToken).ConfigureAwait(false);
            }
            catch (LlmApiException ex) when (ex.IsRetryable && attempt < _options.MaxAttempts)
            {
                _logger.LogWarning("Supervisor brain call hit a transient {Category} fault on turn {Turn}; retrying ({Attempt}/{Max})", ex.Category, context.TurnNumber, attempt, _options.MaxAttempts);

                await BackoffAsync(attempt, ex.RetryAfter, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task BackoffAsync(int attempt, TimeSpan? retryAfter, CancellationToken cancellationToken)
    {
        var delay = retryAfter ?? TimeSpan.FromTicks(_options.BaseBackoff.Ticks * attempt);

        if (delay > TimeSpan.Zero) await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
    }
}
