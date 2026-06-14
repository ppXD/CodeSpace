using CodeSpace.Api.Extensions.Hangfire;
using Shouldly;

namespace CodeSpace.IntegrationTests.Hangfire;

/// <summary>
/// Pins the deployment-topology gate on <see cref="CodeSpaceHangfireRegistrar"/>: the env-var name (Rule 8)
/// and the default-ON, opt-OUT polarity of <see cref="CodeSpaceHangfireRegistrar.IsProcessingEnabled(string?)"/>.
/// The gate decides whether a pod starts the Hangfire SERVER (job processing) — storage + the job client
/// (enqueue) are always registered regardless, so a processing-OFF public pod can still enqueue. The pure
/// overload is the testable contract; the one-line <c>if</c> around <c>AddHangfireServer</c> /
/// <c>ScanHangfireRecurringJobs</c> is then self-evident, so this avoids standing up the web host.
/// Lives in IntegrationTests (not UnitTests) only because <see cref="CodeSpaceHangfireRegistrar"/> is a
/// CodeSpace.Api-only type; it touches no database, hence no Postgres collection.
/// </summary>
[Trait("Category", "Unit")]
public class HangfireProcessingGateTests
{
    [Fact]
    public void ProcessingEnabledEnvVar_name_is_pinned() =>
        // Renaming this silently turns processing back ON for an operator who deployed a public pod
        // expecting it OFF (Rule 8).
        CodeSpaceHangfireRegistrar.ProcessingEnabledEnvVar.ShouldBe("CODESPACE_HANGFIRE_PROCESSING_ENABLED");

    [Theory]
    [InlineData(null, true)]        // unset → processing ON (today's all-in-one default)
    [InlineData("", true)]
    [InlineData("1", true)]
    [InlineData("true", true)]
    [InlineData("TRUE", true)]
    [InlineData("yes", true)]
    [InlineData("garbage", true)]   // default-ON: anything that isn't an explicit disable keeps processing
    [InlineData("0", false)]
    [InlineData("false", false)]
    [InlineData("False", false)]
    [InlineData("FALSE", false)]
    [InlineData(" false ", false)]  // trimmed
    [InlineData(" 0 ", false)]      // trimmed
    public void IsProcessingEnabled_is_default_on_and_off_only_for_explicit_disable_values(string? raw, bool expected) =>
        CodeSpaceHangfireRegistrar.IsProcessingEnabled(raw).ShouldBe(expected);

    [Fact]
    public void Env_reading_wrapper_reads_the_pinned_var()
    {
        var original = Environment.GetEnvironmentVariable(CodeSpaceHangfireRegistrar.ProcessingEnabledEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(CodeSpaceHangfireRegistrar.ProcessingEnabledEnvVar, "false");
            CodeSpaceHangfireRegistrar.IsProcessingEnabled().ShouldBeFalse("the wrapper reads ProcessingEnabledEnvVar");

            Environment.SetEnvironmentVariable(CodeSpaceHangfireRegistrar.ProcessingEnabledEnvVar, null);
            CodeSpaceHangfireRegistrar.IsProcessingEnabled().ShouldBeTrue("unset → default-ON");
        }
        finally
        {
            Environment.SetEnvironmentVariable(CodeSpaceHangfireRegistrar.ProcessingEnabledEnvVar, original);
        }
    }
}
