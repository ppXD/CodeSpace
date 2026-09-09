using System.Text.Json;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents.ModelCredentials;
using CodeSpace.Core.Services.Learning;
using CodeSpace.Core.Services.Workflows.Llm;
using CodeSpace.Messages.Agents;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace CodeSpace.UnitTests.Learning;

[Trait("Category", "Unit")]
public sealed class LessonRelevanceEvaluatorTests
{
    [Fact]
    public async Task Structured_model_selects_only_a_semantically_relevant_lesson_with_verifiable_evidence()
    {
        var relevant = Lesson("Restore packages before compiling", "Run dotnet restore before dotnet build");
        var unrelated = Lesson("A CSS grid overflowed", "Clamp the card width in the stylesheet");
        var client = new ScriptedClient($$"""
            {"decisions":[
              {"lessonId":"{{relevant.Id}}","verdict":"relevant","taskEvidence":"compile the service","lessonEvidence":"before dotnet build","rationale":"The prerequisite directly affects the requested build."},
              {"lessonId":"{{unrelated.Id}}","verdict":"irrelevant","taskEvidence":"","lessonEvidence":"","rationale":"No UI work is requested."}
            ]}
            """);
        var evaluator = new LlmLessonRelevanceEvaluator(new Registry(client), new Selector(), NullLogger<LlmLessonRelevanceEvaluator>.Instance);

        var result = await evaluator.EvaluateAsync(new(Guid.NewGuid(), "Restore dependencies and compile the service", [relevant, unrelated], 5), CancellationToken.None);

        result.Status.ShouldBe(LessonRelevanceStatuses.Selected);
        result.Lessons.ShouldBe([relevant]);
        result.CandidateIds.ShouldBe([relevant.Id, unrelated.Id]);
        result.ObservedModel.ShouldBe("observed-model");
        result.AssessmentDigest.ShouldNotBeNull();
        client.Requests.ShouldHaveSingleItem().ResponseValidator!(client.Answer).ShouldBeEmpty();
        client.Requests[0].UserPrompt.ShouldContain("Restore dependencies and compile the service");
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("foreign")]
    [InlineData("invented-evidence")]
    public void Incomplete_or_unverifiable_assessments_fail_closed(string fault)
    {
        var first = Lesson("Restore packages", "Run dotnet restore before compiling");
        var second = Lesson("Regenerate schemas", "Run the schema generator before compiling");
        var foreign = Guid.NewGuid();
        var decisions = fault switch
        {
            "missing" => $$"""[{"lessonId":"{{first.Id}}","verdict":"irrelevant","taskEvidence":"","lessonEvidence":"","rationale":"no"}]""",
            "duplicate" => $$"""[{"lessonId":"{{first.Id}}","verdict":"irrelevant","taskEvidence":"","lessonEvidence":"","rationale":"no"},{"lessonId":"{{first.Id}}","verdict":"irrelevant","taskEvidence":"","lessonEvidence":"","rationale":"no"}]""",
            "foreign" => $$"""[{"lessonId":"{{first.Id}}","verdict":"irrelevant","taskEvidence":"","lessonEvidence":"","rationale":"no"},{"lessonId":"{{foreign}}","verdict":"irrelevant","taskEvidence":"","lessonEvidence":"","rationale":"no"}]""",
            _ => $$"""[{"lessonId":"{{first.Id}}","verdict":"relevant","taskEvidence":"a phrase the task never contained","lessonEvidence":"restore","rationale":"yes"},{"lessonId":"{{second.Id}}","verdict":"irrelevant","taskEvidence":"","lessonEvidence":"","rationale":"no"}]""",
        };
        var request = new LessonRelevanceRequest(Guid.NewGuid(), "Compile the service", [first, second], 5);

        var result = LlmLessonRelevanceEvaluator.Project(request, JsonDocument.Parse($$"""{"decisions":{{decisions}}}""").RootElement, "observed-model");

        result.Status.ShouldBe(LessonRelevanceStatuses.Failed);
        result.Lessons.ShouldBeEmpty();
        result.CandidateIds.ShouldBe([first.Id, second.Id]);
    }

    [Fact]
    public async Task Missing_model_is_an_explicit_unavailable_abstention()
    {
        var lesson = Lesson("Restore packages", "Run restore first");
        var evaluator = new LlmLessonRelevanceEvaluator(new Registry(), new Selector(hasModel: false), NullLogger<LlmLessonRelevanceEvaluator>.Instance);

        var result = await evaluator.EvaluateAsync(new(Guid.NewGuid(), "Compile", [lesson], 5), CancellationToken.None);

        result.Status.ShouldBe(LessonRelevanceStatuses.Unavailable);
        result.Lessons.ShouldBeEmpty();
        result.CandidateIds.ShouldBe([lesson.Id]);
    }

    [Fact]
    public async Task Provider_failure_is_an_explicit_failed_abstention()
    {
        var lesson = Lesson("Restore packages", "Run restore first");
        var evaluator = new LlmLessonRelevanceEvaluator(new Registry(new ThrowingClient(new InvalidOperationException("gateway unavailable"))), new Selector(), NullLogger<LlmLessonRelevanceEvaluator>.Instance);

        var result = await evaluator.EvaluateAsync(new(Guid.NewGuid(), "Compile", [lesson], 5), CancellationToken.None);

        result.Status.ShouldBe(LessonRelevanceStatuses.Failed);
        result.Lessons.ShouldBeEmpty();
        result.CandidateIds.ShouldBe([lesson.Id]);
        result.AssessmentDigest.ShouldBeNull();
    }

    [Fact]
    public async Task Caller_cancellation_is_never_converted_into_an_assessment()
    {
        var lesson = Lesson("Restore packages", "Run restore first");
        var evaluator = new LlmLessonRelevanceEvaluator(new Registry(new ThrowingClient(new OperationCanceledException())), new Selector(), NullLogger<LlmLessonRelevanceEvaluator>.Instance);

        await Should.ThrowAsync<OperationCanceledException>(() => evaluator.EvaluateAsync(new(Guid.NewGuid(), "Compile", [lesson], 5), CancellationToken.None));
    }

    [Fact]
    public async Task Model_input_enforces_task_lesson_and_candidate_bounds()
    {
        var candidates = Enumerable.Range(0, LlmLessonRelevanceEvaluator.MaxCandidates + 1)
            .Select(index => Lesson(new string((char)('a' + index), LlmLessonRelevanceEvaluator.MaxLessonChars + 100), "bounded guidance"))
            .ToList();
        var decisions = candidates.Take(LlmLessonRelevanceEvaluator.MaxCandidates).Select(lesson => new { lessonId = lesson.Id, verdict = "irrelevant", taskEvidence = "", lessonEvidence = "", rationale = "not applicable" });
        var client = new ScriptedClient(JsonSerializer.Serialize(new { decisions }));
        var evaluator = new LlmLessonRelevanceEvaluator(new Registry(client), new Selector(), NullLogger<LlmLessonRelevanceEvaluator>.Instance);

        var result = await evaluator.EvaluateAsync(new(Guid.NewGuid(), new string('t', LlmLessonRelevanceEvaluator.MaxTaskChars + 100), candidates, 5), CancellationToken.None);

        result.CandidateIds.Count.ShouldBe(LlmLessonRelevanceEvaluator.MaxCandidates);
        result.CandidateIds.ShouldNotContain(candidates[^1].Id);
        using var prompt = JsonDocument.Parse(client.Requests.ShouldHaveSingleItem().UserPrompt);
        prompt.RootElement.GetProperty("task").GetString()!.Length.ShouldBe(LlmLessonRelevanceEvaluator.MaxTaskChars);
        prompt.RootElement.GetProperty("candidates").GetArrayLength().ShouldBe(LlmLessonRelevanceEvaluator.MaxCandidates);
        prompt.RootElement.GetProperty("candidates")[0].GetProperty("text").GetString()!.Length.ShouldBe(LlmLessonRelevanceEvaluator.MaxLessonChars);
    }

    private static Lesson Lesson(string whatFailed, string howToApply) => new()
    {
        Id = Guid.NewGuid(), FailureClass = "build", WhatFailed = whatFailed, Why = "ordering", HowToApply = howToApply,
    };

    private sealed class ScriptedClient : ILLMClient, IStructuredLLMClient
    {
        public ScriptedClient(string json) => Answer = JsonDocument.Parse(json).RootElement.Clone();
        public string Provider => "Test";
        public JsonElement Answer { get; }
        public List<StructuredLLMCompletionRequest> Requests { get; } = [];
        public Task<LLMCompletion> CompleteAsync(LLMCompletionRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<StructuredLLMCompletion> CompleteStructuredAsync(StructuredLLMCompletionRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(new StructuredLLMCompletion { Json = Answer, Model = request.Model, ObservedModel = "observed-model" });
        }
    }

    private sealed class ThrowingClient(Exception error) : ILLMClient, IStructuredLLMClient
    {
        public string Provider => "Test";
        public Task<LLMCompletion> CompleteAsync(LLMCompletionRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<StructuredLLMCompletion> CompleteStructuredAsync(StructuredLLMCompletionRequest request, CancellationToken cancellationToken) => Task.FromException<StructuredLLMCompletion>(error);
    }

    private sealed class Registry(params ILLMClient[] clients) : ILLMClientRegistry
    {
        public IReadOnlyList<ILLMClient> All { get; } = clients;
        public ILLMClient Resolve(string provider) => All.Single(client => client.Provider == provider);
    }

    private sealed class Selector(bool hasModel = true) : IModelPoolSelector
    {
        public Task<ModelPoolPick?> SelectAsync(Guid teamId, string provider, IReadOnlyList<string>? allowedModels, string? pinnedModel, CancellationToken cancellationToken) => Task.FromResult(hasModel ? new ModelPoolPick { ModelId = "test-model", Credential = new ResolvedModelCredential { Provider = provider, ApiKey = "test" } } : null);
        public Task<ModelPoolPick?> ResolveByRowIdAsync(Guid teamId, Guid modelCredentialModelId, CancellationToken cancellationToken) => Task.FromResult<ModelPoolPick?>(null);
        public Task<ModelDispatchRef?> ResolveDispatchAsync(Guid teamId, string modelName, IReadOnlyList<Guid>? allowedRowIds, CancellationToken cancellationToken) => Task.FromResult<ModelDispatchRef?>(null);
        public Task<IReadOnlyList<PoolModelInfo>> ListPoolAsync(Guid teamId, IReadOnlyList<Guid>? allowedRowIds, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<PoolModelInfo>>([]);
        public Task<Guid?> SelectBrainRowIdAsync(Guid teamId, IReadOnlyCollection<string> eligibleProviders, CancellationToken cancellationToken) => Task.FromResult<Guid?>(null);
        public Task<Guid?> ResolvePinnedBrainRowIdAsync(Guid teamId, Guid modelCredentialModelId, IReadOnlyCollection<string> eligibleProviders, CancellationToken cancellationToken) => Task.FromResult<Guid?>(null);
        public Task<string?> ResolveTeamDefaultProviderAsync(Guid teamId, CancellationToken cancellationToken) => Task.FromResult<string?>(null);
    }
}
