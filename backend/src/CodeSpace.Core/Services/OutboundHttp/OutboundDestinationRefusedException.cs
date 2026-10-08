using CodeSpace.Core.Settings.OutboundHttp;
using CodeSpace.Messages.Failures;

namespace CodeSpace.Core.Services.OutboundHttp;

/// <summary>
/// A guarded connection refused because its host resolved to no address the guard may dial. Thrown from the handler's
/// connect callback, so the caller sees it as the inner exception of the <see cref="HttpRequestException"/> the request
/// fails with. The message names the host and the setting that admits it, never the addresses it resolved to: it is
/// shown to the run viewer, and echoing an internal address back would make the guard a resolver for the worker's DNS.
/// </summary>
public sealed class OutboundDestinationRefusedException : Exception, IFailure
{
    public OutboundDestinationRefusedException(string host)
        : base($"Refused to connect to '{host}': it does not resolve to a public address. Workflow HTTP calls may not reach the worker's loopback, link-local, private or metadata addresses; an operator admits an intentional internal destination by committing its host or CIDR to {OutboundHttpAllowlistSetting.ConfigurationKey}.")
    {
    }

    /// <summary>Well-formed and understood, but blocked by the destination rule: the URL or the operator's allowlist must change, and a retry cannot help.</summary>
    public FailureKind Kind => FailureKind.Unprocessable;

    public string Code => FailureCodes.OutboundDestinationRefused;
}
