using CodeSpace.Messages.Enums;

namespace CodeSpace.Core.Persistence.Entities;

/// <summary>
/// A team-scoped LLM model credential — the API key (and optional base URL) an agent harness uses to
/// authenticate to a model provider. A DISTINCT entity from the git <see cref="Credential"/> on purpose:
/// a model key has no git host, so reusing <see cref="Credential"/> would force a fake
/// <c>ProviderInstance</c> and route through the git-only <c>AuthType</c> serializer. Only the encryption
/// primitive (<see cref="Services.Credentials.IPayloadEncryptor"/>) is shared — every other credential is
/// encrypted the same way.
///
/// <para><b>Injection model:</b> a workflow run never freezes the key — it freezes a <c>Guid</c> REFERENCE
/// to this row, which is decrypted just-in-time in the executor and injected into the sandboxed child's
/// environment, then discarded. So the secret lives only here (encrypted at rest) and transiently in the
/// child process env — never in <c>agent_run.task_json</c>, the event log, or any result.</para>
/// </summary>
public class ModelCredential : IEntity<Guid>, IAuditable
{
    public Guid Id { get; set; }

    public Guid TeamId { get; set; }

    /// <summary>
    /// Stable LLM provider tag, aligned with <c>ILLMProviderModule.Provider</c> ("Anthropic", "OpenAI",
    /// "OpenRouter", "Ollama", …). A string (not an enum) to match the LLM-module + harness/runner <c>Kind</c>
    /// conventions, so a new provider plugs in with no schema change AND the agent path can later converge
    /// with the in-process <c>llm.complete</c> path on one credential source.
    /// </summary>
    public string Provider { get; set; } = default!;

    public string DisplayName { get; set; } = default!;

    /// <summary>The API key encrypted via <see cref="Services.Credentials.IPayloadEncryptor"/>. NULL for a keyless provider (e.g. a local Ollama reached over <see cref="BaseUrl"/>).</summary>
    public string? EncryptedApiKey { get; set; }

    /// <summary>Non-secret base-URL override (OpenRouter / self-hosted gateway / Ollama). Plaintext on purpose — it's config, not a secret, so the UI can display/edit it without decrypting.</summary>
    public string? BaseUrl { get; set; }

    public CredentialStatus Status { get; set; } = CredentialStatus.Active;

    public DateTimeOffset CreatedDate { get; set; }
    public Guid CreatedBy { get; set; }
    public DateTimeOffset LastModifiedDate { get; set; }
    public Guid LastModifiedBy { get; set; }

    /// <summary>Soft-delete: a removed credential keeps run history intact and is treated as unresolvable at the just-in-time resolve. NULL = active.</summary>
    public DateTimeOffset? DeletedDate { get; set; }

    public Team Team { get; set; } = default!;

    /// <summary>
    /// The models this credential can run — the maintained list (operator-typed + provider-reflected). A
    /// credential predating the catalog has an EMPTY list, which keeps just-in-time resolution byte-identical:
    /// <see cref="Services.Agents.ModelCredentialResolver"/> never reads this navigation, so the pool it backs
    /// is additive metadata, not a gate on existing runs.
    /// </summary>
    public ICollection<ModelCredentialModel> Models { get; set; } = new List<ModelCredentialModel>();
}
