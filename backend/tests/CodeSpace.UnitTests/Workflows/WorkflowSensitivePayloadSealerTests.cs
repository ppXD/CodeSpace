using System.Text.Json;
using CodeSpace.Core.Services.Workflows.Runtime;
using Microsoft.AspNetCore.DataProtection;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// The sealer is the seam that keeps a resolved workflow secret out of a payload's PUBLIC column while the run it is
/// handed to still executes with the exact value: the public copy is masked, the exact copy and the values it embeds
/// are encrypted, and a payload carrying no secret is left byte-identical with no ciphertext at all.
/// </summary>
[Trait("Category", "Unit")]
public sealed class WorkflowSensitivePayloadSealerTests
{
    private const string Secret = "CANARY-sealer-3f1c9a7e5b2d";

    private static readonly WorkflowSensitivePayloadSealer Sealer = new(new DataProtectionWorkflowSensitivePayloadProtector(new EphemeralDataProtectionProvider()));

    private static JsonElement Payload(object value) => JsonSerializer.SerializeToElement(value);

    [Fact]
    public void A_payload_without_any_given_secret_stays_verbatim_and_unsealed()
    {
        var payload = Payload(new { token = "public", nested = new { n = 42 } });

        var sealedPayload = Sealer.Seal(payload, new PersistenceSecretRedactor([Secret]));

        sealedPayload.PublicJson.ShouldBe(payload.GetRawText());
        sealedPayload.Ciphertext.ShouldBeNull("nothing was masked, so the public copy is itself exact — a row written for it is byte-identical to a pre-sealing row");
    }

    [Fact]
    public void A_secret_bearing_payload_is_masked_in_public_and_opens_to_the_exact_payload_and_its_secrets()
    {
        var payload = Payload(new { token = Secret, header = $"Bearer {Secret}", other = "kept" });

        var sealedPayload = Sealer.Seal(payload, new PersistenceSecretRedactor([Secret, "never-present-secret"]));

        sealedPayload.PublicJson.ShouldNotContain(Secret);
        sealedPayload.PublicJson.ShouldContain("Bearer [REDACTED]");
        sealedPayload.PublicJson.ShouldContain("kept");
        sealedPayload.Ciphertext.ShouldNotBeNull();
        sealedPayload.Ciphertext.ShouldNotContain(Secret, customMessage: "the exact copy is encrypted, never stored as text beside the mask");

        var opened = Sealer.Open(sealedPayload.PublicJson, sealedPayload.Ciphertext);

        JsonDocument.Parse(opened.Json).RootElement.GetProperty("header").GetString().ShouldBe($"Bearer {Secret}");
        opened.Secrets.ShouldBe([Secret], "only the values the payload actually embeds travel with it — never the rest of the sealing scope");
    }

    [Fact]
    public void An_unsealed_row_opens_from_its_public_column_with_no_secrets()
    {
        var opened = Sealer.Open("""{"token":"legacy"}""", null);

        opened.Json.ShouldBe("""{"token":"legacy"}""");
        opened.Secrets.ShouldBeEmpty();
    }

    [Fact]
    public void A_ciphertext_of_another_kind_under_the_same_purpose_is_refused()
    {
        var protector = new DataProtectionWorkflowSensitivePayloadProtector(new EphemeralDataProtectionProvider());
        var sealer = new WorkflowSensitivePayloadSealer(protector);
        var nodeOutputs = protector.Protect("""{"outputs":{"x":"y"}}""");

        Should.Throw<InvalidOperationException>(() => sealer.Open("{}", nodeOutputs));
    }
}
