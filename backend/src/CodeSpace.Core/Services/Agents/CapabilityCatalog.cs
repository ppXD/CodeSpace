using System.Text;
using CodeSpace.Core.Services.Agents.ModelCredentials;

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
    public static string Render(IReadOnlyList<IAgentHarness> harnesses, IReadOnlyList<PoolModelInfo> pool)
    {
        var builder = new StringBuilder();

        builder.AppendLine("Capability catalog — when you author a per-agent harness + model, the harness MUST be able to drive the model's provider:");

        builder.AppendLine("Harnesses (and the model providers each can drive):");
        foreach (var harness in harnesses)
        {
            var providers = harness is IModelCredentialProjector projector && projector.SupportedProviders.Count > 0
                ? string.Join(", ", projector.SupportedProviders)
                : "(needs no model key)";
            builder.AppendLine($"  - {harness.Kind} — drives: {providers}");
        }

        if (pool.Count > 0)
        {
            builder.AppendLine("Models in this run's credentialed pool (model — provider):");
            foreach (var model in pool) builder.AppendLine($"  - {model.ModelId} — {model.Provider}");
            builder.AppendLine("Per agent, pick a model from THIS pool and a harness whose providers include that model's provider. The server corrects an incompatible pair, but a compatible choice is preferred.");
        }
        else
        {
            builder.AppendLine("No credentialed models are listed for this run — omit per-agent model/harness and let the run defaults apply.");
        }

        return builder.ToString();
    }
}
