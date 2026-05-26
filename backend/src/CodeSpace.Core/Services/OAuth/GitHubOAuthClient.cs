using System.Net.Http.Headers;
using System.Text.Json;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Messages.Enums;

namespace CodeSpace.Core.Services.OAuth;

/// <summary>
/// GitHub OAuth App flow. Works against github.com (default) and any GitHub Enterprise host —
/// the host is read from <see cref="ProviderInstance.BaseUrl"/> so a per-team self-hosted GHE
/// integration drops in with zero code change.
///
/// Notes:
///   • PKCE support landed in GitHub OAuth in 2023; we always send the S256 challenge.
///   • OAuth Apps only issue refresh tokens when the app is configured with "Expire user
///     authorization tokens" enabled. If the response has no refresh_token, we just store the
///     access token; refresh attempts later will fail with a clear error rather than silently
///     producing wrong tokens.
///   • Token endpoint historically returned form-urlencoded; we ask for JSON via Accept.
/// </summary>
public sealed class GitHubOAuthClient : IOAuthClient, ISingletonDependency
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly TimeProvider _clock;

    public GitHubOAuthClient(IHttpClientFactory httpClientFactory, TimeProvider clock)
    {
        _httpClientFactory = httpClientFactory;
        _clock = clock;
    }

    public ProviderKind Kind => ProviderKind.GitHub;

    public Uri BuildAuthorizeUrl(OAuthAuthorizeInput input)
    {
        var authorizeBase = ResolveAuthorizeBase(input.Instance);

        var query = new Dictionary<string, string?>
        {
            ["client_id"] = input.ClientId,
            ["redirect_uri"] = input.RedirectUri.ToString(),
            ["state"] = input.State,
            ["code_challenge"] = input.CodeChallengeS256,
            ["code_challenge_method"] = "S256",
            // Force the consent screen on every authorize. Without this, GitHub silently
            // re-issues a token when the user is already signed in and the OAuth app is
            // still in their authorized list — which means the operator can NOT change
            // which organizations get authorized on reconnect.
            //
            // `prompt=consent` is officially supported for GitHub Apps and undocumented
            // (but observed to work) for OAuth Apps. If a future GitHub change makes OAuth
            // Apps ignore it entirely, the "Manage org access on GitHub" link in the UI
            // gives the operator a manual escape hatch — they revoke the app on GitHub,
            // and the next authorize forces fresh consent regardless of this param.
            ["prompt"] = "consent"
        };

        if (input.Scopes is { Count: > 0 }) query["scope"] = string.Join(" ", input.Scopes);

        return new Uri(authorizeBase + ToQueryString(query));
    }

    public async Task<OAuthTokenResponse> ExchangeCodeAsync(OAuthCodeExchangeInput input, CancellationToken cancellationToken)
    {
        var form = new Dictionary<string, string>
        {
            ["client_id"] = input.ClientId,
            ["client_secret"] = input.ClientSecret,
            ["code"] = input.Code,
            ["redirect_uri"] = input.RedirectUri.ToString(),
            ["code_verifier"] = input.CodeVerifier
        };

        return await PostTokenAsync(input.Instance, form, cancellationToken).ConfigureAwait(false);
    }

    public async Task<OAuthTokenResponse> RefreshAsync(OAuthRefreshInput input, CancellationToken cancellationToken)
    {
        var form = new Dictionary<string, string>
        {
            ["client_id"] = input.ClientId,
            ["client_secret"] = input.ClientSecret,
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = input.RefreshToken
        };

        return await PostTokenAsync(input.Instance, form, cancellationToken).ConfigureAwait(false);
    }

    public async Task RevokeAsync(OAuthRevokeInput input, CancellationToken cancellationToken)
    {
        // DELETE /applications/{client_id}/grant revokes the user's grant entirely — kills
        // every access + refresh token GitHub ever issued under our client_id for that user.
        // Surgical alternative is /token (revoke single token) but for a CodeSpace credential
        // we always want the whole grant gone. Idempotent: subsequent calls return 404.
        var apiBase = ResolveApiBase(input.Instance);
        var url = new Uri($"{apiBase}/applications/{Uri.EscapeDataString(input.ClientId)}/grant");

        using var http = _httpClientFactory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Delete, url)
        {
            Content = new StringContent($"{{\"access_token\":\"{input.Token}\"}}", System.Text.Encoding.UTF8, "application/json")
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", BasicAuthValue(input.ClientId, input.ClientSecret));

        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);

        // 204 = revoked, 404 = already gone, 422 = malformed token (also treat as already gone).
        // Anything else propagates so the caller can log + retry next refresh.
        if (response.StatusCode is System.Net.HttpStatusCode.NoContent or System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.UnprocessableContent) return;

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        throw new OAuthExchangeException($"http_{(int)response.StatusCode}", $"GitHub revoke failed: {response.ReasonPhrase}", body);
    }

    private static string BasicAuthValue(string clientId, string clientSecret) => Convert.ToBase64String(System.Text.Encoding.ASCII.GetBytes($"{clientId}:{clientSecret}"));

    private static string ResolveApiBase(ProviderInstance instance)
    {
        if (!string.IsNullOrWhiteSpace(instance.ApiUrl)) return instance.ApiUrl.TrimEnd('/');
        if (string.Equals(instance.BaseUrl?.TrimEnd('/'), "https://github.com", StringComparison.OrdinalIgnoreCase)) return "https://api.github.com";
        return instance.BaseUrl.TrimEnd('/') + "/api/v3";
    }

    private async Task<OAuthTokenResponse> PostTokenAsync(ProviderInstance instance, IDictionary<string, string> form, CancellationToken cancellationToken)
    {
        var tokenUrl = ResolveTokenUrl(instance);

        using var http = _httpClientFactory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, tokenUrl) { Content = new FormUrlEncodedContent(form) };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode) throw new OAuthExchangeException($"http_{(int)response.StatusCode}", response.ReasonPhrase, body);

        return ParseTokenResponse(body);
    }

    private OAuthTokenResponse ParseTokenResponse(string body)
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        // GitHub returns 200 with an error payload when the code is invalid — must inspect body.
        if (root.TryGetProperty("error", out var err)) throw new OAuthExchangeException(err.GetString() ?? "unknown_error", root.TryGetProperty("error_description", out var d) ? d.GetString() : null, body);

        var accessToken = root.GetProperty("access_token").GetString() ?? throw new OAuthExchangeException("malformed_response", "missing access_token", body);
        var refreshToken = root.TryGetProperty("refresh_token", out var rt) ? rt.GetString() : null;
        var tokenType = root.TryGetProperty("token_type", out var tt) ? tt.GetString() : null;
        var grantedScope = root.TryGetProperty("scope", out var sc) ? sc.GetString() : null;

        DateTimeOffset? expiresAt = null;

        if (root.TryGetProperty("expires_in", out var exp) && exp.TryGetInt64(out var seconds)) expiresAt = _clock.GetUtcNow() + TimeSpan.FromSeconds(seconds);

        return new OAuthTokenResponse
        {
            AccessToken = accessToken,
            RefreshToken = refreshToken,
            ExpiresAt = expiresAt,
            TokenType = tokenType,
            GrantedScopes = string.IsNullOrWhiteSpace(grantedScope) ? null : grantedScope.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        };
    }

    private static string ResolveAuthorizeBase(ProviderInstance instance) => instance.BaseUrl.TrimEnd('/') + "/login/oauth/authorize";

    private static Uri ResolveTokenUrl(ProviderInstance instance) => new(instance.BaseUrl.TrimEnd('/') + "/login/oauth/access_token");

    private static string ToQueryString(IDictionary<string, string?> query)
    {
        var pairs = query.Where(kv => kv.Value != null).Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value!)}");
        return "?" + string.Join("&", pairs);
    }
}
