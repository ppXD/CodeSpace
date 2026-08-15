using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Workflows.Artifacts.Routing;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows.Artifacts.Routing;

public sealed class StorageRouteRulesTests
{
    [Theory]
    [InlineData("agent-run-log/v1", "agent-run-log/v1")]
    [InlineData(" Workflow.Run-Event/v27 ", "workflow.run-event/v27")]
    [InlineData("artifact-cas/v0", null)]
    [InlineData("artifact-cas", null)]
    [InlineData("artifact_cas/v1", null)]
    [InlineData("/v1", null)]
    public void Data_class_keys_are_open_versioned_and_canonical(string value, string? expected)
    {
        if (expected == null)
        {
            Should.Throw<ArgumentException>(() => StorageRouteRules.NormalizeDataClassTypeKey(value));
            return;
        }

        StorageRouteRules.NormalizeDataClassTypeKey(value).ShouldBe(expected);
    }

    [Fact]
    public void Lifecycle_is_managed_and_retired_is_terminal()
    {
        StorageRouteRules.EnsureTransition(StorageRouteState.Draft, StorageRouteState.Active);
        StorageRouteRules.EnsureTransition(StorageRouteState.Active, StorageRouteState.Disabled);
        StorageRouteRules.EnsureTransition(StorageRouteState.Disabled, StorageRouteState.Active);
        StorageRouteRules.EnsureTransition(StorageRouteState.Active, StorageRouteState.Retired);

        Should.Throw<ArgumentException>(() => StorageRouteRules.EnsureTransition(StorageRouteState.Active, StorageRouteState.Draft)).Message.ShouldContain("Draft");
        Should.Throw<ArgumentException>(() => StorageRouteRules.EnsureTransition(StorageRouteState.Retired, StorageRouteState.Active)).Message.ShouldContain("terminal");
        Should.Throw<ArgumentException>(() => StorageRouteRules.EnsureRevisionAllowed(StorageRouteState.Retired)).Message.ShouldContain("terminal");
    }

    [Fact]
    public void Profile_revision_selection_is_exact_and_fail_closed()
    {
        StorageRouteRules.EnsureProfileSelection(StorageProfileRevisionMode.CurrentAtWrite, null);
        StorageRouteRules.EnsureProfileSelection(StorageProfileRevisionMode.Pinned, 7);

        Should.Throw<ArgumentException>(() => StorageRouteRules.EnsureProfileSelection(StorageProfileRevisionMode.CurrentAtWrite, 1));
        Should.Throw<ArgumentException>(() => StorageRouteRules.EnsureProfileSelection(StorageProfileRevisionMode.Pinned, null));
        Should.Throw<ArgumentException>(() => StorageRouteRules.EnsureProfileSelection(StorageProfileRevisionMode.Pinned, 0));
        Should.Throw<ArgumentException>(() => StorageRouteRules.EnsureProfileSelection((StorageProfileRevisionMode)99, null));
    }
}
