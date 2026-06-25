using Autofac;
using CodeSpace.Core.Services.Workflows.Llm;
using CodeSpace.IntegrationTests.Infrastructure;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// The linchpin for "Custom endpoints run all the way to the supervisor": the REAL DI scan must register a
/// <c>"Custom"</c> structured client, so <c>"Custom"</c> is an eligible IN-PROCESS structured provider. That is what
/// makes a Custom-tagged pool model selectable as the supervisor brain (the brain auto-pick's eligible-provider set is
/// <c>_llm.All.OfType&lt;IStructuredLLMClient&gt;().Select(c =&gt; c.Provider)</c>) and resolvable by the decider's
/// provider-match — not just drivable by the agent CLI harness.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class CustomProviderRegistrationFlowTests
{
    private readonly PostgresFixture _fixture;

    public CustomProviderRegistrationFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public void The_DI_registers_a_Custom_structured_client_alongside_OpenAI_and_Anthropic()
    {
        using var scope = _fixture.BeginScope();

        var structuredProviders = scope.Resolve<ILLMClientRegistry>().All.OfType<IStructuredLLMClient>().Select(c => c.Provider).ToList();

        structuredProviders.ShouldContain("Custom", "a Custom OpenAI-compatible gateway is an in-process structured provider — a Custom pool model can run the supervisor brain / planner / effort, not just the agent harness");
        structuredProviders.ShouldContain("OpenAI", "the built-in OpenAI wire is still registered");
        structuredProviders.ShouldContain("Anthropic", "the built-in Anthropic wire is still registered");
    }
}
