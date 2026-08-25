using CodeSpace.Core.Settings;
using CodeSpace.Messages.Enums;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;

namespace CodeSpace.E2ETests.Infrastructure;

/// <summary>
/// FIXTURE-ONLY tier (Rule 12): this asserts a property of the test fixtures themselves, not of production code, so
/// it counts toward no production coverage. It exists because the property it pins is invisible at every other tier
/// and expensive to lose.
///
/// <para><see cref="HangfireHostingSetting"/> resolves an absent, blank or unrecognised role to
/// <see cref="HangfireHosting.Worker"/> — deliberately, so a mistyped deployment still processes jobs. A fixture
/// that names no role therefore inherits the PROCESSING role and starts two Hangfire servers whose workers each
/// hold a Postgres connection, <c>ControlWorkerCount + Environment.ProcessorCount * 2</c> of them. That made one
/// fixture's connection footprint a function of the host's core count: 38 held idle on a 12-core box, so the third
/// concurrent fixture failed with <c>53300: sorry, too many clients already</c> against the
/// <c>postgres:18-alpine</c> default <c>max_connections=100</c> — the same image and default CI runs.</para>
///
/// <para>Asserting the CONNECTION COUNT instead would have been a corpus hole: on a 2-core runner
/// <c>ProcessorCount * 2</c> is small enough that the unfixed fixture passes anyway. The server COUNT is the same
/// on every machine, so this goes red wherever the role is lost — including if the default itself is ever changed.</para>
/// </summary>
[Trait("Category", "E2E")]
[Trait("Surface", "Http")]
public sealed class FactoryHangfireRoleTests
{
    [Fact]
    public async Task The_task_launch_fixture_starts_no_job_server()
    {
        await using var factory = new TaskLaunchApiFactory();
        await ((IAsyncLifetime)factory).InitializeAsync();

        HangfireHostedServiceNames(factory.Services).ShouldBeEmpty(
            customMessage: "The fixture booted a Hangfire job server. Its workers each hold a Postgres connection, "
                + "so concurrent fixtures exhaust max_connections and fail with 53300. The fixture drains a "
                + $"DeferredJobClient by hand, so it needs no server: it must name {nameof(HangfireHosting.Api)} "
                + $"under the '{HangfireHostingSetting.ConfigurationKey}' key, because the DEFAULT role is Worker.");
    }

    [Fact]
    public async Task The_webhook_fixture_starts_no_job_server()
    {
        await using var factory = new WebhookApiFactory();
        await ((IAsyncLifetime)factory).InitializeAsync();

        HangfireHostedServiceNames(factory.Services).ShouldBeEmpty(
            customMessage: "The fixture booted a Hangfire job server, whose workers each hold a Postgres connection. "
                + "This fixture registers a no-op job client, so no worker could run anything at all.");
    }

    /// <summary>
    /// Every hosted service Hangfire owns, by type name. Hangfire's server hosted service is internal, so it is
    /// matched by assembly rather than by type: naming the internal type would pin a detail of Hangfire's build,
    /// while "any hosted service Hangfire contributes" is the property actually worth holding.
    /// </summary>
    private static IReadOnlyList<string> HangfireHostedServiceNames(IServiceProvider services) =>
        services.GetServices<IHostedService>()
            .Select(service => service.GetType())
            .Where(type => type.Assembly.GetName().Name?.StartsWith("Hangfire", StringComparison.Ordinal) == true)
            .Select(type => type.FullName ?? type.Name)
            .ToList();
}
