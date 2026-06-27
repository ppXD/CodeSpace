using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.ModelCredentials;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Core.Services.Supervisor.Deciders;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// P1 — the capability catalog the supervisor brain authors against: every registered harness + the model providers it
/// can drive, and the run's credentialed pool models + each model's provider. Pins that the catalog is rendered
/// faithfully and reaches the user prompt — so the model can pick a provider-compatible (harness, model) pair on
/// purpose instead of blind.
/// </summary>
[Trait("Category", "Unit")]
public class SupervisorCapabilityCatalogTests
{
    private static readonly IReadOnlyList<IAgentHarness> Harnesses = new IAgentHarness[]
    {
        new FakeHarness("codex-cli", "OpenAI", "OpenRouter", "Ollama", "Custom"),
        new FakeHarness("claude-code", "Anthropic", "Custom"),
    };

    [Fact]
    public void Catalog_lists_each_harness_with_its_drivable_providers_and_each_pool_model_with_its_provider()
    {
        var pool = new[] { new PoolModelInfo("metis-coder-max", "Anthropic"), new PoolModelInfo("gpt-4o", "OpenAI") };

        var catalog = LlmSupervisorDecider.RenderCatalog(Harnesses, pool);

        catalog.ShouldContain("codex-cli — drives: OpenAI, OpenRouter, Ollama, Custom");
        catalog.ShouldContain("claude-code — drives: Anthropic, Custom");
        catalog.ShouldContain("metis-coder-max — Anthropic");
        catalog.ShouldContain("gpt-4o — OpenAI");
        catalog.ShouldContain("harness whose providers include that model's provider", Case.Insensitive, "the catalog states the compatibility constraint");
    }

    [Fact]
    public void Catalog_appends_the_capability_tier_when_known_and_guides_allocation()
    {
        var pool = new[]
        {
            new PoolModelInfo("claude-opus-4-8", "Anthropic", ModelCapabilityTier.Frontier),
            new PoolModelInfo("metis-coder-max", "Anthropic", ModelCapabilityTier.Unknown),   // opaque/un-tiered → no suffix
        };

        var catalog = LlmSupervisorDecider.RenderCatalog(Harnesses, pool);

        catalog.ShouldContain("claude-opus-4-8 — Anthropic — tier: frontier", Case.Sensitive, "a known tier is surfaced so the brain allocates capability-aware");
        catalog.ShouldContain("metis-coder-max — Anthropic\n", Case.Sensitive, "an Unknown tier renders no suffix");
        catalog.ShouldNotContain("metis-coder-max — Anthropic — tier", Case.Insensitive);
        catalog.ShouldContain("higher-tier model", Case.Insensitive, "the allocation guidance line appears when any tier is known");
    }

    [Fact]
    public void Catalog_with_an_all_untiered_pool_is_byte_identical_to_the_pre_tier_render()
    {
        var untiered = new[] { new PoolModelInfo("metis-coder-max", "Anthropic"), new PoolModelInfo("gpt-4o", "OpenAI") };
        var explicitUnknown = new[] { new PoolModelInfo("metis-coder-max", "Anthropic", ModelCapabilityTier.Unknown), new PoolModelInfo("gpt-4o", "OpenAI", ModelCapabilityTier.Unknown) };

        var render = LlmSupervisorDecider.RenderCatalog(Harnesses, untiered);

        render.ShouldBe(LlmSupervisorDecider.RenderCatalog(Harnesses, explicitUnknown));
        render.ShouldNotContain("tier:", Case.Insensitive, "no tier suffix");
        render.ShouldNotContain("higher-tier", Case.Insensitive, "and no allocation-guidance line — an un-tiered pool renders exactly as before this signal existed");
    }

    [Fact]
    public void Catalog_notes_an_empty_pool_so_the_model_falls_back_to_run_defaults()
    {
        var catalog = LlmSupervisorDecider.RenderCatalog(Harnesses, Array.Empty<PoolModelInfo>());

        catalog.ShouldContain("No credentialed models are listed");
        catalog.ShouldContain("codex-cli — drives:", Case.Sensitive, "harnesses are still listed even with no pool models");
    }

    [Fact]
    public void A_keyless_harness_is_listed_as_needing_no_model_key()
    {
        var catalog = LlmSupervisorDecider.RenderCatalog(new IAgentHarness[] { new KeylessHarness("local-stub") }, Array.Empty<PoolModelInfo>());

        catalog.ShouldContain("local-stub — drives: (needs no model key)");
    }

    [Fact]
    public void The_user_prompt_carries_the_catalog()
    {
        var context = new SupervisorTurnContext { Goal = "ship the feature", TurnNumber = 0 };
        var catalog = LlmSupervisorDecider.RenderCatalog(Harnesses, new[] { new PoolModelInfo("metis-coder-max", "Anthropic") });

        var prompt = LlmSupervisorDecider.BuildUserPromptForTest(context, catalog);

        prompt.ShouldContain("ship the feature");
        prompt.ShouldContain("metis-coder-max — Anthropic", Case.Sensitive, "the brain sees the pool in its prompt");
        prompt.ShouldContain("claude-code — drives: Anthropic, Custom", Case.Sensitive, "the brain sees the harness↔provider map in its prompt");
    }

    [Fact]
    public void An_empty_catalog_leaves_the_prompt_unchanged_byte_for_byte()
    {
        var context = new SupervisorTurnContext { Goal = "g", TurnNumber = 0 };

        LlmSupervisorDecider.BuildUserPromptForTest(context, "").ShouldBe(LlmSupervisorDecider.BuildUserPromptForTest(context),
            "no catalog → the prior framing is byte-identical (existing decider tests stay green)");
    }

    [Fact]
    public void Catalog_lists_each_team_persona_by_slug_name_and_description_sorted_by_slug()
    {
        var personas = new[]
        {
            new PersonaCatalogInfo("security-reviewer", "Security Reviewer", "Audits for vulnerabilities"),
            new PersonaCatalogInfo("backend-impl", "Backend Implementer", null),
        };

        var catalog = CapabilityCatalog.Render(Harnesses, Array.Empty<PoolModelInfo>(), personas);

        catalog.ShouldContain("backend-impl — Backend Implementer", Case.Sensitive, "a persona with no description renders slug — name");
        catalog.ShouldContain("security-reviewer — Security Reviewer — Audits for vulnerabilities", Case.Sensitive, "a persona with a description renders slug — name — description");
        catalog.IndexOf("backend-impl", StringComparison.Ordinal).ShouldBeLessThan(catalog.IndexOf("security-reviewer", StringComparison.Ordinal), "personas render in a stable slug order");
        catalog.ShouldContain("Author a per-agent persona by its SLUG", Case.Insensitive, "the catalog tells the brain how to use a persona");
    }

    [Fact]
    public void No_personas_omit_the_persona_section_byte_for_byte_the_planner_path()
    {
        var withNull = CapabilityCatalog.Render(Harnesses, Array.Empty<PoolModelInfo>());
        var withEmpty = CapabilityCatalog.Render(Harnesses, Array.Empty<PoolModelInfo>(), Array.Empty<PersonaCatalogInfo>());

        withNull.ShouldBe(withEmpty, "an absent/empty persona list renders identically — the planner's two-arg call is byte-identical");
        withNull.ShouldNotContain("persona", Case.Insensitive, "no persona section is emitted when the team has no personas");
    }

    private sealed class FakeHarness : IAgentHarness, IModelCredentialProjector
    {
        public FakeHarness(string kind, params string[] providers) { Kind = kind; SupportedProviders = providers; }

        public string Kind { get; }
        public string Version => "test";
        public IReadOnlyList<string> Models => Array.Empty<string>();
        public IReadOnlyList<string> SupportedProviders { get; }

        public SandboxSpec BuildInvocation(AgentTask task) => throw new NotSupportedException();
        public IReadOnlyList<AgentEvent> ParseEvents(string rawLine) => throw new NotSupportedException();
        public AgentRunResult BuildResult(IReadOnlyList<AgentEvent> events, int exitCode) => throw new NotSupportedException();
        public IReadOnlyDictionary<string, string> ProjectToEnv(ResolvedModelCredential credential) => throw new NotSupportedException();
    }

    private sealed class KeylessHarness : IAgentHarness
    {
        public KeylessHarness(string kind) { Kind = kind; }

        public string Kind { get; }
        public string Version => "test";
        public IReadOnlyList<string> Models => Array.Empty<string>();

        public SandboxSpec BuildInvocation(AgentTask task) => throw new NotSupportedException();
        public IReadOnlyList<AgentEvent> ParseEvents(string rawLine) => throw new NotSupportedException();
        public AgentRunResult BuildResult(IReadOnlyList<AgentEvent> events, int exitCode) => throw new NotSupportedException();
    }
}
