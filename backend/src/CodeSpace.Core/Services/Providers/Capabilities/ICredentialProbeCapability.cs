using CodeSpace.Messages.Dtos.Providers;

namespace CodeSpace.Core.Services.Providers.Capabilities;

/// <summary>
/// "Is this credential currently valid against this provider instance?" check. Used at bind
/// time to fail fast before any DB write, and by the test-binding endpoint.
/// </summary>
public interface ICredentialProbeCapability : IProviderCapability
{
    Task<CredentialProbeResult> ProbeCredentialAsync(ProviderContext context, CancellationToken cancellationToken);
}
