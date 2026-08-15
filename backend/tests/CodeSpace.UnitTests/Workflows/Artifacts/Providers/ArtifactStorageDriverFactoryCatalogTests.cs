using Autofac;
using Autofac.Core;
using CodeSpace.Core;
using CodeSpace.Core.Services.Workflows.Artifacts.Providers;
using CodeSpace.Core.Services.Workflows.Artifacts.Providers.Local;
using Microsoft.Extensions.Configuration;
using Serilog;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows.Artifacts.Providers;

[Trait("Category", "Unit")]
public sealed class ArtifactStorageDriverFactoryCatalogTests
{
    [Fact]
    public void Get_and_require_resolve_only_the_exact_provider_type_key()
    {
        var first = new ConfigurableFactory("acme-object/v1");
        var second = new SecondFactory("other-store/v3");
        var catalog = Catalog([second, first], Module("acme-object/v1", typeof(ConfigurableFactory)), Module("other-store/v3", typeof(SecondFactory)));

        catalog.Get("acme-object/v1").ShouldBeSameAs(first);
        catalog.Require("other-store/v3").ShouldBeSameAs(second);
        catalog.Get("Acme-object/v1").ShouldBeNull();
        typeof(IArtifactStorageDriverFactoryCatalog).GetProperties().ShouldBeEmpty();
        typeof(IArtifactStorageDriverFactoryCatalog).GetMethods().Select(method => method.Name).OrderBy(name => name, StringComparer.Ordinal).ShouldBe(["Get", "Require"]);
    }

    [Fact]
    public void Require_rejects_an_unknown_provider_type_key_with_sorted_available_keys()
    {
        var catalog = Catalog(
            [new SecondFactory("zeta-store/v1"), new ConfigurableFactory("alpha-store/v2")],
            Module("zeta-store/v1", typeof(SecondFactory)), Module("alpha-store/v2", typeof(ConfigurableFactory)));

        var error = Should.Throw<NotSupportedException>(() => catalog.Require("future-store/v7"));

        error.Message.ShouldContain("future-store/v7");
        error.Message.ShouldContain("alpha-store/v2, zeta-store/v1");
    }

    [Fact]
    public void Constructor_rejects_duplicate_factory_keys()
    {
        var first = new ConfigurableFactory("acme-object/v1");
        var second = new SecondFactory("acme-object/v1");

        var error = Should.Throw<InvalidOperationException>(() => Catalog([second, first], Module("acme-object/v1", typeof(ConfigurableFactory))));

        error.Message.ShouldContain("acme-object/v1");
        error.Message.ShouldContain(typeof(ConfigurableFactory).FullName!);
        error.Message.ShouldContain(typeof(SecondFactory).FullName!);
    }

    [Fact]
    public void Constructor_rejects_a_blank_factory_key()
    {
        var error = Should.Throw<InvalidOperationException>(() => Catalog([new ConfigurableFactory(" ")]));

        error.Message.ShouldContain(nameof(IArtifactStorageDriverFactory.ProviderTypeKey));
        error.Message.ShouldContain(typeof(ConfigurableFactory).FullName!);
    }

    [Fact]
    public void Constructor_rejects_a_module_without_its_declared_factory()
    {
        var error = Should.Throw<InvalidOperationException>(() => Catalog([], Module("acme-object/v1", typeof(ConfigurableFactory))));

        error.Message.ShouldContain("acme-object/v1");
        error.Message.ShouldContain(typeof(ConfigurableFactory).FullName!);
        error.Message.ShouldContain("not registered");
    }

    [Fact]
    public void Constructor_rejects_an_orphan_factory_without_a_module()
    {
        var expected = new ConfigurableFactory("acme-object/v1");
        var orphan = new SecondFactory("orphan-store/v4");

        var error = Should.Throw<InvalidOperationException>(() => Catalog([orphan, expected], Module("acme-object/v1", typeof(ConfigurableFactory))));

        error.Message.ShouldContain("orphan-store/v4");
        error.Message.ShouldContain(typeof(SecondFactory).FullName!);
        error.Message.ShouldContain("no installed storage provider module");
    }

    [Fact]
    public void Constructor_rejects_the_wrong_concrete_factory_type_for_a_module_key()
    {
        var error = Should.Throw<InvalidOperationException>(() => Catalog([new SecondFactory("acme-object/v1")], Module("acme-object/v1", typeof(ConfigurableFactory))));

        error.Message.ShouldContain("acme-object/v1");
        error.Message.ShouldContain(typeof(ConfigurableFactory).FullName!);
        error.Message.ShouldContain(typeof(SecondFactory).FullName!);
    }

    [Fact]
    public void Constructor_rejects_the_declared_factory_when_its_key_mismatches_the_module()
    {
        var error = Should.Throw<InvalidOperationException>(() => Catalog([new ConfigurableFactory("acme-object/v2")], Module("acme-object/v1", typeof(ConfigurableFactory))));

        error.Message.ShouldContain(nameof(IArtifactStorageDriverFactory.ProviderTypeKey));
        error.Message.ShouldContain("acme-object/v1");
        error.Message.ShouldContain("acme-object/v2");
    }

    [Fact]
    public void Code_space_module_composes_first_party_and_external_di_contributions()
    {
        var externalModule = Module("external-store/v2", typeof(ExternalFactory));
        var externalFactory = new ExternalFactory("external-store/v2");
        var builder = Builder();
        builder.RegisterInstance(externalModule).As<IStorageProviderModule>();
        builder.RegisterInstance(externalFactory).As<IArtifactStorageDriverFactory>();

        using var container = builder.Build();
        var catalog = container.Resolve<IArtifactStorageDriverFactoryCatalog>();

        catalog.Require(LocalRwxArtifactStorageDriverFactory.TypeKey).ShouldBeOfType<LocalRwxArtifactStorageDriverFactory>();
        catalog.Require(externalModule.TypeKey).ShouldBeSameAs(externalFactory);
    }

    [Fact]
    public void Code_space_module_auto_activates_factory_validation_during_container_build()
    {
        var builder = Builder();
        builder.RegisterInstance(Module("external-store/v2", typeof(ExternalFactory))).As<IStorageProviderModule>();

        var error = Should.Throw<DependencyResolutionException>(() => builder.Build());

        error.ToString().ShouldContain("external-store/v2");
        error.ToString().ShouldContain(typeof(ExternalFactory).FullName!);
    }

    private static ArtifactStorageDriverFactoryCatalog Catalog(IEnumerable<IArtifactStorageDriverFactory> factories, params IStorageProviderModule[] modules) =>
        new(factories, new StorageProviderModuleCatalog(modules));

    private static TestModule Module(string typeKey, Type factoryType) => new()
    {
        TypeKey = typeKey,
        DisplayName = typeKey,
        ConfigSchema = JsonSchema.Empty,
        SecretSchema = JsonSchema.Empty,
        Capabilities = StorageProviderCapabilities.None,
        FactoryType = factoryType
    };

    private static ContainerBuilder Builder()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ASPNETCORE_ENVIRONMENT"] = "Development",
            ["CodeSpaceStore:ConnectionString"] = "Host=unused;Database=unused"
        }).Build();
        var builder = new ContainerBuilder();
        builder.RegisterModule(new CodeSpaceModule(new LoggerConfiguration().CreateLogger(), configuration));
        return builder;
    }

    private sealed class TestModule : IStorageProviderModule
    {
        public required string TypeKey { get; init; }
        public required string DisplayName { get; init; }
        public required System.Text.Json.JsonElement ConfigSchema { get; init; }
        public required System.Text.Json.JsonElement SecretSchema { get; init; }
        public required StorageProviderCapabilities Capabilities { get; init; }
        public required Type FactoryType { get; init; }
    }

    private static class JsonSchema
    {
        public static System.Text.Json.JsonElement Empty { get; } = System.Text.Json.JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new { },
            additionalProperties = false
        });
    }

    private class ConfigurableFactory(string providerTypeKey) : IArtifactStorageDriverFactory
    {
        public string ProviderTypeKey { get; } = providerTypeKey;
        public ValueTask<IArtifactStorageDriver> CreateAsync(ArtifactStorageDriverCreateRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class SecondFactory(string providerTypeKey) : ConfigurableFactory(providerTypeKey);
    private sealed class ExternalFactory(string providerTypeKey) : ConfigurableFactory(providerTypeKey);
}
