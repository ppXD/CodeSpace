using CodeSpace.Messages.Failures;

namespace CodeSpace.Core.Services.Webhooks.Exceptions;

/// <summary>
/// A correctly signed delivery from a provider that stamps every delivery with an id arrived without one. The request
/// is malformed for that provider — a real one never sends it — so it is answered 400, not as a signature failure.
/// </summary>
public sealed class WebhookDeliveryUnidentifiedException : Exception, IFailure
{
    public WebhookDeliveryUnidentifiedException(Guid webhookId, string header) : base($"Webhook {webhookId} delivery carries no {header}") { }

    /// <summary>Malformed for its provider: the same request sent again is refused again.</summary>
    public FailureKind Kind => FailureKind.Invalid;

    public string Code => FailureCodes.InvalidRequest;
}
