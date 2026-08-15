using CodeSpace.Api.Extensions;
using CodeSpace.Api.Http;
using CodeSpace.Core.Services.Identity;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Shouldly;

namespace CodeSpace.IntegrationTests.Authorization;

/// <summary>Fast configuration tests live here because UnitTests intentionally has no CodeSpace.Api reference.</summary>
[Trait("Category", "Integration")]
public class CorsPolicyExtensionTests
{
    [Fact]
    public void Spa_policy_exposes_every_range_header_the_browser_must_validate()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Cors:AllowedOrigins:0"] = "https://app.example.com",
        }).Build();
        services.AddCorsPolicy(configuration, new FakeHostEnvironment());

        using var provider = services.BuildServiceProvider();
        var policy = provider.GetRequiredService<IOptions<CorsOptions>>().Value.GetPolicy(CorsPolicyExtension.PolicyName)!;

        policy.ExposedHeaders.ShouldContain(HeaderCurrentTeam.HeaderName);
        foreach (var header in AgentRunLogHttpHeaders.RangeResponseHeaders) policy.ExposedHeaders.ShouldContain(header);
    }

    private sealed class FakeHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "test";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }
}
