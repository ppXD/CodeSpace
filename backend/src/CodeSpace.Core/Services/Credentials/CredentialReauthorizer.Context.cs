using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Messages.Credentials;
using CodeSpace.Messages.Dtos.Providers;
using CodeSpace.Messages.Enums;

namespace CodeSpace.Core.Services.Credentials;

public sealed partial class CredentialReauthorizer
{
    private static ReauthorizationContext BuildContext(CredentialReauthorization request) => new() { Request = request };

    /// <summary>The state one re-authorization builds up, step by step, and the next step reads. One per call.</summary>
    private sealed class ReauthorizationContext
    {
        public required CredentialReauthorization Request { get; init; }

        /// <summary>The row, read untracked: as it was before the write until <see cref="CredentialReauthorizer.MarkReauthorized"/> brings it up to date.</summary>
        public Credential Credential { get; set; } = default!;

        public ProviderInstance Instance => Credential.ProviderInstance;

        /// <summary>The auth type the row had before this re-authorization — what its replaced payload must be read as.</summary>
        public AuthType PreviousAuthType { get; set; }

        public string NewEncryptedPayload { get; set; } = string.Empty;

        /// <summary>The provider's answer for the new token; null when the provider has no probe or the probe could not be run.</summary>
        public CredentialProbeResult? Profile { get; set; }

        /// <summary>The provider account the credential was connected as, when something durable says so.</summary>
        public KnownAccount? Known { get; set; }

        /// <summary>What the row held when it was replaced — the payload the write was compared against.</summary>
        public string ReplacedEncryptedPayload { get; set; } = string.Empty;
    }

    private sealed record KnownAccount(string ProviderUserId, string ProviderUsername);
}
