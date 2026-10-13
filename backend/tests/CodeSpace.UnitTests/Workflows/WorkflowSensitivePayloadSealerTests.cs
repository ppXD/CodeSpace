using System.Text.Json;
using CodeSpace.Core.Services.Workflows.Runtime;
using CodeSpace.UnitTests.Fleet;
using Microsoft.AspNetCore.DataProtection;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// The sealer is the seam that keeps a resolved workflow secret out of a payload's PUBLIC column while the run it is
/// handed to still executes with the exact value: the public copy is masked, the exact copy and the values it embeds
/// are encrypted, and a payload carrying no secret is left byte-identical with no ciphertext at all. While a
/// previous-version worker is live — it reads only the public column — a secret-bearing payload is written verbatim and
/// unsealed, the one shape that worker can execute.
/// </summary>
[Trait("Category", "Unit")]
public sealed class WorkflowSensitivePayloadSealerTests
{
    private const string Secret = "CANARY-sealer-3f1c9a7e5b2d";

    private static readonly WorkflowSensitivePayloadSealer Sealer = new(new DataProtectionWorkflowSensitivePayloadProtector(new EphemeralDataProtectionProvider()), FixedFleetCapabilities.AllCurrent);

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
        var sealer = new WorkflowSensitivePayloadSealer(protector, FixedFleetCapabilities.AllCurrent);
        var nodeOutputs = protector.Protect("""{"outputs":{"x":"y"}}""");

        Should.Throw<InvalidOperationException>(() => sealer.Open("{}", nodeOutputs));
    }

    [Fact]
    public void While_a_previous_version_worker_is_live_a_secret_bearing_payload_is_written_verbatim_and_unsealed()
    {
        var fleet = FixedFleetCapabilities.WithPreviousVersionWorker;
        var sealer = new WorkflowSensitivePayloadSealer(new DataProtectionWorkflowSensitivePayloadProtector(new EphemeralDataProtectionProvider()), fleet);
        var payload = Payload(new { token = Secret, header = $"Bearer {Secret}" });

        var written = sealer.Seal(payload, new PersistenceSecretRedactor([Secret]));

        written.PublicJson.ShouldBe(payload.GetRawText(), "the previous build executes the public column, so it must hold the exact payload — byte for byte what that build wrote itself");
        written.Ciphertext.ShouldBeNull("a sealed copy beside the exact one could go stale the moment a previous-version worker rewrites the public column");
        fleet.Asks.ShouldBe(1);
    }

    [Fact]
    public void A_payload_with_nothing_to_seal_never_consults_the_fleet()
    {
        var fleet = FixedFleetCapabilities.WithPreviousVersionWorker;
        var sealer = new WorkflowSensitivePayloadSealer(new DataProtectionWorkflowSensitivePayloadProtector(new EphemeralDataProtectionProvider()), fleet);

        sealer.Seal(Payload(new { token = "public" }), new PersistenceSecretRedactor([Secret])).Ciphertext.ShouldBeNull();

        fleet.Asks.ShouldBe(0, "the fleet read is for payloads that would be sealed — every other write stays off the registry");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_copy_of_a_sealed_payload_for_a_new_row_is_sealed_again_through_the_fleet_gate(bool everyLiveWorkerReadsSealedPayloads)
    {
        // A replay or rerun fork is a new row that any live worker may execute — a previous-version one included.
        var protector = new DataProtectionWorkflowSensitivePayloadProtector(new EphemeralDataProtectionProvider());
        var payload = Payload(new { token = Secret, other = "kept" });
        var original = new WorkflowSensitivePayloadSealer(protector, FixedFleetCapabilities.AllCurrent).Seal(payload, new PersistenceSecretRedactor([Secret, "never-present-secret"]));
        var sealer = new WorkflowSensitivePayloadSealer(protector, new FixedFleetCapabilities(everyLiveWorkerReadsSealedPayloads));

        var copy = sealer.SealCopy(original.PublicJson, original.Ciphertext);

        if (!everyLiveWorkerReadsSealedPayloads)
        {
            copy.PublicJson.ShouldBe(payload.GetRawText(), "the previous build builds a fork's trigger scope from the public column alone");
            copy.Ciphertext.ShouldBeNull();
            return;
        }

        copy.PublicJson.ShouldBe(original.PublicJson);
        copy.Ciphertext.ShouldNotBeNull();
        var opened = sealer.Open(copy.PublicJson, copy.Ciphertext);
        opened.Json.ShouldBe(payload.GetRawText());
        opened.Secrets.ShouldBe([Secret]);
    }

    [Fact]
    public void A_copy_of_an_unsealed_payload_is_carried_over_byte_identical_without_consulting_the_fleet()
    {
        var fleet = FixedFleetCapabilities.WithPreviousVersionWorker;
        var sealer = new WorkflowSensitivePayloadSealer(new DataProtectionWorkflowSensitivePayloadProtector(new EphemeralDataProtectionProvider()), fleet);
        const string legacy = """{ "token" : "written-before-sealing" }""";

        var copy = sealer.SealCopy(legacy, null);

        copy.PublicJson.ShouldBe(legacy, "a fork of an unsealed run carries the original's column exactly as before");
        copy.Ciphertext.ShouldBeNull();
        fleet.Asks.ShouldBe(0);
    }

    [Fact]
    public void Resealing_a_payload_for_the_row_a_current_worker_already_runs_ignores_the_fleet_gate()
    {
        // The row is Running on a current worker: no previous-version worker ever launches it, and writing it unsealed
        // would drop the secrets its launch folded into the handle's redactor fingerprint.
        var fleet = FixedFleetCapabilities.WithPreviousVersionWorker;
        var sealer = new WorkflowSensitivePayloadSealer(new DataProtectionWorkflowSensitivePayloadProtector(new EphemeralDataProtectionProvider()), fleet);
        var payload = Payload(new { goal = $"Deploy with {Secret}" });

        var written = sealer.Reseal(payload, new PersistenceSecretRedactor([Secret]));

        written.PublicJson.ShouldNotContain(Secret);
        written.Ciphertext.ShouldNotBeNull();
        sealer.Open(written.PublicJson, written.Ciphertext).Secrets.ShouldBe([Secret]);
        fleet.Asks.ShouldBe(0);
    }
}
