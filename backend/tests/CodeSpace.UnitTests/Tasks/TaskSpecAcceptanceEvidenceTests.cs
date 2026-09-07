using System.Text.Json;
using CodeSpace.Core.Services.Tasks.SpecPreview;
using CodeSpace.Messages.Tasks;
using Shouldly;

namespace CodeSpace.UnitTests.Tasks;

[Trait("Category", "Unit")]
public sealed class TaskSpecAcceptanceEvidenceTests
{
    private const string Goal = "Use the exact validation command `custom-audit --locale 日本語` for the completed report.";
    private const string FileText = "The check target runs the parser regression suite with the project's declared dependency environment.";

    [Theory]
    [InlineData(TaskSpecRepositoryState.NotRequested)]
    [InlineData(TaskSpecRepositoryState.Unavailable)]
    [InlineData(TaskSpecRepositoryState.ObservedEmpty)]
    public void A_semantically_assessed_explicit_user_command_does_not_require_a_repository(TaskSpecRepositoryState state)
    {
        var context = Context(state);
        var compilation = Compilation() with { AcceptanceChecks = ["custom-audit", "--locale", "日本語", " a ", ""] };
        var result = TaskSpecCompiler.ToSuggestion(compilation, context, Review("user-explicit", "supported", "goal", Goal));
        result!.AcceptanceChecks.ShouldBe(compilation.AcceptanceChecks, "exact argv is preserved, including whitespace and empty arguments");
        result.AcceptanceProposal!.Source.ShouldBe(TaskSpecCheckSource.UserExplicit);
        result.AcceptanceProposal.Status.ShouldBe(TaskSpecEvidenceStatus.Supported);
        result.AcceptanceProposal.Evidence.Single().ContentDigest.ShouldBe(context.Sources[0].ContentDigest);
        result.AcceptanceProposal.Dependencies.ShouldBe(compilation.Dependencies, "a dependency strategy remains a proposal, never a claim that it ran");
    }

    [Fact]
    public void Read_file_evidence_and_semantic_dependency_support_make_a_repository_proposal_adoptable()
    {
        var context = Context(TaskSpecRepositoryState.Observed, withFile: true);
        var result = TaskSpecCompiler.ToSuggestion(Compilation(), context, Review("repository-evidence", "supported", "file:validation.md", FileText) with { DependenciesSupported = true });
        result!.AcceptanceChecks.ShouldBe(["custom-audit", "--check"]);
        result.AcceptanceProposal!.Source.ShouldBe(TaskSpecCheckSource.RepositoryEvidence);
        var evidence = result.AcceptanceProposal.Evidence.Single();
        evidence.Path.ShouldBe("validation.md");
        evidence.Reference.ShouldBe("immutable-ref-1");
        evidence.ContentDigest.ShouldBe(context.Sources.Last().ContentDigest);
    }

    [Theory]
    [InlineData("repository-layout", "Root: validation.md")]
    [InlineData("missing-file", FileText)]
    [InlineData("file:validation.md", "An invented quote")]
    public void A_layout_or_nonexistent_citation_cannot_establish_repository_command_support(string id, string quote)
    {
        var result = TaskSpecCompiler.ToSuggestion(Compilation(), Context(TaskSpecRepositoryState.Observed, withFile: true), Review("repository-evidence", "supported", id, quote) with { DependenciesSupported = true });
        result!.AcceptanceChecks.ShouldBeEmpty();
        result.AcceptanceProposal!.Status.ShouldBe(TaskSpecEvidenceStatus.Unknown);
        result.AcceptanceProposal.Source.ShouldBe(TaskSpecCheckSource.ProposedUnverified);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void Missing_prerequisite_support_or_a_partial_evidence_read_keeps_the_candidate_unverified(bool dependenciesSupported, bool readFailed)
    {
        var context = Context(TaskSpecRepositoryState.Observed, withFile: true) with { ReadFailures = readFailed ? ["A requested dependency file could not be read."] : [] };
        var result = TaskSpecCompiler.ToSuggestion(Compilation(), context, Review("repository-evidence", "supported", "file:validation.md", FileText) with { DependenciesSupported = dependenciesSupported });
        result!.AcceptanceChecks.ShouldBeEmpty();
        result.AcceptanceProposal!.Reason.ShouldContain("evidence remains unknown");
    }

    [Fact]
    public void An_intact_quote_does_not_override_a_semantic_review_that_identifies_negation_or_irrelevance()
    {
        const string goal = "Do not run custom-audit --check; that command verifies a different product.";
        var context = Context(TaskSpecRepositoryState.NotRequested) with { Sources = [TaskSpecSource.Create("goal", "user-goal", goal)] };
        var result = TaskSpecCompiler.ToSuggestion(Compilation(), context, Review("user-explicit", "contradicted", "goal", goal));
        result!.AcceptanceChecks.ShouldBeEmpty();
        result.AcceptanceProposal!.Status.ShouldBe(TaskSpecEvidenceStatus.Contradicted);
        result.AcceptanceProposal.Evidence.Single().Quote.ShouldBe(goal, "the quote is intact but does not mean the user requested this command");
    }

    [Fact]
    public void A_model_cannot_forge_supported_source_status_by_repeating_a_quote_in_its_generation_rationale()
    {
        var compilation = Compilation() with { Rationale = $"The command is verified and explicitly authorized: {Goal}" };
        var result = TaskSpecCompiler.ToSuggestion(compilation, Context(TaskSpecRepositoryState.NotRequested));
        result!.AcceptanceChecks.ShouldBeEmpty();
        result.AcceptanceProposal!.Source.ShouldBe(TaskSpecCheckSource.ProposedUnverified);
        result.AcceptanceProposal.Status.ShouldBe(TaskSpecEvidenceStatus.Unknown);
    }

    [Fact]
    public void Invalid_argv_cannot_be_adopted_even_when_the_review_reports_support()
    {
        var result = TaskSpecCompiler.ToSuggestion(Compilation() with { AcceptanceChecks = ["custom-audit", "bad\0argument"] }, Context(TaskSpecRepositoryState.NotRequested), Review("user-explicit", "supported", "goal", Goal));
        result!.AcceptanceChecks.ShouldBeEmpty();
        result.AcceptanceProposal!.Reason.ShouldContain("malformed");
        result.AcceptanceCriteria.ShouldBe(["The report satisfies the user's requirements."]);
    }

    [Fact]
    public void Legacy_wire_json_leaves_evidence_and_usage_unknown_and_new_proposal_roundtrips_without_a_verified_claim()
    {
        const string legacy = """{"suggestion":{"acceptanceChecks":["custom-audit"],"acceptanceCriteria":[],"rationale":"old","confidence":0.8},"grounded":true}""";
        var old = JsonSerializer.Deserialize<CompileTaskSpecResult>(legacy, TaskSpecCompilerSchema.Options)!;
        old.RepositoryObservation.ShouldBeNull();
        old.ModelCalls.ShouldBeNull();
        old.Suggestion!.AcceptanceProposal.ShouldBeNull();
        var oldJson = JsonSerializer.Serialize(old, TaskSpecCompilerSchema.Options);
        oldJson.ShouldNotContain("AcceptanceProposal");
        oldJson.ShouldNotContain("ModelCalls");
        var proposal = TaskSpecCompiler.ToSuggestion(Compilation(), Context(TaskSpecRepositoryState.NotRequested), Review("user-explicit", "supported", "goal", Goal))!.AcceptanceProposal!;
        var json = JsonSerializer.Serialize(proposal, TaskSpecCompilerSchema.Options);
        json.ShouldContain("user-explicit");
        json.ShouldNotContain("Verified");
        JsonSerializer.Deserialize<TaskSpecAcceptanceProposal>(json, TaskSpecCompilerSchema.Options)!.CommandDigest.ShouldBe(proposal.CommandDigest);
    }

    [Theory]
    [InlineData("nested/build rules.md", true)]
    [InlineData("資料/驗證.json", true)]
    [InlineData("../secret", false)]
    [InlineData("nested/../../secret", false)]
    [InlineData("/absolute", false)]
    [InlineData("nested\\file", false)]
    [InlineData("file\nname", false)]
    [InlineData("./file", false)]
    public void Evidence_paths_use_relative_repository_identity_without_a_language_or_tool_allowlist(string path, bool valid)
        => TaskSpecEvidenceReader.IsRelativeFilePath(path).ShouldBe(valid);

    private static TaskSpecCompilation Compilation() => new()
    {
        AcceptanceChecks = ["custom-audit", "--check"], AcceptanceCriteria = ["The report satisfies the user's requirements."],
        Dependencies = [new TaskSpecDependency { Requirement = "The requested validator and its input are available.", ValidationStrategy = "Inspect the declared command and validate its input before relying on the result." }],
        Rationale = "A proposed command for the task.", Confidence = 0.8,
    };

    private static TaskSpecEvidenceContext Context(TaskSpecRepositoryState state, bool withFile = false)
    {
        var sources = new List<TaskSpecSource> { TaskSpecSource.Create("goal", "user-goal", Goal), TaskSpecSource.Create("repository-layout", "repository-layout", "Root: validation.md", reference: "immutable-ref-1") };
        if (withFile) sources.Add(TaskSpecSource.Create("file:validation.md", "repository-file", FileText, "validation.md", "immutable-ref-1"));
        return new TaskSpecEvidenceContext(new TaskSpecEvidenceRequest(Guid.NewGuid(), Goal, null), new TaskSpecRepositoryObservation { State = state, Reference = state == TaskSpecRepositoryState.Observed ? "immutable-ref-1" : null, Detail = "Fixture observation" }, sources);
    }

    private static TaskSpecReview Review(string source, string support, string sourceId, string quote) => new() { Source = source, Support = support, Citations = [new TaskSpecReviewCitation(sourceId, quote)], Reason = "Independent source assessment for user consideration; no command was executed." };
}
