using System.Text.Json;
using CodeSpace.Core.Handlers.QueryHandlers.Storage;
using CodeSpace.Core.Services.Workflows.Artifacts.Providers;
using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Queries.Storage;
using Shouldly;

namespace CodeSpace.UnitTests.Handlers.Storage;

[Trait("Category", "Unit")]
public sealed class ListStorageProviderModulesQueryHandlerTests
{
    [Fact]
    public async Task Handler_projects_only_public_descriptor_metadata_in_deterministic_order()
    {
        var zeta = Module(
            "zeta-store/v2",
            "Zeta",
            JsonSerializer.SerializeToElement(new { type = "object", title = "Zeta config" }),
            JsonSerializer.SerializeToElement(new { type = "object", properties = new { token = new { type = "string", writeOnly = true } } }),
            StorageProviderCapabilities.ConditionalCreate | StorageProviderCapabilities.StreamingRead);
        var alpha = Module(
            "alpha-store/v1",
            "Alpha",
            JsonSerializer.SerializeToElement(new { type = "object", title = "Alpha config" }),
            JsonSerializer.SerializeToElement(new { type = "object", properties = new { } }),
            StorageProviderCapabilities.None);
        var handler = new ListStorageProviderModulesQueryHandler(new StubCatalog([zeta, alpha]));

        var result = await handler.Handle(new ListStorageProviderModulesQuery(), CancellationToken.None);

        result.Select(item => item.TypeKey).ShouldBe(["alpha-store/v1", "zeta-store/v2"], "wire order must not depend on module registration order");
        result[0].DisplayName.ShouldBe("Alpha");
        result[0].Capabilities.ShouldBeEmpty();
        result[0].ConfigSchema.GetProperty("title").GetString().ShouldBe("Alpha config");
        result[1].Capabilities.ShouldBe(["StreamingRead", "ConditionalCreate"], "flags are emitted once in stable enum-value order");
        result[1].SecretSchema.GetProperty("properties").GetProperty("token").GetProperty("writeOnly").GetBoolean().ShouldBeTrue();

        var wire = JsonSerializer.SerializeToElement(result, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        wire[0].EnumerateObject().Select(property => property.Name).ShouldBe([
            "typeKey", "displayName", "configSchema", "secretSchema", "capabilities"
        ], "the discovery API must never grow an accidental FactoryType or secret-value field");
        wire.GetRawText().ShouldNotContain("factoryType", Case.Insensitive);
        wire.GetRawText().ShouldNotContain(nameof(MustNotBeInstantiatedFactory), Case.Insensitive);
    }

    [Fact]
    public void Query_requires_authenticated_team_membership()
    {
        new ListStorageProviderModulesQuery().ShouldBeAssignableTo<IRequireTeamMembership>();
    }

    [Fact]
    public void Handler_fails_loudly_instead_of_silently_dropping_unknown_capability_bits()
    {
        var module = Module(
            "future-store/v1",
            "Future",
            JsonSerializer.SerializeToElement(new { type = "object" }),
            JsonSerializer.SerializeToElement(new { type = "object" }),
            (StorageProviderCapabilities)(1L << 20));
        var handler = new ListStorageProviderModulesQueryHandler(new StubCatalog([module]));

        var error = Should.Throw<InvalidOperationException>(() => handler.Handle(new ListStorageProviderModulesQuery(), CancellationToken.None));

        error.Message.ShouldContain("unknown capability bits", Case.Insensitive);
    }

    private static TestModule Module(string typeKey, string displayName, JsonElement configSchema, JsonElement secretSchema, StorageProviderCapabilities capabilities) => new()
    {
        TypeKey = typeKey,
        DisplayName = displayName,
        ConfigSchema = configSchema,
        SecretSchema = secretSchema,
        Capabilities = capabilities,
        FactoryType = typeof(MustNotBeInstantiatedFactory),
    };

    private sealed class StubCatalog(IReadOnlyList<IStorageProviderModule> modules) : IStorageProviderModuleCatalog
    {
        public IReadOnlyList<IStorageProviderModule> Modules { get; } = modules;
        public IStorageProviderModule? Get(string typeKey) => Modules.SingleOrDefault(module => module.TypeKey == typeKey);
        public IStorageProviderModule Require(string typeKey) => Get(typeKey) ?? throw new NotSupportedException();
    }

    private sealed class TestModule : IStorageProviderModule
    {
        public required string TypeKey { get; init; }
        public required string DisplayName { get; init; }
        public required JsonElement ConfigSchema { get; init; }
        public required JsonElement SecretSchema { get; init; }
        public required StorageProviderCapabilities Capabilities { get; init; }
        public required Type FactoryType { get; init; }
    }

    private sealed class MustNotBeInstantiatedFactory
    {
        public MustNotBeInstantiatedFactory() => throw new InvalidOperationException("The discovery handler must never instantiate a storage factory.");
    }
}
