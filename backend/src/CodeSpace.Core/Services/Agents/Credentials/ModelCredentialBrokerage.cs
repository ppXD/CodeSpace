using CodeSpace.Core.Settings;
using CodeSpace.Messages.Failures;

namespace CodeSpace.Core.Services.Agents.Credentials;

/// <summary>
/// The POLICY half of the brokerage, kept apart from the transport: given "a key would have entered this sandbox"
/// and "the broker did / did not front it", what is this deployment allowed to do about it.
///
/// <para>It rides <c>Sandbox:RequireConfinement</c> rather than a switch of its own, and that is the decision, not a
/// shortcut. An operator who has said "never run an agent this host cannot confine" has already answered the
/// question this asks — they are not going to want the OS sandbox mandatory and the tenant's long-lived provider key
/// handed out unconditionally beside it. A second toggle would only let the two drift, and a deployment would then
/// have to be read twice to learn what an agent run is actually holding.</para>
/// </summary>
public static class ModelCredentialBrokerage
{
    /// <summary>Whether this deployment mandates brokerage — the SAME setting <c>BubblewrapSandbox.IsRequired</c> reads, live off the bound settings so it tracks configuration rather than a captured copy.</summary>
    public static bool IsRequired => RuntimeSettings.Current.RequireSandboxConfinement;

    /// <summary>
    /// Fail-closed guard: throws when a credential WOULD be injected into the sandbox
    /// (<paramref name="credentialWouldReachSandbox"/>) but was not brokered, on a deployment that
    /// <paramref name="required"/> it. Pure (explicit args) so every branch is unit-testable on a host with no broker
    /// and no bwrap — the host whose branch matters. The mirror of
    /// <c>BubblewrapSandbox.EnsureSatisfiable</c>, and it lands on the same executor path: a clean terminal failure
    /// naming the wall, never a launch that quietly hands the key over anyway.
    /// </summary>
    public static void EnsureSatisfiable(bool credentialWouldReachSandbox, bool brokered, bool required)
    {
        if (!required || brokered || !credentialWouldReachSandbox) return;

        throw new ModelCredentialBrokerUnavailableException(
            "Sandbox isolation is required (Sandbox:RequireConfinement) but this run's model credential could not be brokered " +
            "(no broker could listen on this worker, the harness cannot be re-pointed at one, or the credential names no upstream endpoint). " +
            "Refusing to inject the provider key directly.");
    }

    /// <summary>
    /// What the launch's confinement record says about the credential: whether it was brokered, or NULL when no
    /// credential reached the sandbox at all — a harness that authenticates some other way, or a run for which none
    /// resolved. The null is the load-bearing case: without it, every credential-less run would record "not
    /// brokered" and the posture sentence would disclose a direct injection that never happened.
    /// </summary>
    public static bool? BrokeredPosture(bool credentialWouldReachSandbox, bool brokered) =>
        credentialWouldReachSandbox ? brokered : null;
}

/// <summary>
/// A run's model credential could not be brokered on a deployment that requires it. Surfaced by the executor as a
/// clean run failure — the injection never happens.
///
/// <para>Unavailable, not Invalid, for the same reason <c>EgressSubnetReservationUnavailableException</c> is: nothing
/// about the LAUNCH can be changed to make it work, and the identical launch succeeds untouched once an operator
/// makes the worker able to broker.</para>
/// </summary>
public sealed class ModelCredentialBrokerUnavailableException : Exception, IFailure
{
    FailureKind IFailure.Kind => FailureKind.Unavailable;
    string IFailure.Code => FailureCodes.ModelCredentialBrokerUnavailable;

    public ModelCredentialBrokerUnavailableException(string message) : base(message) { }
}

/// <summary>
/// The worker holding a run's brokered lease went away AND no later worker could restore the address its agent holds,
/// so the detached agent's model access ended with that process: the child still holds a base URL naming a port
/// nothing answers on, and every model call it makes from here fails to connect.
///
/// <para>The second half of that sentence is the whole of what is left. A re-attaching worker DOES try to bind the
/// run's recorded port and re-install its lease (<see cref="IModelCredentialBroker.RebindAsync"/>); this failure is
/// what a refusal leaves behind — a handle stamped before the address was recorded, a run whose agent lives on another
/// host, a credential that no longer resolves to the row the launch fronted, or a port something else now holds.</para>
///
/// <para>Unavailable, like its sibling above, and for the same reason: nothing about the LAUNCH can be changed to make
/// it work, and the identical launch succeeds untouched on a live worker. That is also why the remedy it states is a
/// plain retry — this is the one brokerage failure that is nobody's mistake.</para>
/// </summary>
public sealed class ModelCredentialLeaseLostException : Exception, IFailure
{
    /// <summary>
    /// What an operator is told. It names the cause, says the attempt is over, and says what to do — deliberately in
    /// OUR vocabulary, because what it replaced was a run that silently degraded to its spec timeout while every model
    /// call failed to connect, which reads as a provider outage to whoever sees it first. Nothing here may say
    /// "provider" or "gateway": the provider is fine, and a reader who goes looking at one loses the hour this sentence
    /// exists to save. Pinned by a unit test — the words ARE the fix.
    /// </summary>
    public const string Explanation =
        "The worker holding this run's model credential lease restarted, so the agent's model access ended with it. " +
        "This attempt cannot continue — it can make no further model call — and was stopped rather than left running without one. " +
        "Retry to start a fresh attempt on a live worker; nothing about the run's own inputs needs to change.";

    FailureKind IFailure.Kind => FailureKind.Unavailable;
    string IFailure.Code => FailureCodes.ModelCredentialLeaseLost;

    public ModelCredentialLeaseLostException() : base(Explanation) { }
}
