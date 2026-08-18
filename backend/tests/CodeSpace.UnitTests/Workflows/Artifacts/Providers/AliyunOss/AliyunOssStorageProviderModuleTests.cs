using System.Text.Json;
using Autofac;
using CodeSpace.Core;
using CodeSpace.Core.Services.Workflows.Artifacts.Profiles;
using CodeSpace.Core.Services.Workflows.Artifacts.Providers;
using CodeSpace.Core.Services.Workflows.Artifacts.Providers.AliyunOss;
using Microsoft.Extensions.Configuration;
using Serilog;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows.Artifacts.Providers.AliyunOss;

/// <summary>
/// Admission tests for the first non-local storage provider: its descriptor, the schemas a Settings form renders,
/// and the startup guarantee that the module resolves to exactly one factory.
/// </summary>
[Trait("Category", "Unit")]
public sealed class AliyunOssStorageProviderModuleTests
{
    [Fact]
    public void The_descriptor_declares_an_open_versioned_key_and_only_the_capabilities_the_driver_implements()
    {
        var module = new AliyunOssStorageProviderModule();

        module.TypeKey.ShouldBe("aliyun-oss/v1");
        module.FactoryType.ShouldBe(typeof(AliyunOssArtifactStorageDriverFactory));
        module.Capabilities.ShouldBe(StorageProviderCapabilities.StreamingWrite | StorageProviderCapabilities.StreamingRead
            | StorageProviderCapabilities.RangeRead | StorageProviderCapabilities.ConditionalCreate
            | StorageProviderCapabilities.Delete | StorageProviderCapabilities.HealthProbe);
        (module.Capabilities & StorageProviderCapabilities.ObjectVersioning).ShouldBe(StorageProviderCapabilities.None, "bucket versioning is an operator setting this driver cannot promise");
        (module.Capabilities & StorageProviderCapabilities.MultipartUpload).ShouldBe(StorageProviderCapabilities.None, "the driver publishes with a single streamed upload plus a server-side copy");
        new StorageProviderModuleCatalog([module]).Require(module.TypeKey).ShouldBeSameAs(module);
    }

    [Fact]
    public void The_config_schema_asks_for_the_endpoint_region_and_bucket_the_v4_signature_needs()
    {
        var properties = new AliyunOssStorageProviderModule().ConfigSchema.GetProperty("properties");

        properties.EnumerateObject().Select(property => property.Name).OrderBy(name => name, StringComparer.Ordinal)
            .ShouldBe(["bucket", "endpoint", "keyPrefix", "region"]);
        new AliyunOssStorageProviderModule().ConfigSchema.GetProperty("required").EnumerateArray().Select(value => value.GetString())
            .ShouldBe(["endpoint", "region", "bucket"], "region is not optional: it is scoped into every OSS4-HMAC-SHA256 signing key");
    }

    [Fact]
    public void The_secret_schema_declares_the_sts_token_as_the_only_optional_input()
    {
        var schema = new AliyunOssStorageProviderModule().SecretSchema;

        schema.GetProperty("properties").EnumerateObject().Select(property => property.Name).OrderBy(name => name, StringComparer.Ordinal)
            .ShouldBe(["accessKeyId", "accessKeySecret", "securityToken"]);
        schema.GetProperty("required").EnumerateArray().Select(value => value.GetString()).ShouldBe(["accessKeyId", "accessKeySecret"]);
        schema.GetProperty("additionalProperties").GetBoolean().ShouldBeFalse();
    }

    [Theory]
    [InlineData("""{"region":"cn-hangzhou","bucket":"codespace-artifacts"}""")]
    [InlineData("""{"endpoint":"oss-cn-hangzhou.aliyuncs.com","bucket":"codespace-artifacts"}""")]
    [InlineData("""{"endpoint":"oss-cn-hangzhou.aliyuncs.com","region":"cn-hangzhou"}""")]
    [InlineData("""{"endpoint":"http://oss-cn-hangzhou.aliyuncs.com","region":"cn-hangzhou","bucket":"codespace-artifacts"}""")]
    [InlineData("""{"endpoint":"oss-cn-hangzhou.aliyuncs.com","region":"cn-hangzhou","bucket":"UPPER"}""")]
    [InlineData("""{"endpoint":"oss-cn-hangzhou.aliyuncs.com","region":"cn-hangzhou","bucket":"codespace-artifacts","surprise":true}""")]
    public void The_control_plane_rejects_a_configuration_the_driver_could_not_address(string configJson)
    {
        var module = new AliyunOssStorageProviderModule();
        using var config = JsonDocument.Parse(configJson);

        Should.Throw<ArgumentException>(() => StorageProfileRules.ValidateConfig(config.RootElement, module.ConfigSchema, module.SecretSchema));
    }

    [Fact]
    public void A_configuration_that_smuggles_a_secret_is_rejected_without_repeating_the_value()
    {
        var module = new AliyunOssStorageProviderModule();
        using var config = JsonDocument.Parse("""{"endpoint":"oss-cn-hangzhou.aliyuncs.com","region":"cn-hangzhou","bucket":"codespace-artifacts","accessKeySecret":"super-secret-value"}""");

        var error = Should.Throw<ArgumentException>(() => StorageProfileRules.ValidateConfig(config.RootElement, module.ConfigSchema, module.SecretSchema));

        error.Message.ShouldContain("accessKeySecret");
        error.Message.ShouldNotContain("super-secret-value");
    }

    [Fact]
    public void A_complete_configuration_is_admitted_and_its_namespace_identity_covers_the_key_prefix()
    {
        IStorageProviderModule module = new AliyunOssStorageProviderModule();
        using var config = JsonDocument.Parse("""{"endpoint":"oss-cn-hangzhou.aliyuncs.com","region":"cn-hangzhou","bucket":"codespace-artifacts","keyPrefix":"team-7/"}""");
        using var other = JsonDocument.Parse("""{"endpoint":"oss-cn-hangzhou.aliyuncs.com","region":"cn-hangzhou","bucket":"codespace-artifacts","keyPrefix":"team-9/"}""");

        StorageProfileRules.ValidateConfig(config.RootElement, module.ConfigSchema, module.SecretSchema);

        StorageProfileRules.NamespaceFingerprint(module.TypeKey, module.GetNamespaceConfiguration(config.RootElement))
            .ShouldNotBe(StorageProfileRules.NamespaceFingerprint(module.TypeKey, module.GetNamespaceConfiguration(other.RootElement)));
    }

    [Fact]
    public void Startup_resolves_the_module_to_exactly_one_registered_factory()
    {
        using var container = Container();

        var factories = container.Resolve<IEnumerable<IArtifactStorageDriverFactory>>().Where(factory => factory.ProviderTypeKey == AliyunOssArtifactStorageDriverFactory.TypeKey).ToList();

        factories.Count.ShouldBe(1);
        container.Resolve<IArtifactStorageDriverFactoryCatalog>().Require(AliyunOssArtifactStorageDriverFactory.TypeKey).ShouldBeSameAs(factories[0]);
        container.Resolve<IStorageProviderModuleCatalog>().Require(AliyunOssArtifactStorageDriverFactory.TypeKey).ShouldBeOfType<AliyunOssStorageProviderModule>();
    }

    [Fact]
    public void Registering_the_module_leaves_the_local_provider_exactly_where_it_was()
    {
        using var container = Container();

        var keys = container.Resolve<IStorageProviderModuleCatalog>().Modules.Select(module => module.TypeKey).ToList();

        keys.Count(key => key == "local-rwx/v1").ShouldBe(1, "adding a provider must not displace the installed one");
        keys.ShouldContain("aliyun-oss/v1");
        keys.SequenceEqual(keys.OrderBy(key => key, StringComparer.Ordinal)).ShouldBeTrue("Settings discovery stays deterministic regardless of DI contribution order");
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
}
