using CodeSpace.Core.Services.Agents;
using CodeSpace.Messages.Agents;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// Pure-logic tests for the harness↔model-credential reconciliation matrix (<see cref="HarnessModelReconciler.Reconcile"/>):
/// keep the authored harness when it can drive the credential's provider, else repair to a registered harness that
/// can (the always-runnable fallback), else leave the authored one for the resolver to reject (the honest floor for a
/// genuinely-unrunnable provider). A fake harness pool stands in for the registry — no DB.
/// </summary>
[Trait("Category", "Unit")]
public class HarnessModelReconcilerTests
{
    private static readonly IAgentHarness Codex = new FakeHarness("codex-cli", "OpenAI", "OpenRouter", "Ollama", "Custom");
    private static readonly IAgentHarness Claude = new FakeHarness("claude-code", "Anthropic", "Custom");
    private static readonly IReadOnlyList<IAgentHarness> Pool = new[] { Codex, Claude };
    private const string Default = "codex-cli";   // the registered default harness — wins the multi-driver tie-break

    [Theory]
    [InlineData("codex-cli", "OpenAI", "codex-cli", false)]    // compatible → kept
    [InlineData("claude-code", "Anthropic", "claude-code", false)]
    [InlineData("codex-cli", "Custom", "codex-cli", false)]    // both drive Custom → authored kept, no churn
    [InlineData("codex-cli", "Anthropic", "claude-code", true)] // the bug: codex can't drive Anthropic → repaired to claude
    [InlineData("claude-code", "OpenAI", "codex-cli", true)]    // mirror: claude can't drive OpenAI → repaired to codex
    [InlineData("codex-cli", "anthropic", "claude-code", true)] // provider match is case-insensitive
    public void Reconciles_to_a_harness_that_can_drive_the_provider(string authoredKind, string provider, string expectedKind, bool expectedRepaired)
    {
        var result = HarnessModelReconciler.Reconcile(authoredKind, provider, Pool, Default);

        result.HarnessKind.ShouldBe(expectedKind);
        result.Repaired.ShouldBe(expectedRepaired);
        (result.Note is null).ShouldBe(!expectedRepaired, "a repair carries a timeline note; a kept harness does not");
    }

    [Theory]
    [InlineData("codex-cli", "codex-cli")]      // default codex → a Custom (OpenAI-wire) gateway derives codex, not the alphabetically-first claude
    [InlineData("claude-code", "claude-code")]  // a claude default → Custom derives claude
    public void Among_multiple_drivers_the_default_harness_wins_the_tie_break(string defaultKind, string expected)
    {
        // "Custom" is driven by BOTH codex-cli and claude-code. When the authored harness can't drive it (here a keyless
        // stub), the DEFAULT harness wins — so a Custom gateway follows the OpenAI-wire default rather than alphabetical order.
        var pool = new IAgentHarness[] { new KeylessHarness("local-stub"), Codex, Claude };

        var result = HarnessModelReconciler.Reconcile("local-stub", "Custom", pool, defaultKind);

        result.HarnessKind.ShouldBe(expected);
        result.Repaired.ShouldBeTrue();
    }

    [Fact]
    public void A_provider_no_harness_can_drive_leaves_the_authored_harness_for_the_resolver_to_reject()
    {
        // The honest floor: nothing to fall back to → don't silently run a wrong model; let the credential resolver
        // surface its precise "cannot drive" error.
        var result = HarnessModelReconciler.Reconcile("codex-cli", "SomeUnsupportedProvider", Pool, Default);

        result.HarnessKind.ShouldBe("codex-cli");
        result.Repaired.ShouldBeFalse();
    }

    [Fact]
    public void A_harness_that_projects_no_credentials_is_never_chosen_as_the_fallback()
    {
        // A future keyless/local harness (no IModelCredentialProjector) drives nothing here — it must never be picked
        // to "drive" a provider it can't authenticate to.
        var keyless = new KeylessHarness("local-stub");
        var pool = new IAgentHarness[] { keyless, Claude };

        var result = HarnessModelReconciler.Reconcile("local-stub", "Anthropic", pool, Default);

        result.HarnessKind.ShouldBe("claude-code", "the keyless harness can't drive Anthropic, so reconcile to the one that can");
        result.Repaired.ShouldBeTrue();
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
