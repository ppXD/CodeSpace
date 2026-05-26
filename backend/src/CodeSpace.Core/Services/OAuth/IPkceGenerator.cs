namespace CodeSpace.Core.Services.OAuth;

/// <summary>
/// Produces RFC 7636 PKCE pairs. Verifier is 32 bytes of CSPRNG output (43-char base64url);
/// challenge is the SHA-256 of the verifier, also base64url. Only the challenge is sent to
/// the provider; the verifier stays in <see cref="OAuthPendingState"/> until token exchange.
/// </summary>
public interface IPkceGenerator
{
    PkcePair Generate();
}

public sealed record PkcePair(string Verifier, string ChallengeS256);
