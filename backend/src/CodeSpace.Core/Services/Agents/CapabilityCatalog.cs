using System.Text;
using CodeSpace.Core.Services.Agents.ModelCredentials;
using CodeSpace.Messages.Enums;

namespace CodeSpace.Core.Services.Agents;

/// <summary>
/// The single capability-catalog renderer the supervisor decider AND the workflow planner both author against (P1 +
/// P2): every registered harness + the model providers it can drive, and the run's credentialed pool models + each
/// model's provider — so the LLM picks a provider-compatible (harness, model) pair on purpose rather than blind. One
/// renderer keeps the manifest the two brains see identical (the highest-leverage SOTA move — the model can only
/// allocate well when the options + their constraints are IN the prompt). Pure + static so it is unit-pinned without
/// an LLM/DB.
/// </summary>
public static class CapabilityCatalog
{
    public static string Render(IReadOnlyList<IAgentHarness> harnesses, IReadOnlyList<PoolModelInfo> pool, IReadOnlyList<PersonaCatalogInfo>? personas = null)
    {
        var builder = new StringBuilder();

        builder.AppendLine("Capability catalog — when you author a per-agent harness + model, the harness MUST be able to drive the model's provider:");

        builder.AppendLine("Harnesses (and the model providers each can drive):");
        foreach (var harness in harnesses.OrderBy(h => h.Kind, StringComparer.Ordinal))
        {
            var providers = harness is IModelCredentialProjector projector && projector.SupportedProviders.Count > 0
                ? string.Join(", ", projector.SupportedProviders)
                : "(needs no model key)";
            builder.AppendLine($"  - {harness.Kind} — drives: {providers}");
        }

        if (pool.Count > 0)
        {
            builder.AppendLine("Models in this run's credentialed pool (model — provider):");
            foreach (var model in pool)
            {
                // Cached capability tier (frontier / strong / basic) when known — omitted for an un-tiered / opaque model
                // so an all-Unknown pool renders byte-identically to before this signal existed.
                var tier = model.Tier == ModelCapabilityTier.Unknown ? "" : $" — tier: {model.Tier.ToString().ToLowerInvariant()}";
                builder.AppendLine($"  - {model.ModelId} — {model.Provider}{tier}");
            }

            if (pool.Any(m => m.Tier != ModelCapabilityTier.Unknown))
                builder.AppendLine("Prefer a higher-tier model (frontier > strong > basic) for a harder subtask, and a cheaper lower-tier one for a trivial subtask.");

            builder.AppendLine("Per agent, pick a model from THIS pool and a harness whose providers include that model's provider. The server corrects an incompatible pair, but a compatible choice is preferred.");
        }
        else
        {
            builder.AppendLine("No credentialed models are listed for this run — omit per-agent model/harness and let the run defaults apply.");
        }

        // The persona pool (supervisor only — the planner passes none). The brain authors a per-agent persona by its
        // slug; the section is omitted entirely when no personas exist so the planner's two-arg render is byte-identical.
        if (personas is { Count: > 0 })
        {
            builder.AppendLine("Agent personas in this team's library (slug — name — description). Author a per-agent persona by its SLUG to give that agent a specialist role/prompt:");
            foreach (var persona in personas.OrderBy(p => p.Slug, StringComparer.Ordinal))
                builder.AppendLine($"  - {persona.Slug} — {persona.Name}{(string.IsNullOrWhiteSpace(persona.Description) ? "" : $" — {persona.Description}")}");
        }

        return builder.ToString();
    }
}

/// <summary>One persona row the capability catalog renders so the brain can author a per-agent persona by slug (Rule 18.1 — a render-input noun). Maps from <c>AgentDefinitionSummary</c>; only the fields the brain reasons about (slug, name, routing description).</summary>
public sealed record PersonaCatalogInfo(string Slug, string Name, string? Description);
