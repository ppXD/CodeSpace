using CodeSpace.Core.Services.Workflows.Llm;
using CodeSpace.Core.Services.Workflows.Runtime;
using CodeSpace.Messages.Agents;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// The shared wire-identity policy (bounded length, control/format-char rejection, redaction) pinned directly off
/// BOTH call shapes <see cref="ObservedLlmModel"/> exposes: the credential overload (the in-process structured
/// clients — Anthropic/OpenAI — build a fresh per-call redactor from the request's credential) and the
/// redactor-taking overload (the physical accounting handler reuses its candidate's already-built redactor instead
/// of allocating a new one per POST). <c>CriticObservedModelTests</c> exercises the credential overload end to end
/// through the real HTTP clients; this file pins the policy itself and that the two entry points agree.
/// </summary>
[Trait("Category", "Unit")]
public sealed class ObservedLlmModelTests
{
    private const string ApiKey = "sk-observed-model-secret";
    private const string BaseUrl = "https://observed-model.test/v1";

    public static IEnumerable<object?[]> Cases()
    {
        yield return ["clean-model-name", "clean-model-name"];
        yield return [null, null];
        yield return ["", null];
        yield return ["   ", null];
        yield return ["model\nforged", null];
        yield return ["model\u202Eforged", null];
        yield return [new string('m', 500), new string('m', 500)];
        yield return [new string('m', 501), null];
        yield return ["模型/🧠:version-2", "模型/🧠:version-2"];
        yield return [ApiKey, null];
        yield return ["model-" + ApiKey, null];
        yield return [BaseUrl, null];
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void The_credential_overload_applies_length_format_and_redaction_policy(string? wire, string? expected)
    {
        var credential = new ResolvedModelCredential { Provider = "Test", ApiKey = ApiKey, BaseUrl = BaseUrl };

        ObservedLlmModel.FromWire(wire, credential).ShouldBe(expected);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void The_redactor_overload_applies_the_same_policy_the_accounting_handler_reuses(string? wire, string? expected)
    {
        var credentialRedactor = new PersistenceSecretRedactor(new[] { ApiKey, BaseUrl });

        ObservedLlmModel.FromWire(wire, credentialRedactor, captureRedactor: null).ShouldBe(expected);
    }

    [Fact]
    public void Both_overloads_agree_given_equivalent_redactors()
    {
        // The credential overload builds its own PersistenceSecretRedactor from the SAME two secrets the accounting
        // handler's candidate already built at construction — proving the shared function is the ONE policy, not
        // two implementations that merely happen to agree today.
        var credential = new ResolvedModelCredential { Provider = "Test", ApiKey = ApiKey, BaseUrl = BaseUrl };
        var equivalentRedactor = new PersistenceSecretRedactor(new[] { ApiKey, BaseUrl });

        foreach (var wire in new[] { "clean-model", ApiKey, "model\u202Eforged", null, "" })
            ObservedLlmModel.FromWire(wire, credential).ShouldBe(ObservedLlmModel.FromWire(wire, equivalentRedactor, captureRedactor: null));
    }

    [Fact]
    public void The_redactor_overload_also_rejects_a_value_only_the_capture_redactor_recognises()
    {
        var credentialRedactor = new PersistenceSecretRedactor(new[] { ApiKey, BaseUrl });
        var captureRedactor = new PersistenceSecretRedactor(["run-only-sensitive-value"]);

        ObservedLlmModel.FromWire("run-only-sensitive-value", credentialRedactor, captureRedactor).ShouldBeNull();
        ObservedLlmModel.FromWire("run-only-sensitive-value", credentialRedactor, captureRedactor: null).ShouldBe("run-only-sensitive-value", "no capture redactor supplied ⇒ nothing to catch this needle");
    }

    [Fact]
    public void The_credential_overload_also_reads_the_ambient_capture_redactor()
    {
        var credential = new ResolvedModelCredential { Provider = "Test", ApiKey = ApiKey, BaseUrl = BaseUrl };

        using var scope = LlmCallContext.Push(new LlmCallScope(Guid.NewGuid(), Guid.NewGuid(), "n", "", "k", Logger: null!, Offloader: null!, CaptureRedactor: new PersistenceSecretRedactor(["run-only-sensitive-value"])));

        ObservedLlmModel.FromWire("run-only-sensitive-value", credential).ShouldBeNull();
    }
}
