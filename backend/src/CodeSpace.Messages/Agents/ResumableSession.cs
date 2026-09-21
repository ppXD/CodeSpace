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
/// <param name="CheckpointAt">
/// When the transcript this session restores was CHECKPOINTED mid-run, because its attempt's host died before it
/// could finish — null for the ordinary case, where the transcript was captured by an attempt that completed.
///
/// <para>The two are not interchangeable and the difference is what the consumer owes the agent. A captured
/// transcript comes from an attempt that ran to its end with its workspace intact, so the only open question is
/// whether a branch was pushed. A checkpoint comes from an attempt whose machine is gone: the conversation may
/// describe turns the checkpoint never saw and edits the new sandbox does not contain, so only this one owes the
/// lost-tree sentence — and only this one may degrade to a cold start when its bytes turn out to be unreadable,
/// since a captured ref that cannot be read is a real fault.</para>
/// </param>
public sealed record ResumableSession(Guid AgentRunId, string SessionId, string? InlineTranscript, Guid? TranscriptArtifactId, DateTimeOffset? CheckpointAt = null);
