using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Messages.Enums;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// Pins the credential-rooted model row's DEFAULT contract independent of the DB: a freshly constructed model is
/// ENABLED, MANUAL-sourced, and declares NO capabilities. These defaults are behaviour — "enabled by default" is
/// why a just-added model is immediately usable, and "all capabilities false" is the safe floor the scheduler
/// reads — so they are pinned here, not left implicit.
/// </summary>
[Trait("Category", "Unit")]
public class ModelCredentialModelTests
{
    [Fact]
    public void A_new_model_defaults_to_enabled_manual_and_no_capabilities()
    {
        var model = new ModelCredentialModel { ModelId = "claude-sonnet-4-5" };

        model.Enabled.ShouldBeTrue("a freshly added model is usable by default");
        model.Source.ShouldBe(ModelSource.Manual, "the default authoring mode is operator-typed");

        model.SupportsStructuredOutput.ShouldBeFalse();
        model.SupportsToolUse.ShouldBeFalse();
        model.RecommendedForSupervisor.ShouldBeFalse("capabilities default to a safe declares-nothing floor");

        model.DisplayName.ShouldBeNull("null DisplayName falls back to the wire ModelId");
    }
}
