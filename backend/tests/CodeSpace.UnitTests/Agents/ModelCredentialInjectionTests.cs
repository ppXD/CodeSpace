using CodeSpace.Core.Services.Agents;
using CodeSpace.Messages.Agents;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// Pins the pure pieces of model-credential injection: the operator-global env-var name (Rule 8), the
/// executor's env merge (injected secret overrides a same-named task var; an empty secret is byte-for-byte
/// the old behavior), and the needle set the run's <see cref="SecretRedactor"/> is built from. The DB-load /
/// tenancy / decrypt branches are integration-pinned.
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

    [Fact]
    public void BuildRunRedactor_masks_every_secret_the_run_injects()
    {
        // The three carriers that put a secret into the child's environment: the decrypted api key, the credential
        // parts embedded in a gateway base URL, and an author-supplied env secret (a git token, an MCP secret an
        // agent definition injects). All three can be echoed straight back by the CLI into text the run PERSISTS.
        var credential = new ResolvedModelCredential { Provider = "Custom", ApiKey = "sk-live-9f3a7c21", BaseUrl = "https://svc:bx7-gateway-pass@gw.example/v1?api-key=qk-8811-zzzz&api-version=2024-02-01" };
        var env = new Dictionary<string, string> { ["GIT_ASKPASS_TOKEN"] = "ghp-7ac41d2e9b", ["ANTHROPIC_BASE_URL"] = "https://gw.example", ["ANTHROPIC_MODEL"] = "claude-sonnet-4-5" };

        var redacted = AgentRunExecutor.BuildRunRedactor(env, credential)
            .Redact("401 at 2024-02-01T09:12:00Z from https://svc:bx7-gateway-pass@gw.example/v1?api-key=qk-8811-zzzz&api-version=2024-02-01 with sk-live-9f3a7c21 and ghp-7ac41d2e9b for claude-sonnet-4-5");

        redacted.ShouldNotContain("sk-live-9f3a7c21", customMessage: "the api key must not reach a persisted error");
        redacted.ShouldNotContain("bx7-gateway-pass", customMessage: "a base URL's userinfo password is a key wearing a URL");
        redacted.ShouldNotContain("qk-8811-zzzz", customMessage: "a gateway that authenticates by query string carries its key in the base URL");
        redacted.ShouldNotContain("ghp-7ac41d2e9b", customMessage: "an author-supplied env token is injected by the same launch and leaks the same way");
        redacted.ShouldContain("claude-sonnet-4-5", customMessage: "the model name is not a secret — masking it would blind the journal card");
        redacted.ShouldContain("gw.example", customMessage: "the endpoint host is the diagnostic the error is read FOR; only the credentials inside the URL are struck");
        redacted.ShouldContain("2024-02-01T09:12:00Z", customMessage: "a query parameter whose NAME does not mark it secret stays readable — masking an api-version date would shred every timestamp of that day");
    }

    [Fact]
    public void BuildRunRedactor_leaves_a_short_injected_value_readable()
    {
        // A value below the guard is a fragment, not an identifier — striking "1" would hit every line it appears on.
        var subject = AgentRunExecutor.BuildRunRedactor(new Dictionary<string, string> { ["CACHE_KEY"] = "1", ["DEBUG_TOKEN"] = "true" }, credential: null);

        subject.IsEmpty.ShouldBeTrue("no injected value clears the minimum-needle guard");
        subject.Redact("retry 1 of 3, verbose=true").ShouldBe("retry 1 of 3, verbose=true");
    }

    [Fact]
    public void MinimumNeedleLength_is_pinned() =>
        // Same threshold MIN_NEEDLE_LENGTH uses in .github/scripts/collect-real-model-verdicts.sh; lowering it here
        // starts shredding unrelated text, raising it starts shipping short secrets in the clear.
        AgentRunExecutor.MinimumNeedleLength.ShouldBe(8);

    [Fact]
    public void BuildRunRedactor_never_names_a_needle_in_its_own_diagnostics()
    {
        var credential = new ResolvedModelCredential { Provider = "Custom", ApiKey = "sk-live-9f3a7c21", BaseUrl = "https://gw.example/v1?api-key=qk-8811-zzzz" };

        var subject = AgentRunExecutor.BuildRunRedactor(new Dictionary<string, string> { ["GIT_TOKEN"] = "ghp-7ac41d2e9b" }, credential);

        foreach (var needle in new[] { "sk-live-9f3a7c21", "qk-8811-zzzz", "ghp-7ac41d2e9b" })
        {
            subject.ToString().ShouldNotContain(needle, customMessage: "the redactor must never print a needle when it is itself logged");
            subject.Fingerprint!.ShouldNotContain(needle, customMessage: "the fingerprint is a one-way hash, never the secrets");
        }
    }

    [Fact]
    public void BuildRunRedactor_fingerprint_is_a_function_of_the_secret_set_not_of_env_order()
    {
        // The launch stamps this fingerprint on the durable handle; a re-attach may re-tail the spool ONLY if it
        // rebuilds the same one. Two dictionaries with the same entries in a different order are the same run.
        var forward = new Dictionary<string, string> { ["A_TOKEN"] = "aaa-11112222", ["B_SECRET"] = "bbb-33334444" };
        var reversed = new Dictionary<string, string> { ["B_SECRET"] = "bbb-33334444", ["A_TOKEN"] = "aaa-11112222" };

        var fingerprint = AgentRunExecutor.BuildRunRedactor(forward, null).Fingerprint;

        fingerprint.ShouldNotBeNull("the env secrets alone key a real redactor — otherwise this pins nothing but two nulls");
        fingerprint.ShouldBe(AgentRunExecutor.BuildRunRedactor(reversed, null).Fingerprint);
    }
}
