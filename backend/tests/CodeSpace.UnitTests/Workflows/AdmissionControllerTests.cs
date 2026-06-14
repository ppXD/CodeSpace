using CodeSpace.Core.Services.Agents;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// Pins the fail-closed admission-cap contract: the env-var constant NAMES (an operator pins a custom cap via
/// them — a rename is a silent prod regression, Rule 8), the parse/clamp of those env values, and the pure
/// admit/reject branch logic (under cap admits; at/over the per-team cap throws; at/over the global cap throws).
/// Env-var writes are restored per test (xUnit news up the class per [Fact] + Disposes after each), keeping the
/// tests isolated. The DB-count + fail-closed-on-query-fault behaviour is pinned by the integration tier.
/// </summary>
[Trait("Category", "Unit")]
public class AdmissionControllerTests : IDisposable
{
    private readonly string? _originalPerTeam = Environment.GetEnvironmentVariable(AdmissionController.MaxInflightPerTeamEnvVar);
    private readonly string? _originalGlobal = Environment.GetEnvironmentVariable(AdmissionController.MaxInflightGlobalEnvVar);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(AdmissionController.MaxInflightPerTeamEnvVar, _originalPerTeam);
        Environment.SetEnvironmentVariable(AdmissionController.MaxInflightGlobalEnvVar, _originalGlobal);
    }

    [Fact]
    public void PerTeam_env_var_constant_name_is_pinned()
    {
        // Renaming this breaks every operator who pinned a custom per-team cap via env.
        AdmissionController.MaxInflightPerTeamEnvVar.ShouldBe("CODESPACE_AGENT_MAX_INFLIGHT_PER_TEAM");
    }

    [Fact]
    public void Global_env_var_constant_name_is_pinned()
    {
        // Renaming this breaks every operator who pinned a custom global cap via env.
        AdmissionController.MaxInflightGlobalEnvVar.ShouldBe("CODESPACE_AGENT_MAX_INFLIGHT_GLOBAL");
    }

    [Fact]
    public void Defaults_are_chosen_to_not_break_a_normal_large_fan_out()
    {
        // The whole point of the gate is to catch a runaway, NOT to throttle an ordinary team — pin the safe
        // defaults so a thoughtless edit can't silently tighten them under a normal fan-out.
        AdmissionController.DefaultMaxInflightPerTeam.ShouldBe(50);
        AdmissionController.DefaultMaxInflightGlobal.ShouldBe(200);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not-a-number")]
    [InlineData("")]
    public void ParseCap_falls_back_to_the_default_when_unset_or_unparseable(string? raw)
    {
        AdmissionController.ParseCap(raw, @default: 50).ShouldBe(50);
    }

    [Fact]
    public void ParseCap_reads_a_valid_override()
    {
        AdmissionController.ParseCap("75", @default: 50).ShouldBe(75);
    }

    [Theory]
    [InlineData("0", 1)]            // below the floor → clamped up to 1 (fully-serialized), never 0 (a closed gate)
    [InlineData("-5", 1)]
    [InlineData("99999999", 100_000)] // above the ceiling → clamped down so a fat-fingered value can't disable the gate
    public void ParseCap_clamps_out_of_range_values(string raw, int expected)
    {
        AdmissionController.ParseCap(raw, @default: 50).ShouldBe(expected);
    }

    [Fact]
    public void Under_both_caps_admits()
    {
        Environment.SetEnvironmentVariable(AdmissionController.MaxInflightPerTeamEnvVar, "50");
        Environment.SetEnvironmentVariable(AdmissionController.MaxInflightGlobalEnvVar, "200");

        Should.NotThrow(() => AdmissionController.EnsureUnderCaps(Guid.NewGuid(), teamInflight: 49, globalInflight: 199));
    }

    [Theory]
    [InlineData(50)]   // AT the cap — the 51st run is refused
    [InlineData(60)]   // already OVER the cap
    public void At_or_over_the_per_team_cap_throws(int teamInflight)
    {
        Environment.SetEnvironmentVariable(AdmissionController.MaxInflightPerTeamEnvVar, "50");
        Environment.SetEnvironmentVariable(AdmissionController.MaxInflightGlobalEnvVar, "200");

        var teamId = Guid.NewGuid();

        var ex = Should.Throw<AgentRunAdmissionException>(() =>
            AdmissionController.EnsureUnderCaps(teamId, teamInflight, globalInflight: 0));

        ex.Message.ShouldContain(teamId.ToString());
        ex.Message.ShouldContain(AdmissionController.MaxInflightPerTeamEnvVar);   // names the env var to raise
    }

    [Theory]
    [InlineData(200)]  // AT the global cap
    [InlineData(250)]  // already OVER
    public void At_or_over_the_global_cap_throws_even_when_the_team_is_under_its_cap(int globalInflight)
    {
        Environment.SetEnvironmentVariable(AdmissionController.MaxInflightPerTeamEnvVar, "50");
        Environment.SetEnvironmentVariable(AdmissionController.MaxInflightGlobalEnvVar, "200");

        // The team is well under its own cap, but the deployment as a whole is full.
        var ex = Should.Throw<AgentRunAdmissionException>(() =>
            AdmissionController.EnsureUnderCaps(Guid.NewGuid(), teamInflight: 1, globalInflight: globalInflight));

        ex.Message.ShouldContain(AdmissionController.MaxInflightGlobalEnvVar);   // names the global env var to raise
    }
}
