using System.Text.Json;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Workflows.Artifacts.Defaults;
using CodeSpace.Core.Services.Workflows.Artifacts.Providers;
using CodeSpace.Core.Services.Workflows.Artifacts.Providers.AliyunOss;
using CodeSpace.Core.Services.Workflows.Artifacts.Providers.Local;
using CodeSpace.Core.Services.Workflows.Artifacts.Routing;
using CodeSpace.Core.Services.Workflows.Artifacts.Routing.DataClasses;
using CodeSpace.Messages.Constants;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows.Artifacts.Defaults;

/// <summary>
/// Admission rules for the deployment storage template. Nothing consumes a template yet, so these are the only place
/// a bad one is stopped before the materializer lane ever reads it.
/// </summary>
[Trait("Category", "Unit")]
public sealed class StorageDefaultRulesTests
{
    /// <summary>
    /// Rule 8 pin. The literal crosses a boundary a rename cannot follow: it is stored as a row in the
    /// <c>permission</c> table by migration 0173, so renaming the constant without a matching migration leaves the
    /// deployment's only holder of this capability unable to prove it.
    /// </summary>
    [Fact]
    public void Permission_constant_value_is_pinned() => Permissions.StorageDefaultsManage.ShouldBe("storage.defaults.manage");

    /// <summary>
    /// Deployment-level write access must never ride the every-account grant. That list is handed to every account
    /// that exists, so adding this to it would give every signed-in user authority over all teams' storage.
    /// </summary>
    [Fact]
    public void Permission_is_not_granted_to_every_account() =>
        Permissions.GrantedToEveryAccount.ShouldNotContain(Permissions.StorageDefaultsManage);

    [Theory]
    [InlineData("workflow-artifact/v1", "workflow-artifact/v1")]
    [InlineData("  Agent-Run-Log/v1  ", "agent-run-log/v1")]
    public void Data_class_type_key_is_normalized(string input, string expected) =>
        StorageDefaultRules.NormalizeDataClassTypeKey(input).ShouldBe(expected);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("workflow-artifact")]
    [InlineData("workflow-artifact/v0")]
    [InlineData("workflow artifact/v1")]
    public void Data_class_type_key_fails_closed(string? input) =>
        Should.Throw<ArgumentException>(() => StorageDefaultRules.NormalizeDataClassTypeKey(input));

    [Fact]
    public void Namespace_root_is_trimmed_and_kept_verbatim() =>
        StorageDefaultRules.NormalizeNamespaceRoot("  codespace-defaults/  ").ShouldBe("codespace-defaults/");

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("bad\nroot")]
    public void Namespace_root_fails_closed(string? input) =>
        Should.Throw<ArgumentException>(() => StorageDefaultRules.NormalizeNamespaceRoot(input));

    [Fact]
    public void Namespace_root_is_bounded() =>
        Should.Throw<ArgumentException>(() => StorageDefaultRules.NormalizeNamespaceRoot(new string('a', StorageDefaultRules.MaxNamespaceRootLength + 1)));

    /// <summary>
    /// The template's config is partial by construction — the provider's namespace field is assembled per team at
    /// materialization — so a missing REQUIRED property is lawful here. local-rwx requires <c>rootPath</c>, which is
    /// exactly the field the namespace root replaces.
    /// </summary>
    [Fact]
    public void Partial_config_may_omit_the_required_namespace_field() =>
        Should.NotThrow(() => StorageDefaultRules.ValidatePartialConfig(Json("{}"), new LocalRwxStorageProviderModule()));

    [Fact]
    public void Partial_config_still_validates_the_properties_it_does_carry()
    {
        var module = new AliyunOssStorageProviderModule();

        Should.NotThrow(() => StorageDefaultRules.ValidatePartialConfig(Json("""{"endpoint":"oss-cn-hangzhou.aliyuncs.com"}"""), module));
        Should.Throw<ArgumentException>(() => StorageDefaultRules.ValidatePartialConfig(Json("""{"endpoint":"NOT A HOST"}"""), module));
        Should.Throw<ArgumentException>(() => StorageDefaultRules.ValidatePartialConfig(Json("""{"unknownField":"x"}"""), module));
    }

    /// <summary>
    /// A secret in the template's plaintext config is the one thing partial validation must still refuse.
    ///
    /// <para>The provider here is a stub, on purpose. Both shipped modules close their ConfigSchema with
    /// <c>additionalProperties: false</c>, so against either of them this assertion passes even with the secret check
    /// deleted — the schema rejects the property as merely unknown, and the test proves nothing about the guard. A
    /// provider whose config is OPEN is the only shape that reaches the secret check, and it is a shape the module
    /// contract allows, so it is the shape worth pinning.</para>
    /// </summary>
    [Fact]
    public void Partial_config_refuses_a_secret_schema_property()
    {
        var exception = Should.Throw<ArgumentException>(() =>
            StorageDefaultRules.ValidatePartialConfig(Json("""{"apiToken":"must-never-persist"}"""), new OpenConfigModule()));

        exception.Message.ShouldContain("apiToken");
        exception.Message.ShouldNotContain("must-never-persist");
    }

    /// <summary>The same open provider accepts a non-secret property, so the refusal above is about the secret and not about openness.</summary>
    [Fact]
    public void Partial_config_accepts_a_non_secret_property_on_an_open_provider() =>
        Should.NotThrow(() => StorageDefaultRules.ValidatePartialConfig(Json("""{"anythingElse":"fine"}"""), new OpenConfigModule()));

    [Fact]
    public void Partial_config_refuses_the_property_that_IS_the_namespace()
    {
        // The template's own doc says its config EXCLUDES every namespace field, and that NamespaceRoot is "a ROOT,
        // never a finished namespace". A config that already sets the provider's namespace property would shadow the
        // per-team segment the materializer composes and hand every team one namespace — while both comments still
        // read as though that could not happen. Object keys carry no team segment, so one namespace means one object.
        var rejected = Should.Throw<ArgumentException>(
            () => StorageDefaultRules.ValidatePartialConfig(Json("""{"prefix":"one-namespace-for-everyone/"}"""), new OpenConfigModule()));

        rejected.Message.ShouldContain("prefix", Case.Sensitive, "the refusal must name the offending property, not merely decline");
    }

    [Fact]
    public void A_provider_that_cannot_subdivide_its_namespace_cannot_be_a_deployment_default() =>
        // Refusing at admission is the last point where the deployment can still choose otherwise: once teams are
        // materialized onto one shared namespace their routes are Active, and an Active route never returns to Draft.
        Should.Throw<ArgumentException>(() => StorageDefaultRules.ValidatePartialConfig(Json("""{"anythingElse":"fine"}"""), new UndividableModule()));

    [Fact]
    public void Partial_config_must_be_an_object() =>
        Should.Throw<ArgumentException>(() => StorageDefaultRules.ValidatePartialConfig(Json("[]"), new LocalRwxStorageProviderModule()));

    /// <summary>
    /// The owner's decision, derived rather than remembered: a class that keeps a local home is taken off it
    /// IRREVERSIBLY by materialization, so it may only be adopted Explicitly.
    /// </summary>
    [Fact]
    public void A_class_with_a_local_home_cannot_be_adopted_automatically()
    {
        var dataClass = new WorkflowArtifactDataClass();

        var exception = Should.Throw<ArgumentException>(() => StorageDefaultRules.EnsureAdoptionPolicyAllowed(dataClass, StorageDefaultAdoptionPolicy.Automatic));

        exception.Message.ShouldContain(dataClass.TypeKey);
        Should.NotThrow(() => StorageDefaultRules.EnsureAdoptionPolicyAllowed(dataClass, StorageDefaultAdoptionPolicy.Explicit));
    }

    /// <summary>
    /// The mutation guard for the rule above. It is derived from the interface, so deleting
    /// <c>IRoutedDataClassLocalFallback</c> from this declaration would silently make Automatic legal for the class
    /// the owner ruled out. Pinned here so that deletion is a build-visible decision.
    /// </summary>
    [Fact]
    public void The_workflow_artifact_class_is_the_one_that_declares_a_local_home()
    {
        new WorkflowArtifactDataClass().ShouldBeAssignableTo<IRoutedDataClassLocalFallback>();
        new AgentRunLogDataClass().ShouldNotBeAssignableTo<IRoutedDataClassLocalFallback>();
    }

    /// <summary>A class with nowhere else to put its bytes is refusing writes until cut over, so cutting it over takes nothing away.</summary>
    [Fact]
    public void A_class_with_no_local_home_may_be_adopted_automatically() =>
        Should.NotThrow(() => StorageDefaultRules.EnsureAdoptionPolicyAllowed(new AgentRunLogDataClass(), StorageDefaultAdoptionPolicy.Automatic));

    [Fact]
    public void An_undeclared_adoption_policy_fails_closed() =>
        Should.Throw<ArgumentException>(() => StorageDefaultRules.EnsureAdoptionPolicyAllowed(new AgentRunLogDataClass(), (StorageDefaultAdoptionPolicy)42));

    /// <summary>Both allowed values are declared, and the CHECK constraint in migration 0173 lists exactly these names.</summary>
    [Fact]
    public void The_adoption_policy_vocabulary_is_pinned() =>
        Enum.GetNames<StorageDefaultAdoptionPolicy>().ShouldBe(["Automatic", "Explicit"]);

    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    /// <summary>A lawful module whose ConfigSchema does NOT close additional properties — the only shape that reaches the secret-leak check.</summary>
    private sealed class OpenConfigModule : IStorageProviderModule, IStorageProviderTeamNamespace
    {
        public string TypeKey => "open-config/v1";
        public string DisplayName => "Open config test provider";
        public JsonElement ConfigSchema => Json("""{"type":"object","properties":{},"additionalProperties":true}""");
        public JsonElement SecretSchema => Json("""{"type":"object","properties":{"apiToken":{"type":"string"}},"required":["apiToken"],"additionalProperties":false}""");
        public StorageProviderCapabilities Capabilities => StorageProviderCapabilities.None;
        public Type FactoryType => typeof(OpenConfigModule);
        public string TeamNamespaceProperty => "prefix";
        public string ComposeTeamNamespace(string namespaceRoot, string teamSegment) => $"{namespaceRoot}/{teamSegment}";
    }

    /// <summary>The same module WITHOUT the subdivision sibling — a provider that can only ever name one namespace.</summary>
    private sealed class UndividableModule : IStorageProviderModule
    {
        public string TypeKey => "undividable/v1";
        public string DisplayName => "Undividable test provider";
        public JsonElement ConfigSchema => Json("""{"type":"object","properties":{},"additionalProperties":true}""");
        public JsonElement SecretSchema => Json("""{"type":"object","properties":{},"additionalProperties":false}""");
        public StorageProviderCapabilities Capabilities => StorageProviderCapabilities.None;
        public Type FactoryType => typeof(UndividableModule);
    }
}
