using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows.Supervisor;

/// <summary>
/// Pins the real-model gate POLICY (which wires gate CI): the blessed default (Anthropic required, OpenAI
/// informational) and the env-override contract. Tests run sequentially within this class (xUnit serializes a class),
/// and the only other reader (the RealModel flow tests) self-skips without secrets, so the env mutation here can't race.
/// </summary>
public sealed class RealModelGateTests
{
    [Fact]
    public void RequiredProvidersEnvVar_constant_name_is_pinned()
    {
        // Renaming this breaks any operator who pinned the blessed wire set via env. Hard-pin the literal.
        RealModelGate.RequiredProvidersEnvVar.ShouldBe("CODESPACE_REALMODEL_REQUIRED_PROVIDERS");
    }

    [Fact]
    public void By_default_Anthropic_gates_and_OpenAI_is_informational()
    {
        var prior = Environment.GetEnvironmentVariable(RealModelGate.RequiredProvidersEnvVar);
        Environment.SetEnvironmentVariable(RealModelGate.RequiredProvidersEnvVar, null);
        try
        {
            RealModelGate.IsRequired("Anthropic").ShouldBeTrue("Anthropic is the default blessed wire");
            RealModelGate.IsRequired("anthropic").ShouldBeTrue("provider match is case-insensitive");
            RealModelGate.IsRequired("OpenAI").ShouldBeFalse("OpenAI is informational by default — its verdict must not gate CI");
        }
        finally
        {
            Environment.SetEnvironmentVariable(RealModelGate.RequiredProvidersEnvVar, prior);
        }
    }

    [Fact]
    public void An_operator_can_rebless_the_wires_via_env()
    {
        var prior = Environment.GetEnvironmentVariable(RealModelGate.RequiredProvidersEnvVar);
        Environment.SetEnvironmentVariable(RealModelGate.RequiredProvidersEnvVar, "OpenAI, Anthropic");
        try
        {
            RealModelGate.IsRequired("OpenAI").ShouldBeTrue("the env override blesses OpenAI too");
            RealModelGate.IsRequired("Anthropic").ShouldBeTrue();
        }
        finally
        {
            Environment.SetEnvironmentVariable(RealModelGate.RequiredProvidersEnvVar, prior);
        }
    }
}
