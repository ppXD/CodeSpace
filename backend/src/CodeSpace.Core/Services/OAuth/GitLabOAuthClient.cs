using System.Net.Http.Headers;
using System.Text.Json;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Messages.Enums;

namespace CodeSpace.Core.Services.OAuth;

/// <summary>
/// GitLab OAuth flow. Works against gitlab.com (default) and any self-managed GitLab host —
/// the host is read from <see cref="ProviderInstance.BaseUrl"/>. Supports both user-owned
/// and group-owned OAuth applications since they share the same authorize/token endpoints.
///
/// Notes:
///   • PKCE (S256) is required; GitLab returns invalid_grant if code_verifier is missing.
///   • Always returns refresh_token + expires_in. Refresh rotates the refresh_token, so the
///     new one MUST be persisted or the next refresh will fail with invalid_grant.
///   • Token endpoint returns JSON.
/// </summary>
public sealed class GitLabOAuthClient : IOAuthClient, ISingletonDependency
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly TimeProvider _clock;

    public GitLabOAuthClient(IHttpClientFactory httpClientFactory, TimeProvider clock)
    {
        _httpClientFactory = httpClientFactory;
        _clock = clock;
    }

    public ProviderKind Kind => ProviderKind.GitLab;

    public Uri BuildAuthorizeUrl(OAuthAuthorizeInput input)
    {
        var authorizeBase = ResolveAuthorizeBase(input.Instance);

        var query = new Dictionary<string, string?>
        {
            ["client_id"] = input.ClientId,
            ["redirect_uri"] = input.RedirectUri.ToString(),
            ["response_type"] = "code",
            ["state"] = input.State,
            ["code_challenge"] = input.CodeChallengeS256,
            ["code_challenge_method"] = "S256"
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
            ["grant_type"] = "authorization_code",
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
        // RFC 7009 token revocation. Returns 200 on success AND on already-revoked, so any
        // 2xx response is acceptable. Only revokes the specific token passed — caller is
        // responsible for calling once per access_token and once per refresh_token if it
        // wants both gone.
        var revokeUrl = new Uri(input.Instance.BaseUrl.TrimEnd('/') + "/oauth/revoke");

        var form = new Dictionary<string, string>
        {
            ["client_id"] = input.ClientId,
            ["client_secret"] = input.ClientSecret,
            ["token"] = input.Token
        };

        if (!string.IsNullOrEmpty(input.TokenTypeHint)) form["token_type_hint"] = input.TokenTypeHint;

        using var http = _httpClientFactory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, revokeUrl) { Content = new FormUrlEncodedContent(form) };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (response.IsSuccessStatusCode) return;

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        throw ParseError(body, response.StatusCode, response.ReasonPhrase);
    }

    private async Task<OAuthTokenResponse> PostTokenAsync(ProviderInstance instance, IDictionary<string, string> form, CancellationToken cancellationToken)
    {
        var tokenUrl = ResolveTokenUrl(instance);

        using var http = _httpClientFactory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, tokenUrl) { Content = new FormUrlEncodedContent(form) };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode) throw ParseError(body, response.StatusCode, response.ReasonPhrase);

        return ParseTokenResponse(body);
    }

    private OAuthTokenResponse ParseTokenResponse(string body)
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

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
            GrantedScopes = string.IsNullOrWhiteSpace(grantedScope) ? null : grantedScope.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        };
    }

    private static OAuthExchangeException ParseError(string body, System.Net.HttpStatusCode statusCode, string? reason)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            if (root.TryGetProperty("error", out var err)) return new OAuthExchangeException(err.GetString() ?? "unknown_error", root.TryGetProperty("error_description", out var d) ? d.GetString() : null, body);
        }
        catch (JsonException) { /* fall through to generic http error */ }

        return new OAuthExchangeException($"http_{(int)statusCode}", reason, body);
    }

    private static string ResolveAuthorizeBase(ProviderInstance instance) => instance.BaseUrl.TrimEnd('/') + "/oauth/authorize";

    private static Uri ResolveTokenUrl(ProviderInstance instance) => new(instance.BaseUrl.TrimEnd('/') + "/oauth/token");

    private static string ToQueryString(IDictionary<string, string?> query)
    {
        var pairs = query.Where(kv => kv.Value != null).Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value!)}");
        return "?" + string.Join("&", pairs);
    }
}
