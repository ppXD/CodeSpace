using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodeSpace.Core.Services.Agents.Cost;
using CodeSpace.Core.Services.Workflows.Budget;
using CodeSpace.Core.Services.Workflows.Llm.Exceptions;
using CodeSpace.Core.Services.Workflows.Runtime;
using CodeSpace.Messages.Agents;

namespace CodeSpace.Core.Services.Workflows.Llm;

/// <summary>The provider's structured implementation uses the registered per-POST accounting transport.</summary>
public interface IPhysicalStructuredLLMClient : IStructuredLLMClient { }

internal static class PhysicalLlmCallContext
{
    private static readonly AsyncLocal<Operation?> CurrentOperation = new();
    private static readonly AsyncLocal<Candidate?> CurrentCandidate = new();
    private static readonly ConditionalWeakTable<HttpClient, object> RegisteredClients = new();
    internal static readonly HttpRequestOptionsKey<Dispatch> DispatchKey = new("CodeSpace.PhysicalStructuredPost/v1");

    internal static IDisposable EnterOperation()
    {
        var prior = CurrentOperation.Value;
        if (prior is null && LlmCallContext.Current is { Budget: IPhysicalLlmInvocationLedger ledger, CapUsd: not null } scope)
            CurrentOperation.Value = new Operation(scope, ledger);
        return new Restore(() => CurrentOperation.Value = prior);
    }

    internal static IDisposable EnterCandidate(LlmCallScope scope, string provider, StructuredLLMCompletionRequest request)
    {
        if (scope.Budget is not IPhysicalLlmInvocationLedger)
            throw new PhysicalLlmAccountingException("The capped structured provider requires a native physical invocation ledger.");
        var operation = CurrentOperation.Value ?? throw new PhysicalLlmAccountingException("The physical model operation scope is missing.");
        var prior = CurrentCandidate.Value;
        CurrentCandidate.Value = new Candidate(operation, provider, request);
        return new Restore(() => CurrentCandidate.Value = prior);
    }

    internal static IDisposable? EnterProvider(StructuredLLMCompletionRequest request, string provider)
    {
        if (CurrentCandidate.Value is not null || LlmCallContext.Current is not { CapUsd: not null } scope) return null;
        var operation = EnterOperation();
        try
        {
            var candidate = EnterCandidate(scope, request.Credential?.Provider ?? provider, request);
            return new Restore(() => { candidate.Dispose(); operation.Dispose(); });
        }
        catch { operation.Dispose(); throw; }
    }

    internal static Guid? LogicalCallId => CurrentOperation.Value?.Id;
    internal static Guid? CandidateId => CurrentCandidate.Value?.Id;
    internal static PersistenceSecretRedactor? CredentialRedactor => CurrentCandidate.Value?.CredentialRedactor;

    internal static void Register(HttpClient client) => RegisteredClients.GetOrCreateValue(client);

    internal static void Attach(HttpClient client, HttpRequestMessage request, Func<JsonElement, ProviderEnvelope> envelope)
    {
        if (CurrentCandidate.Value is { } candidate)
        {
            if (!RegisteredClients.TryGetValue(client, out _)) throw new PhysicalLlmAccountingException("A capped structured provider requires the registered physical accounting HTTP pipeline.");
            request.Options.Set(DispatchKey, new Dispatch(candidate, envelope));
        }
    }

    internal static StructuredLLMCompletion Aggregate(StructuredLLMCompletion completion, bool candidateOnly = false)
    {
        var operation = CurrentOperation.Value;
        if (operation is null) return completion;
        var rows = operation.Observations.Where(o => !candidateOnly || o.CandidateId == CurrentCandidate.Value?.Id).ToArray();
        if (rows.Length == 0) return completion;
        var usage = rows[0].Usage;
        for (var i = 1; i < rows.Length; i++) usage = usage.Add(rows[i].Usage, string.Equals(rows[0].Model, rows[i].Model, StringComparison.OrdinalIgnoreCase));
        return completion with { Usage = usage with { FinishReason = completion.Usage.FinishReason, IsPartial = usage.IsPartial || rows.Any(r => string.IsNullOrWhiteSpace(r.Model)) || operation.PersistenceFailed } };
    }

    internal sealed class Operation(LlmCallScope scope, IPhysicalLlmInvocationLedger ledger)
    {
        private int _ordinal;
        public Guid Id { get; } = Guid.NewGuid();
        public LlmCallScope Scope { get; } = scope;
        public IPhysicalLlmInvocationLedger Ledger { get; } = ledger;
        public ConcurrentQueue<Observation> Observations { get; } = new();
        public bool PersistenceFailed { get; set; }
        public int NextCandidate() => Interlocked.Increment(ref _ordinal);
    }

    internal sealed class Candidate
    {
        public Candidate(Operation operation, string provider, StructuredLLMCompletionRequest request)
        {
            Operation = operation;
            Provider = provider;
            Model = request.Model;
            MaxOutputTokens = request.MaxOutputTokens;
            CredentialRedactor = new PersistenceSecretRedactor(new[] { request.Credential?.ApiKey, request.Credential?.BaseUrl }.OfType<string>());
            Ordinal = operation.NextCandidate();
            // Freeze the effective price table once per candidate. Unknown future model names stay unpriced;
            // settlement and late receipts never consult today's environment or a changed operator row.
            var prices = new Dictionary<string, ModelPrice>(AgentCostPricing.ResolveTable(), StringComparer.OrdinalIgnoreCase);
            if (operation.Scope.ModelPrices is { } rows)
            {
                if (rows.Count > 2048) throw new PhysicalLlmAccountingException("The physical accounting price snapshot exceeds its entry limit.");
                foreach (var (name, price) in rows)
                    if (AgentCostPricing.PriceFor(name, rows) is { } usable) prices[name] = usable;
            }
            Prices = prices;
            PricingSnapshotJson = JsonSerializer.Serialize(prices.Where(p => p.Key.Length <= 500 && Redact(p.Key) == p.Key).OrderBy(p => p.Key, StringComparer.Ordinal).ToDictionary(p => p.Key, p => p.Value));
            if (Encoding.UTF8.GetByteCount(PricingSnapshotJson) > 262144)
                throw new PhysicalLlmAccountingException("The physical accounting price snapshot exceeds its 256 KiB metadata limit.");
            PricingVersion = "frozen-price/v1:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(PricingSnapshotJson)));
        }

        public Guid Id { get; } = Guid.NewGuid();
        public Operation Operation { get; }
        public int Ordinal { get; }
        public string Provider { get; }
        public string Model { get; }
        public int? MaxOutputTokens { get; }
        public IReadOnlyDictionary<string, ModelPrice> Prices { get; }
        public string PricingSnapshotJson { get; }
        public string PricingVersion { get; }
        public PersistenceSecretRedactor CredentialRedactor { get; }
        public string? Redact(string? value) => CredentialRedactor.Redact(Operation.Scope.CaptureRedactor?.Redact(value).Value ?? value).Value;
    }

    internal sealed record Dispatch(Candidate Candidate, Func<JsonElement, ProviderEnvelope> ReadEnvelope);
    internal sealed record ProviderEnvelope(string? Model, LlmUsage Usage);
    internal sealed record Observation(Guid CandidateId, string? Model, LlmUsage Usage);
    private sealed class Restore(Action restore) : IDisposable { public void Dispose() => restore(); }
}
