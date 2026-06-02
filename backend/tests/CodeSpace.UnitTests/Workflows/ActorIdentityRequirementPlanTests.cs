using System.Text.Json;
using CodeSpace.Core.Services.Workflows.Engine;
using CodeSpace.Core.Services.Workflows.Nodes;
using CodeSpace.Messages.Dtos.Workflows;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

[Trait("Category", "Unit")]
public class ActorIdentityRequirementPlanTests
{
    private const string Repo = "9c4ea7ba-bb16-4cd2-b312-bdf22c3cc789";

    // The only act-as-user node in these tests; mirrors git.pr_review's declared spec.
    private static readonly ActsAsUserSpec ReviewSpec = new() { ActorInputKey = "actAsUserId", ProviderInputKey = "repositoryId", ProviderSource = ActorProviderSource.Repository };
    private static ActsAsUserSpec? Lookup(string typeKey) => typeKey == "git.pr_review" ? ReviewSpec : null;

    [Fact]
    public void Derives_a_requirement_when_a_downstream_act_as_user_node_references_the_wait()
    {
        var def = Def(
            new[] { Node("wait", "flow.wait_action"), Node("review", "git.pr_review", """{ "actAsUserId": "{{nodes.wait.outputs.by}}", "repositoryId": "{{input.repo}}" }""") },
            ("wait", "review"));

        var reqs = ActorIdentityRequirementPlan.Derive(def, "wait", Lookup, Scope(("repo", Repo)));

        reqs.Count.ShouldBe(1);
        reqs[0].NodeId.ShouldBe("review");
        reqs[0].ProviderSource.ShouldBe(ActorProviderSource.Repository);
        reqs[0].ResolvedId.ShouldBe(Repo);
    }

    [Fact]
    public void Ignores_an_act_as_user_node_whose_actor_is_not_the_wait_responder()
    {
        var def = Def(
            new[] { Node("wait", "flow.wait_action"), Node("review", "git.pr_review", """{ "actAsUserId": "00000000-0000-0000-0000-000000000001", "repositoryId": "{{input.repo}}" }""") },
            ("wait", "review"));

        ActorIdentityRequirementPlan.Derive(def, "wait", Lookup, Scope(("repo", Repo)))
            .ShouldBeEmpty("a static / different actor isn't the responder — can't prompt them via the responder's click");
    }

    [Fact]
    public void Resolves_a_literal_repository_id()
    {
        var def = Def(
            new[] { Node("wait", "flow.wait_action"), Node("review", "git.pr_review", """{ "actAsUserId": "{{nodes.wait.outputs.by}}", "repositoryId": "9c4ea7ba-bb16-4cd2-b312-bdf22c3cc789" }""") },
            ("wait", "review"));

        ActorIdentityRequirementPlan.Derive(def, "wait", Lookup, Scope()).Single().ResolvedId.ShouldBe(Repo);
    }

    [Fact]
    public void Resolves_the_dollar_ref_object_form_of_the_provider_input()
    {
        var def = Def(
            new[] { Node("wait", "flow.wait_action"), Node("review", "git.pr_review", """{ "actAsUserId": "{{nodes.wait.outputs.by}}", "repositoryId": { "$ref": "input.repo" } }""") },
            ("wait", "review"));

        ActorIdentityRequirementPlan.Derive(def, "wait", Lookup, Scope(("repo", Repo))).Single().ResolvedId.ShouldBe(Repo);
    }

    [Fact]
    public void Skips_gracefully_when_the_provider_input_is_an_unresolvable_upstream_output()
    {
        var def = Def(
            new[] { Node("wait", "flow.wait_action"), Node("review", "git.pr_review", """{ "actAsUserId": "{{nodes.wait.outputs.by}}", "repositoryId": "{{nodes.find.outputs.repoId}}" }""") },
            ("wait", "review"));

        ActorIdentityRequirementPlan.Derive(def, "wait", Lookup, Scope())
            .ShouldBeEmpty("a provider id from an upstream node output isn't resolvable from run inputs — skip, never guess");
    }

    [Fact]
    public void Ignores_a_non_act_as_user_downstream_node()
    {
        var def = Def(
            new[] { Node("wait", "flow.wait_action"), Node("post", "chat.post_message", """{ "conversationId": "{{nodes.wait.outputs.by}}" }""") },
            ("wait", "post"));

        ActorIdentityRequirementPlan.Derive(def, "wait", Lookup, Scope(("repo", Repo))).ShouldBeEmpty();
    }

    [Fact]
    public void Ignores_an_act_as_user_node_that_is_not_downstream_of_the_wait()
    {
        // review runs BEFORE the wait → resolving the wait doesn't trigger it → no requirement.
        var def = Def(
            new[] { Node("review", "git.pr_review", """{ "actAsUserId": "{{nodes.wait.outputs.by}}", "repositoryId": "{{input.repo}}" }"""), Node("wait", "flow.wait_action") },
            ("review", "wait"));

        ActorIdentityRequirementPlan.Derive(def, "wait", Lookup, Scope(("repo", Repo))).ShouldBeEmpty();
    }

    [Fact]
    public void Dedupes_multiple_act_as_user_nodes_on_the_same_provider()
    {
        var def = Def(
            new[]
            {
                Node("wait", "flow.wait_action"),
                Node("r1", "git.pr_review", """{ "actAsUserId": "{{nodes.wait.outputs.by}}", "repositoryId": "{{input.repo}}" }"""),
                Node("r2", "git.pr_review", """{ "actAsUserId": "{{nodes.wait.outputs.by}}", "repositoryId": "{{input.repo}}" }"""),
            },
            ("wait", "r1"), ("r1", "r2"));

        ActorIdentityRequirementPlan.Derive(def, "wait", Lookup, Scope(("repo", Repo)))
            .Count.ShouldBe(1, "same provider instance ⇒ one requirement, even across multiple act-as-user nodes");
    }

    // ── Builders ─────────────────────────────────────────────────────────────────

    private static NodeDefinition Node(string id, string typeKey, string inputsJson = "{}") =>
        new() { Id = id, TypeKey = typeKey, Config = Json("{}"), Inputs = Json(inputsJson) };

    private static WorkflowDefinition Def(NodeDefinition[] nodes, params (string From, string To)[] edges) => new()
    {
        SchemaVersion = 1,
        Nodes = nodes,
        Edges = edges.Select(e => new EdgeDefinition { From = e.From, To = e.To }).ToList(),
    };

    private static Dictionary<string, JsonElement> Scope(params (string Key, string Value)[] entries) =>
        entries.ToDictionary(e => e.Key, e => JsonSerializer.SerializeToElement(e.Value));

    private static JsonElement Json(string s) => JsonDocument.Parse(s).RootElement.Clone();
}
