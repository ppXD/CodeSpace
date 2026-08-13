using CodeSpace.Messages.Dtos.Providers;
using CodeSpace.Messages.Failures;

namespace CodeSpace.Messages.Exceptions;

/// <summary>
/// Carries a <see cref="ProviderCallDiagnostic"/> out of a provider's webhook-registration call so
/// the registrar can persist WHAT HAPPENED, not just that something did.
///
/// <para>Why a wrapper rather than widening what is already thrown: the status survives translation
/// (<c>ProviderApiException</c> keeps it) but the provider's response body and the request we sent
/// do not — the body is buried in an SDK-specific exception shape and the request is known only at
/// the call site, which is the one place that can name the URL and the payload. Both providers build
/// this at that call site and throw it.</para>
///
/// <para>Deliberately transparent: the message is the inner exception's message verbatim, and
/// <see cref="Kind"/>/<see cref="Code"/> defer to the inner failure. The wrapper adds evidence, it
/// does not reinterpret the failure — so <c>last_error</c> reads exactly as it did before this type
/// existed, and any HTTP surface answers the same as it would have.</para>
/// </summary>
public sealed class ProviderWebhookRegistrationException : Exception, IFailure
{
    public FailureKind Kind => (InnerException as IFailure)?.Kind ?? FailureKind.Unavailable;

    public string Code => (InnerException as IFailure)?.Code ?? FailureCodes.ProviderError;

    public ProviderWebhookRegistrationException(ProviderCallDiagnostic diagnostic, Exception inner) : base(inner.Message, inner)
    {
        Diagnostic = diagnostic;
    }

    /// <summary>What we sent and what came back — already masked, ready to persist.</summary>
    public ProviderCallDiagnostic Diagnostic { get; }
}
