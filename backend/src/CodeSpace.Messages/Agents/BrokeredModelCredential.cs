namespace CodeSpace.Messages.Agents;

/// <summary>
/// What a run's harness is given INSTEAD of the tenant's provider key: the address of a broker that fronts that key
/// for this one run, plus a per-run bearer that is only honoured while the run's lease is live.
///
/// <para>The three fields are the whole capability. <see cref="BaseUrl"/> is the run's own path on the worker's
/// in-process reverse proxy — it carries an unguessable per-run segment, so it is a route nothing holding the run id
/// can compute. <see cref="RunToken"/> is a 256-bit CSPRNG bearer minted for this attempt; it authenticates to the
/// BROKER and to nothing else, which is the point — sent to the provider directly it is refused, so a token that
/// leaks out of the sandbox buys an attacker no model spend once the lease lapses. <see cref="ExpiresAt"/> is when
/// the lease stops answering absent a renewal from the worker that still owns the run.</para>
///
/// <para>Never persisted alongside a key, because there is no key here to persist: the decrypted
/// <see cref="ResolvedModelCredential"/> stays in the broker's in-memory lease on the worker, and only this record
/// reaches the child's environment. (The token alone IS stamped on the run's durable handle — see
/// <c>SandboxHandle.ModelBrokerRunToken</c> — for the same narrow reason the MCP run token is: a re-attaching worker
/// must rebuild the same redactor.)</para>
/// </summary>
public sealed record BrokeredModelCredential(string BaseUrl, string RunToken, DateTimeOffset ExpiresAt);
