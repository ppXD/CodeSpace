using Autofac;
using CodeSpace.Core.Services.Sessions.Journal;
using CodeSpace.Core.Services.Sessions.Journal.Describers;
using CodeSpace.IntegrationTests.Infrastructure;
using Shouldly;

namespace CodeSpace.IntegrationTests.Sessions;

/// <summary>
/// Verifies the journal describer seam is wired through the REAL CodeSpaceModule container — what a unit test (which
/// news up the registry directly) can't prove: the <c>ISingletonDependency</c> marker scan registers EVERY
/// <see cref="IJournalStepDescriber"/> into the injected <c>IEnumerable</c> (so a new describer dropped into Describers/
/// auto-registers with no wiring edit — the drop-a-folder guarantee), the mandatory <see cref="IJournalFallbackDescriber"/>
/// resolves as its OWN dependency (and is NOT in the claimant list, or it could shadow a specific describer), and the
/// registry resolves injected with both. If the scan ever stops picking a describer up, this fails instead of silently
/// dropping that source's steps.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class JournalDescriberRegistrationTests
{
    private readonly PostgresFixture _fixture;

    public JournalDescriberRegistrationTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public void Every_step_describer_is_registered_and_the_registry_resolves_with_the_fallback()
    {
        using var scope = _fixture.BeginScope();

        var describers = scope.Resolve<IEnumerable<IJournalStepDescriber>>().ToList();

        describers.ShouldContain(d => d is SupervisorStepDescriber);
        describers.ShouldContain(d => d is ToolStepDescriber);
        describers.ShouldContain(d => d is AgentEventStepDescriber);
        describers.ShouldContain(d => d is LifecycleStepDescriber);
        describers.ShouldNotContain(d => d is IJournalFallbackDescriber, "the fallback is NOT a specific describer — it must not sit in the claimant list, or it would shadow a real describer");

        scope.Resolve<IJournalFallbackDescriber>().ShouldBeOfType<FallbackStepDescriber>("the mandatory fallback resolves as its own dependency");
        scope.Resolve<IJournalStepDescriberRegistry>().ShouldBeOfType<JournalStepDescriberRegistry>("the registry resolves, injected with the describers + the fallback");
    }
}
