namespace CodeSpace.Messages.Agents;

/// <summary>
/// A prior agent run's RESUMABLE session, resolved from the fork lineage by the CONTINUE producer: the harness-native
/// session id to <c>--resume</c>, plus its captured transcript as EITHER inline bytes (a small prior transcript) OR an
/// artifact-store reference (a large one, kept out of task_jsonb). At least one transcript form is always set — the
/// producer never reports a session id WITHOUT a transcript, since a resume with no transcript would fail
/// ("No conversation found"); a bytes-less prior session yields null instead, and the continue cold-starts.
/// <c>AgentRunId</c> is the OWNING agent run — a caller resolving this same attempt's world-state (git ref) must key
/// off THIS id, never a separately-resolved "latest attempt" id, so a resume hint's honesty claim about git state
/// always describes the SAME attempt whose conversation it restores.
/// </summary>
public sealed record ResumableSession(Guid AgentRunId, string SessionId, string? InlineTranscript, Guid? TranscriptArtifactId);
