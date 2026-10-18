using CodeSpace.Core.Services.ProviderInstances;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Failures;

namespace CodeSpace.Core.Services.Credentials.Remedy;

/// <summary>
/// Whether what a failure names works NOW — the half of a remedy that turns a repair page into a "run it again" page. Pure over
/// what is recorded about the credential and when the failure was recorded, so the rule is one definition and a unit test can walk
/// every shape of "fixed", "looks fine but was never fixed" and "gone for good".
/// </summary>
public static class RemedyResolution
{
    /// <summary>What is recorded about a credential that decides whether it works; read from the row, never from the provider.</summary>
    public sealed record CredentialState(Guid Id, CredentialStatus Status, AuthType AuthType, DateTimeOffset? ExpiresDate, DateTimeOffset? LastValidatedDate, bool Retired);

    /// <summary>The credential can authenticate as far as CodeSpace can tell: it is in service, not marked refused or disconnected, and a static token's recorded expiry has not passed.</summary>
    public static bool IsLive(CredentialState credential, DateTimeOffset now) =>
        !credential.Retired
        && credential.Status == CredentialStatus.Active
        && CredentialHealthDerivation.Derive(credential.Status, credential.AuthType, credential.ExpiresDate, ownerLeft: false, now) is CredentialHealth.Active or CredentialHealth.Expiring;

    /// <summary>
    /// The credential the failure was about is fixed. For a failure that met a credential the row already said was dead
    /// (disconnected, refused) a live row IS the fix. For one that met a refusal the row did not know about (a 401, a refused push)
    /// the row never said otherwise, so only something that validated it since the failure counts — a reconnect, a replaced token,
    /// a passing probe.
    /// </summary>
    public static bool FailingCredentialIsFixed(CredentialState credential, string code, DateTimeOffset failedAt, DateTimeOffset now)
    {
        if (!IsLive(credential, now)) return false;

        return FixIsObservable(code) || credential.LastValidatedDate >= failedAt;
    }

    /// <summary>Whether CodeSpace can SEE a fix for this code. A dead credential coming back, or an address no longer redirecting, shows in what is recorded; a provider's 401 on an Active row does not, so those remedies leave "run it again" available rather than demand proof nobody can give.</summary>
    public static bool FixIsObservable(string code) => code is FailureCodes.CredentialDisconnected or FailureCodes.CredentialNeedsReconnect or FailureCodes.ProviderBaseUrlRedirects;

    /// <summary>
    /// A repository authenticates through something that works. The credential it runs through is the failing one only when it
    /// IS the one the failure named (or none was named); a different one is a replacement, and a live replacement works.
    /// </summary>
    public static bool RepositoryWorks(CredentialState? current, Guid? failingCredentialId, string code, DateTimeOffset failedAt, DateTimeOffset now)
    {
        if (current is null) return false;

        return failingCredentialId is { } failing && current.Id != failing ? IsLive(current, now) : FailingCredentialIsFixed(current, code, failedAt, now);
    }

    /// <summary>A redirect is resolved once the provider's base URL no longer starts at the origin that redirected: it was edited to somewhere else.</summary>
    public static bool RedirectIsResolved(string? requestedOrigin, string currentBaseUrl) =>
        !string.IsNullOrEmpty(requestedOrigin) && Uri.TryCreate(currentBaseUrl, UriKind.Absolute, out var current) && !string.Equals(Origin(current), requestedOrigin, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The repair that will be ACCEPTED for a provider whose address redirects — the edit the card would offer is "set the base URL to
    /// <paramref name="redirectsTo"/>", and the server refuses to move a provider's host while credentials are connected through it
    /// (<see cref="ProviderInstanceService.EndpointMoves"/>, the same rule the update runs, so the two cannot disagree), allowing only
    /// an http to https upgrade of the same host. A redirect that stays inside one origin has no address to edit to at all.
    /// </summary>
    public static RedirectRepair RepairRedirect(string? requestedOrigin, string? redirectsTo, string baseUrl, string? apiUrl, int connectedCredentials)
    {
        if (string.IsNullOrEmpty(redirectsTo) || string.Equals(requestedOrigin, redirectsTo, StringComparison.OrdinalIgnoreCase)) return RedirectRepair.HostSide;

        return connectedCredentials > 0 && ProviderInstanceService.EndpointMoves(baseUrl, apiUrl, redirectsTo, apiUrl) ? RedirectRepair.DisconnectFirst : RedirectRepair.EditAddress;
    }

    private static string Origin(Uri uri) => $"{uri.Scheme}://{uri.IdnHost}{(uri.IsDefaultPort ? "" : ":" + uri.Port)}";
}
