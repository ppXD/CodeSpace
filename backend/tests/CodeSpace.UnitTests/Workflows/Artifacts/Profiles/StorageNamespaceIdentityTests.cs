using System.Text.Json;
using Autofac;
using CodeSpace.Core;
using CodeSpace.Core.Services.Workflows.Artifacts.Profiles;
using CodeSpace.Core.Services.Workflows.Artifacts.Providers;
using CodeSpace.Core.Services.Workflows.Artifacts.Providers.AliyunOss;
using CodeSpace.Core.Services.Workflows.Artifacts.Providers.Local;
using Microsoft.Extensions.Configuration;
using Serilog;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows.Artifacts.Profiles;

/// <summary>
/// How the object a stored placement reaches is identified when something has to COMPARE two of them — the retention
/// reaper deciding whether one object key, under two teams' profile revisions, is one physical object.
///
/// <para>The rule under test is the direction of every failure. An identity that cannot be derived is not "different
/// from everything": it is unknown, and the caller must read unknown as "possibly the same", because the only cost of
/// that reading is a kept byte and the cost of the other is another team's deleted one.</para>
/// </summary>
[Trait("Category", "Unit")]
public sealed class StorageNamespaceIdentityTests
{
    private static readonly IStorageProviderModuleCatalog Catalog = new StorageProviderModuleCatalog([new LocalRwxStorageProviderModule(), new AliyunOssStorageProviderModule(), new ThrowingProjectionModule(), new ScalarProjectionModule()]);

    private const string Key = "workflow-artifacts/ab/cd/abcd0000000000000000000000000000000000000000000000000000000000ff";

    [Fact]
    public void A_filesystem_revision_derives_the_physical_object_its_key_reaches()
    {
        using var config = JsonDocument.Parse("""{"rootPath":"/srv/codespace/artifacts/"}""");

        StorageProfileRules.TryObjectIdentity(Catalog, "local-rwx/v1", config.RootElement.GetRawText(), Key)
            .ShouldBe(new LocalRwxStorageProviderModule().PhysicalObject(config.RootElement, Key));
    }

    [Fact]
    public void A_revision_whose_provider_cannot_name_the_object_is_identified_by_its_namespace_and_the_key()
    {
        // An object store's key IS the object once the bucket and prefix are known, so the namespace admission
        // fingerprints and the key together name it.
        const string config = """{"endpoint":"oss-cn-hangzhou.aliyuncs.com","bucket":"codespace-artifacts","keyPrefix":"team-7/"}""";
        using var document = JsonDocument.Parse(config);

        StorageProfileRules.TryObjectIdentity(Catalog, "aliyun-oss/v1", config, Key)
            .ShouldBe($"{StorageProfileRules.NamespaceFingerprint(new AliyunOssStorageProviderModule(), document.RootElement)}/{Key}");
    }

    [Fact]
    public void Two_revisions_that_spell_one_root_differently_derive_one_identity() =>
        StorageProfileRules.TryObjectIdentity(Catalog, "local-rwx/v1", """{"rootPath":"/srv/codespace/artifacts/."}""", Key)
            .ShouldBe(StorageProfileRules.TryObjectIdentity(Catalog, "local-rwx/v1", """{"rootPath":"/srv/codespace/artifacts"}""", Key));

    [Theory]
    [InlineData("retired-provider/v1", """{"rootPath":"/srv/codespace/artifacts"}""", Key)]
    [InlineData("local-rwx/v1", """{"rootPath":""", Key)]
    [InlineData("local-rwx/v1", """{"unexpected":"/srv/codespace/artifacts"}""", Key)]
    [InlineData("local-rwx/v1", """{"rootPath":42}""", Key)]
    [InlineData("local-rwx/v1", """{"rootPath":"/srv/codespace/artifacts"}""", "workflow-artifacts/../../escape")]
    [InlineData("aliyun-oss/v1", """{"endpoint":"oss-accelerate.aliyuncs.com","bucket":"codespace-artifacts"}""", Key)]
    [InlineData("throwing-projection/v1", """{}""", Key)]
    [InlineData("scalar-projection/v1", """{}""", Key)]
    [InlineData("", """{}""", Key)]
    public void A_revision_whose_identity_cannot_be_derived_has_none_rather_than_an_invented_one(string providerTypeKey, string config, string objectKey) =>
        StorageProfileRules.TryObjectIdentity(Catalog, providerTypeKey, config, objectKey).ShouldBeNull();

    [Fact]
    public void Every_shipped_storage_module_owns_its_namespace_projection()
    {
        // The interface's default projection is the raw configuration, which is exactly the comparison that let two
        // spellings of one root read as two namespaces. A provider that ships without its own projection inherits that
        // defect, so adding one is required to be a decision rather than an omission.
        using var container = Container();

        var inheriting = container.Resolve<IStorageProviderModuleCatalog>().Modules
            .Where(module => ProjectionOwner(module) == typeof(IStorageProviderModule))
            .Select(module => module.TypeKey)
            .ToList();

        inheriting.ShouldBeEmpty("these modules compare namespaces by the text an operator typed, not the place it reaches");
    }

    private static Type ProjectionOwner(IStorageProviderModule module)
    {
        var map = module.GetType().GetInterfaceMap(typeof(IStorageProviderModule));
        var slot = Array.FindIndex(map.InterfaceMethods, method => method.Name == nameof(IStorageProviderModule.GetNamespaceConfiguration));

        return map.TargetMethods[slot].DeclaringType!;
    }

    private static IContainer Container()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ASPNETCORE_ENVIRONMENT"] = "Development",
            ["CodeSpaceStore:ConnectionString"] = "Host=unused;Database=unused"
        }).Build();
        var builder = new ContainerBuilder();
        builder.RegisterModule(new CodeSpaceModule(new LoggerConfiguration().CreateLogger(), configuration));
        return builder.Build();
    }

    /// <summary>A provider whose projection fails for a reason no caller can name in advance.</summary>
    private sealed class ThrowingProjectionModule : TestModule
    {
        public override string TypeKey => "throwing-projection/v1";
        public override JsonElement GetNamespaceConfiguration(JsonElement nonSecretConfiguration) => throw new IOException("the namespace could not be inspected");
    }

    /// <summary>A provider whose projection breaks the contract's shape: an identity must be an object.</summary>
    private sealed class ScalarProjectionModule : TestModule
    {
        public override string TypeKey => "scalar-projection/v1";
        public override JsonElement GetNamespaceConfiguration(JsonElement nonSecretConfiguration) => JsonDocument.Parse("\"flat\"").RootElement.Clone();
    }

    private abstract class TestModule : IStorageProviderModule
    {
        private static readonly JsonElement Schema = JsonDocument.Parse("""{"type":"object"}""").RootElement.Clone();

        public abstract string TypeKey { get; }
        public string DisplayName => TypeKey;
        public JsonElement ConfigSchema => Schema;
        public JsonElement SecretSchema => Schema;
        public StorageProviderCapabilities Capabilities => StorageProviderCapabilities.StreamingRead;
        public Type FactoryType => typeof(LocalRwxArtifactStorageDriverFactory);
        public abstract JsonElement GetNamespaceConfiguration(JsonElement nonSecretConfiguration);
    }
}
