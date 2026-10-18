using CodeSpace.Core.Services.Health;
using CodeSpace.Core.Settings;
using CodeSpace.Core.Settings.Logging;
using CodeSpace.Messages.Enums;
using Microsoft.Extensions.Configuration;
using Shouldly;

namespace CodeSpace.UnitTests.Health;

/// <summary>Which build and which role this process is — the two facts that let an API image and a worker image be compared from outside.</summary>
[Trait("Category", "Unit")]
public sealed class HostPostureTests
{
    [Theory]
    [InlineData("Api", "Api", false)]
    [InlineData("Worker", "Worker", true)]
    [InlineData(null, "Worker", true)]
    [InlineData("nonsense", "Worker", true)]
    public void The_role_is_the_one_the_deployment_gave_the_process_by_the_same_rule_that_decides_what_it_runs(string? configured, string role, bool processesJobs)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { [HangfireHostingSetting.ConfigurationKey] = configured }).Build();

        var posture = new HostPosture(new HangfireHostingSetting(configuration));

        posture.Role.ShouldBe(Enum.Parse<HangfireHosting>(role));
        posture.RoleName.ShouldBe(role, "the wire name /health prints — a rename is a contract change");
        posture.ProcessesJobs.ShouldBe(processesJobs);
    }

    [Fact]
    public void The_build_is_the_identity_stamped_on_every_log_line()
    {
        var posture = new HostPosture(HangfireHosting.Api, BuildIdentity.Value);

        posture.Build.ShouldBe(BuildIdentity.Value);
    }

    [Theory]
    [InlineData(HangfireHosting.Api, "enqueues", "no jobs")]
    [InlineData(HangfireHosting.Worker, "processes jobs", "recurring")]
    public void The_role_is_explained_in_a_phrase_for_the_boot_line(HangfireHosting role, string first, string second)
    {
        var meaning = new HostPosture(role, "1.0.0+abc").RoleMeaning;

        meaning.ShouldContain(first);
        meaning.ShouldContain(second);
    }
}
