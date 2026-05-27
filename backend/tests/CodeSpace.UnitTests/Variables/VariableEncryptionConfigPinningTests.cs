using CodeSpace.Core.Services.Variables;
using Shouldly;

namespace CodeSpace.UnitTests.Variables;

/// <summary>
/// Pins the operator-facing env-var name (Rule 8). <see cref="VariableEncryptionConfig.MasterKeyEnvVar"/>
/// is the canonical name; <see cref="VariableEncryptionConfig.LegacyMasterKeyEnvVar"/>
/// remains pinned so the backward-compat fallback registered in CodeSpaceModule doesn't
/// silently become a no-op if someone renames the constant.
/// </summary>
[Trait("Category", "Unit")]
public class VariableEncryptionConfigPinningTests
{
    [Fact]
    public void MasterKeyEnvVarName_is_pinned_to_the_operator_facing_string()
    {
        VariableEncryptionConfig.MasterKeyEnvVar.ShouldBe("CODESPACE_VARIABLE_MASTER_KEY");
    }

    [Fact]
    public void LegacyMasterKeyEnvVarName_is_pinned_for_fallback_compatibility()
    {
        // Removing or renaming the legacy fallback would silently break every dev
        // environment that still has the old env var set. The CodeSpaceModule fallback
        // reads through this constant — pin it.
        VariableEncryptionConfig.LegacyMasterKeyEnvVar.ShouldBe("CODESPACE_TEAM_SECRET_MASTER_KEY");
    }

    [Fact]
    public void Constants_live_on_VariableEncryptionConfig()
    {
        // Hard-pin that the constants live on VariableEncryptionConfig (not on the
        // encryption class itself). Tests + startup-validation reach for the same
        // constants; moving them would break both call sites at runtime without compile
        // failure. This assert catches the move when the type is referenced here.
        typeof(VariableEncryptionConfig).GetField(nameof(VariableEncryptionConfig.MasterKeyEnvVar))
            .ShouldNotBeNull("MasterKeyEnvVar must stay a public const on VariableEncryptionConfig");
        typeof(VariableEncryptionConfig).GetField(nameof(VariableEncryptionConfig.LegacyMasterKeyEnvVar))
            .ShouldNotBeNull("LegacyMasterKeyEnvVar must stay a public const on VariableEncryptionConfig");
    }
}
