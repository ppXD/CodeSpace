using System.Text.Json;
using CodeSpace.Core.Services.Workflows.Artifacts.Providers;
using CodeSpace.Core.Services.Workflows.Artifacts.Providers.Local;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows.Artifacts.Providers;

[Trait("Category", "Unit")]
public class StorageProviderModuleCatalogTests
{
    [Fact]
    public void Get_and_require_resolve_an_exact_open_versioned_type_key()
    {
        var v1 = Module("acme-object/v1", "Acme v1");
        var v2 = Module("acme-object/v2", "Acme v2");
        var catalog = new StorageProviderModuleCatalog([v2, v1]);

        catalog.Get("acme-object/v1").ShouldBeSameAs(v1);
        catalog.Require("acme-object/v2").ShouldBeSameAs(v2);
        catalog.Modules.ShouldBe([v1, v2], "Settings discovery is stable regardless of Autofac contribution order");
    }

    [Fact]
    public void Get_returns_null_for_an_unknown_type_key()
    {
        var catalog = new StorageProviderModuleCatalog([Module("acme-object/v1")]);

        catalog.Get("future-provider/v7").ShouldBeNull();
    }

    [Fact]
    public void Require_throws_a_legible_error_for_an_unknown_type_key()
    {
        var catalog = new StorageProviderModuleCatalog([Module("acme-object/v1"), Module("other-store/v3")]);

        var error = Should.Throw<NotSupportedException>(() => catalog.Require("future-provider/v7"));

        error.Message.ShouldContain("future-provider/v7");
        error.Message.ShouldContain("acme-object/v1");
        error.Message.ShouldContain("other-store/v3");
    }

    [Fact]
    public void Constructor_fails_loudly_when_two_modules_claim_the_same_type_key()
    {
        var first = Module("acme-object/v1", "First claim");
        var second = Module("acme-object/v1", "Second claim");

        var error = Should.Throw<InvalidOperationException>(() => new StorageProviderModuleCatalog([first, second]));

        error.Message.ShouldContain("acme-object/v1");
        error.Message.ShouldContain("First claim");
        error.Message.ShouldContain("Second claim");
    }

    [Theory]
    [InlineData("")]
    [InlineData("local-rwx")]
    [InlineData("Local-rwx/v1")]
    [InlineData("local-rwx/v0")]
    [InlineData("local_rwx/v1")]
    public void Constructor_rejects_noncanonical_or_unversioned_type_keys(string typeKey)
    {
        var error = Should.Throw<InvalidOperationException>(() => new StorageProviderModuleCatalog([Module(typeKey)]));

        error.Message.ShouldContain("TypeKey");
    }

    [Fact]
    public void Constructor_rejects_non_object_schemas_before_they_reach_a_settings_form()
    {
        var invalid = new TestModule
        {
            TypeKey = "acme-object/v1",
            DisplayName = "Acme",
            ConfigSchema = JsonSerializer.SerializeToElement("not-a-schema"),
            SecretSchema = EmptySchema(),
            Capabilities = StorageProviderCapabilities.None,
            FactoryType = typeof(TestFactory)
        };

        var error = Should.Throw<InvalidOperationException>(() => new StorageProviderModuleCatalog([invalid]));

        error.Message.ShouldContain(nameof(IStorageProviderModule.ConfigSchema));
    }

    [Fact]
    public void Constructor_rejects_a_concrete_factory_that_does_not_implement_the_driver_factory_contract()
    {
        var invalid = Module("invalid-factory/v1");
        invalid.FactoryType = typeof(string);

        var error = Should.Throw<InvalidOperationException>(() => new StorageProviderModuleCatalog([invalid]));

        error.Message.ShouldContain(nameof(IArtifactStorageDriverFactory));
    }

    [Fact]
    public void Local_rwx_descriptor_is_discoverable_without_replacing_the_current_blob_backend()
    {
        var module = new LocalRwxStorageProviderModule();
        var catalog = new StorageProviderModuleCatalog([module]);

        module.TypeKey.ShouldBe("local-rwx/v1");
        module.FactoryType.ShouldBe(typeof(LocalRwxArtifactStorageDriverFactory));
        (module.Capabilities & StorageProviderCapabilities.StreamingWrite).ShouldBe(StorageProviderCapabilities.StreamingWrite);
        (module.Capabilities & StorageProviderCapabilities.StreamingRead).ShouldBe(StorageProviderCapabilities.StreamingRead);
        (module.Capabilities & StorageProviderCapabilities.ConditionalCreate).ShouldBe(StorageProviderCapabilities.ConditionalCreate);
        module.ConfigSchema.GetProperty("properties").TryGetProperty("rootPath", out _).ShouldBeTrue();
        module.SecretSchema.GetProperty("properties").EnumerateObject().ShouldBeEmpty();
        catalog.Require(module.TypeKey).ShouldBeSameAs(module);
    }

    [Fact]
    public void Every_production_storage_module_is_parameterless_discoverable_and_catalog_valid()
    {
        var modules = typeof(IStorageProviderModule).Assembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract && typeof(IStorageProviderModule).IsAssignableFrom(t))
            .Select(t => (IStorageProviderModule)Activator.CreateInstance(t)!)
            .ToList();

        var catalog = new StorageProviderModuleCatalog(modules);

        catalog.Modules.Select(m => m.TypeKey).ShouldContain("local-rwx/v1");
        catalog.Modules.Select(m => m.TypeKey).Distinct(StringComparer.Ordinal).Count().ShouldBe(catalog.Modules.Count);
    }

    private static TestModule Module(string typeKey, string? displayName = null) => new()
    {
        TypeKey = typeKey,
        DisplayName = displayName ?? typeKey,
        ConfigSchema = EmptySchema(),
        SecretSchema = EmptySchema(),
        Capabilities = StorageProviderCapabilities.None,
        FactoryType = typeof(TestFactory)
    };

    private static JsonElement EmptySchema() => JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new { },
        additionalProperties = false
    });

    private sealed class TestModule : IStorageProviderModule
    {
        public required string TypeKey { get; init; }
        public required string DisplayName { get; init; }
        public required JsonElement ConfigSchema { get; init; }
        public required JsonElement SecretSchema { get; init; }
        public required StorageProviderCapabilities Capabilities { get; init; }
        public required Type FactoryType { get; set; }
    }

    private sealed class TestFactory : IArtifactStorageDriverFactory
    {
        public string ProviderTypeKey => "test/v1";
        public ValueTask<IArtifactStorageDriver> CreateAsync(ArtifactStorageDriverCreateRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
