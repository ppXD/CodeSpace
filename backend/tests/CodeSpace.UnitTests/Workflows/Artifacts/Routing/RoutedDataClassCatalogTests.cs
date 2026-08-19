using System.Reflection;
using CodeSpace.Core.Services.Workflows.Artifacts.Routing;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows.Artifacts.Routing;

/// <summary>
/// Which data classes an operator may create a route for. The set has to be exactly the classes a runtime consumer in
/// this build reads: a route for any other key lists in Settings as configured storage and is never asked for.
/// </summary>
[Trait("Category", "Unit")]
public sealed class RoutedDataClassCatalogTests
{
    [Fact]
    public void This_build_routes_exactly_the_two_classes_its_consumers_read()
    {
        var catalog = DiscoveredCatalog();

        catalog.DataClasses.Select(dataClass => dataClass.TypeKey).ShouldBe(["agent-run-log/v1", "workflow-artifact/v1"]);
        catalog.DataClasses.Select(dataClass => dataClass.DisplayName).ShouldBe(["Agent run logs", "Workflow artifacts"]);
    }

    /// <summary>
    /// The registration check. A resolver declares the key it reads as a <c>public const string DataClassTypeKey</c>;
    /// if a third one ships without an <see cref="IRoutedDataClass"/> declaration, Settings can never create the route
    /// that consumer needs — and nothing else in the build would notice.
    /// </summary>
    [Fact]
    public void Every_consumer_that_names_a_data_class_key_has_a_declaration_in_the_catalog()
    {
        var catalog = DiscoveredCatalog();
        var declared = ConsumerKeys();

        declared.Count.ShouldBe(2, "this check is vacuous if the reflection stops finding the consumers that read a route");
        foreach (var (consumer, key) in declared)
            catalog.Get(key).ShouldNotBeNull($"{consumer} reads data class '{key}' but no IRoutedDataClass declares it, so no operator can route it");
    }

    [Fact]
    public void Two_declarations_of_one_key_fail_at_construction()
    {
        var duplicate = () => new RoutedDataClassCatalog([new StubDataClass("workflow-artifact/v1", "First"), new StubDataClass("workflow-artifact/v1", "Second")]);

        duplicate.ShouldThrow<InvalidOperationException>().Message.ShouldContain("exactly one declaration");
    }

    [Theory]
    [InlineData("", "Empty")]
    [InlineData("workflow-artifact", "Unversioned")]
    [InlineData("Workflow-Artifact/v1", "Uppercase")]
    [InlineData("workflow-artifact/v0", "Zero version")]
    [InlineData("workflow-artifact/v1", "")]
    public void A_malformed_declaration_fails_at_construction(string typeKey, string displayName)
    {
        var malformed = () => new RoutedDataClassCatalog([new StubDataClass(typeKey, displayName)]);

        malformed.ShouldThrow<InvalidOperationException>();
    }

    [Fact]
    public void An_unknown_key_resolves_to_null_rather_than_a_nearest_match()
    {
        var catalog = DiscoveredCatalog();

        // The plural an operator types. A "closest match" here would silently route a class nobody reads.
        catalog.Get("workflow-artifacts/v1").ShouldBeNull();
    }

    /// <summary>Mirrors the container's own convention: every non-abstract <see cref="IRoutedDataClass"/> in Core.</summary>
    private static RoutedDataClassCatalog DiscoveredCatalog() => new(typeof(RoutedDataClassCatalog).Assembly.GetTypes()
        .Where(type => type.IsClass && !type.IsAbstract && typeof(IRoutedDataClass).IsAssignableFrom(type))
        .Select(type => (IRoutedDataClass)Activator.CreateInstance(type)!));

    private static List<(string Consumer, string Key)> ConsumerKeys() => typeof(RoutedDataClassCatalog).Assembly.GetTypes()
        .Where(type => type.IsClass && !typeof(IRoutedDataClass).IsAssignableFrom(type))
        .Select(type => (Consumer: type.Name, Field: type.GetField("DataClassTypeKey", BindingFlags.Public | BindingFlags.Static)))
        .Where(entry => entry.Field is { IsLiteral: true } field && field.FieldType == typeof(string))
        .Select(entry => (entry.Consumer, Key: (string)entry.Field!.GetRawConstantValue()!))
        .ToList();

    private sealed record StubDataClass(string TypeKey, string DisplayName) : IRoutedDataClass;
}
