using System.Collections.Concurrent;
using Autofac;
using CodeSpace.Core.Services.OAuth;
using CodeSpace.Core.Services.Providers;
using CodeSpace.Core.Services.Providers.Capabilities;
using CodeSpace.Messages.Dtos.Providers;
using CodeSpace.Messages.Enums;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;

namespace CodeSpace.E2ETests.Infrastructure;

/// <summary>
/// The real API host with a stub wire behind the generic <see cref="ProviderKind.Git"/> kind: an OAuth client and a
/// credential probe that answer from memory. Production registers no capability for that kind, so nothing real is
/// displaced — it only gives an HTTP test a provider it can connect and probe without a network.
/// </summary>
public class GitWireApiFactory : TaskLaunchApiFactory
{
    public StubGitWire Wire { get; } = new();

    protected override void ConfigureContractTestServices(ContainerBuilder builder)
    {
        builder.RegisterInstance(Wire).As<IProviderCapability>().SingleInstance();
        builder.RegisterInstance(Wire).As<IOAuthClient>().SingleInstance();
    }
}

public sealed class StubGitWire : ICredentialProbeCapability, IOAuthClient
{
    public ProviderKind Kind => ProviderKind.Git;

    /// <summary>Every authorization code the token endpoint was asked to exchange, in order.</summary>
    public ConcurrentQueue<string> ExchangedCodes { get; } = new();

    /// <summary>Every credential the probe was spent on, in order.</summary>
    public ConcurrentQueue<Guid> ProbedCredentials { get; } = new();

    public Task<CredentialProbeResult> ProbeCredentialAsync(ProviderContext context, CancellationToken cancellationToken)
    {
        ProbedCredentials.Enqueue(context.Credential.Id);

        return Task.FromResult(new CredentialProbeResult { IsValid = true, AuthenticatedUserExternalId = "stub-user-42", AuthenticatedUserName = "stub-user" });
    }

    public Uri BuildAuthorizeUrl(OAuthAuthorizeInput input) => new($"https://git.stub.test/oauth/authorize?client_id={input.ClientId}&state={input.State}");

    public Task<OAuthTokenResponse> ExchangeCodeAsync(OAuthCodeExchangeInput input, CancellationToken cancellationToken)
    {
        ExchangedCodes.Enqueue(input.Code);

        return Task.FromResult(new OAuthTokenResponse { AccessToken = $"token-for-{input.Code}", GrantedScopes = new[] { "repo" } });
    }

    public Task<OAuthTokenResponse> RefreshAsync(OAuthRefreshInput input, CancellationToken cancellationToken) => throw new NotSupportedException("the stub wire issues no refresh tokens");

    public Task RevokeAsync(OAuthRevokeInput input, CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>
/// The same host behind a TLS-terminating proxy, configured the way ASP.NET documents for one:
/// <c>ASPNETCORE_FORWARDEDHEADERS_ENABLED=true</c>, which makes the host honour <c>X-Forwarded-Proto</c> (and
/// <c>X-Forwarded-For</c>) from any sender. Set as configuration on this host only, never as a process-wide variable.
/// </summary>
public sealed class ForwardedHeadersGitWireApiFactory : GitWireApiFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        builder.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?> { ["ForwardedHeaders_Enabled"] = "true" }));
    }
}
