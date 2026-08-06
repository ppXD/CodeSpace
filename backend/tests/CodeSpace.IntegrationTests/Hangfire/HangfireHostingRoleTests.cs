using CodeSpace.Api.Extensions;
using CodeSpace.Api.Extensions.Hangfire;
using CodeSpace.Core.Settings;
using CodeSpace.Messages.Enums;
using Microsoft.Extensions.Configuration;
using Shouldly;

namespace CodeSpace.IntegrationTests.Hangfire;

/// <summary>
/// The deployment-topology contract: which ROLE a pod plays, and what that role does. Both roles run the same
/// assembly, so the only thing separating a public pod from a processing one is this selection — get it wrong and
/// a pod either drains a queue it should not touch, or looks healthy while draining nothing.
///
/// <para>Replaces the previous <c>CODESPACE_HANGFIRE_PROCESSING_ENABLED</c> boolean gate. A toggle-shaped env var
/// was the wrong shape twice over: it is banned by this project's standing configuration rule (committed values,
/// changed by PR — not switches), and a boolean cannot express a role, so every future role would have needed
/// another independent flag whose combinations nothing validates.</para>
/// </summary>
public class HangfireHostingRoleTests
{
    [Fact]
    public void The_configuration_key_is_pinned() =>
        // Renaming it silently reverts every deployment to the default role — a public pod would start draining
        // the queue (and executing agents) with nothing in its own config having changed.
        HangfireHostingSetting.ConfigurationKey.ShouldBe("HangfireHosting");

    [Theory]
    [InlineData("Api", HangfireHosting.Api)]
    [InlineData("api", HangfireHosting.Api)]
    [InlineData("  Worker  ", HangfireHosting.Worker)]
    [InlineData("WORKER", HangfireHosting.Worker)]
    [InlineData(null, HangfireHosting.Worker)]
    [InlineData("", HangfireHosting.Worker)]
    [InlineData("nonsense", HangfireHosting.Worker)]
    public void An_absent_or_unrecognised_role_falls_back_to_processing(string? raw, HangfireHosting expected) =>
        // Worker, never Api: an unconfigured deployment that quietly stops draining the queue is a far worse
        // failure than one that processes where nobody asked it to.
        HangfireHostingSetting.Resolve(raw).ShouldBe(expected);

    [Theory]
    [InlineData(HangfireHosting.Api, typeof(ApiHangfireRegistrar))]
    [InlineData(HangfireHosting.Worker, typeof(WorkerHangfireRegistrar))]
    public void Each_role_maps_to_its_own_registrar(HangfireHosting hosting, Type expected) =>
        HangfireExtension.ForRole(hosting).ShouldBeOfType(expected);

    [Fact]
    public void Every_declared_role_is_mapped()
    {
        // A new enum member with no registrar would surface as a pod that processes nothing while looking healthy.
        foreach (var hosting in Enum.GetValues<HangfireHosting>())
            Should.NotThrow(() => HangfireExtension.ForRole(hosting), $"role {hosting} has no registrar mapped");
    }

    [Fact]
    public void The_role_is_read_from_configuration_end_to_end()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [HangfireHostingSetting.ConfigurationKey] = "Api" })
            .Build();

        HangfireExtension.FindRegistrar(config).ShouldBeOfType<ApiHangfireRegistrar>(
            "the key must travel through the real configuration pipeline — a mapping that only works when handed an enum proves nothing about a deployment");
    }
}
