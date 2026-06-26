using CodeSpace.Core.Services.Credentials;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shouldly;

namespace CodeSpace.UnitTests.Credentials;

/// <summary>
/// Pins the shared-key-ring wiring (CodeSpaceDataProtection): the application-name discriminator must be a STABLE
/// constant stamped on every pod, because the framework default is the per-container content-root PATH — which
/// differs per pod and silently breaks cross-pod credential decryption. The DB persistence itself is proven by the
/// integration round-trip; this pins the two invariants that don't need a database.
/// </summary>
[Trait("Category", "Unit")]
public class CodeSpaceDataProtectionTests
{
    [Fact]
    public void ApplicationName_is_pinned_renaming_it_orphans_every_encrypted_credential()
    {
        // Data Protection refuses to decrypt a payload whose application discriminator differs. Renaming this const
        // silently makes EVERY already-encrypted model credential / git secret undecryptable across the whole fleet,
        // so the rename must be a deliberate, test-visible decision (Rule 8) — not an invisible refactor.
        CodeSpaceDataProtection.ApplicationName.ShouldBe("CodeSpace");
    }

    [Fact]
    public void AddCodeSpaceDataProtection_stamps_the_stable_application_discriminator()
    {
        var services = new ServiceCollection();
        services.AddCodeSpaceDataProtection();
        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IOptions<DataProtectionOptions>>().Value.ApplicationDiscriminator
            .ShouldBe(CodeSpaceDataProtection.ApplicationName,
                "all pods must share the SAME discriminator — the default is the per-container content-root path, which would differ per pod and break cross-pod decryption");
    }
}
