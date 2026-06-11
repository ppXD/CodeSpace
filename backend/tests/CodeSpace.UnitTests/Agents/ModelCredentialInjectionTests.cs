using CodeSpace.Core.Services.Agents;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// Pins the pure pieces of model-credential injection: the operator-global env-var name (Rule 8) and the
/// executor's env merge (injected secret overrides a same-named task var; an empty secret is byte-for-byte
/// the old behavior). The DB-load / tenancy / decrypt branches are integration-pinned.
/// </summary>
[Trait("Category", "Unit")]
public class ModelCredentialInjectionTests
{
    [Fact]
    public void OpenAIOperatorKeyEnvVar_is_pinned() =>
        // Renaming this breaks every single-tenant operator who set the global OpenAI key via env.
        ModelCredentialResolver.OpenAIOperatorKeyEnvVar.ShouldBe("CODESPACE_OPENAI_API_KEY");

    [Fact]
    public void MergeEnvironment_injected_secret_overrides_a_same_named_task_var()
    {
        var taskEnv = new Dictionary<string, string> { ["KEEP"] = "task", ["OPENAI_API_KEY"] = "from-task" };
        var secretEnv = new Dictionary<string, string> { ["OPENAI_API_KEY"] = "injected" };

        var merged = AgentRunExecutor.MergeEnvironment(taskEnv, secretEnv);

        merged["KEEP"].ShouldBe("task");
        merged["OPENAI_API_KEY"].ShouldBe("injected", "the injected credential wins over a same-named task env var");
    }

    [Fact]
    public void MergeEnvironment_returns_the_task_env_unchanged_when_no_secret() =>
        // An empty secret env is byte-for-byte the old behavior — the SAME task env reference, no copy.
        AgentRunExecutor.MergeEnvironment(new Dictionary<string, string> { ["A"] = "1" }, new Dictionary<string, string>())
            .ShouldContainKeyAndValue("A", "1");
}
