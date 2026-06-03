import { useState } from "react";

import { Ic } from "@/_imported/ai-code-space/icons";
import { COMMENT_RESPONSE_KEY } from "@/api/chat";
import type { FormComponent, InteractionButton, InteractionButtonStyle, InteractionResponse, MessageInteractionView } from "@/api/chat";
import type { TeamMemberSummary } from "@/api/teams";
import { useActorIdentityGate } from "@/components/identities/ActorIdentityGate";
import { avatarColor } from "@/lib/avatarColor";
import { parseActorRepoPermissionDenied } from "@/lib/actorIdentity";
import { useRespondToMessage } from "@/hooks/use-chat";
import { SchemaForm } from "@/components/workflows/SchemaForm";

type RespondVars = { messageId: string; responseKey: string; comment: string | null; values?: Record<string, unknown> | null };

/** Button visual emphasis → the app's shared button classes (warm Claude theme). */
const STYLE_CLASS: Record<InteractionButtonStyle, string> = {
  Default: "btn",
  Primary: "btn btn-primary",
  Danger: "btn btn-danger",
};

/**
 * The interactive component attached to a message — a row of action buttons or a form. An Open card's
 * controls resolve the parked workflow wait via the respond endpoint (keyed by message id — the wait
 * token never reaches the client): a button click sends its key (+ a comment when required); a form
 * submit sends the field values, which the run reads as the wait node's outputs.values. A Resolved /
 * Expired card shows the outcome stamp instead. On success the message refetches and re-renders settled.
 */
export function MessageInteractionCard({ interaction, members, conversationId, messageId, myUserId }: {
  interaction: MessageInteractionView;
  members: Map<string, TeamMemberSummary>;
  conversationId: string;
  messageId: string;
  myUserId: string | null;
}) {
  const respond = useRespondToMessage(conversationId);
  const gate = useActorIdentityGate();
  const [commenting, setCommenting] = useState<InteractionButton | null>(null);
  const [comment, setComment] = useState("");
  const [draftComment, setDraftComment] = useState("");
  const [permissionError, setPermissionError] = useState<string | null>(null);

  // A non-terminal comment — open to ANY conversation member, never resolves the card (the backend gates it
  // on membership, not the act-as-user identity), so it skips the identity gate entirely.
  const submitComment = () => {
    const text = draftComment.trim();
    if (text === "") return;
    respond.mutate({ messageId, responseKey: COMMENT_RESPONSE_KEY, comment: text }, { onSuccess: () => setDraftComment("") });
  };

  // Respond, branching on the backend's two pre-flight refusals (both thrown BEFORE the wait resolves,
  // so the card stays open and nothing fails in the background):
  //   • 428 actor_identity_required → the responder hasn't LINKED an identity → the gate opens the link
  //     modal and, on success, retries this exact response.
  //   • 403 actor_repo_permission_denied → they're linked but lack repo access → nothing to link, so show
  //     the reason inline on the card.
  const submit = (vars: RespondVars) => {
    setPermissionError(null);
    respond.mutate(vars, {
      onError: e => {
        const denied = parseActorRepoPermissionDenied(e);
        if (denied) { setPermissionError(denied.message); return; }
        gate.prompt(e, () => submit(vars));
      },
    });
  };

  if (interaction.state !== "Open") {
    return (
      <div className="chat-card" data-state={interaction.state}>
        <InteractionTimeline interaction={interaction} members={members} />
        <ResolutionStamp interaction={interaction} members={members} />
      </div>
    );
  }

  const allowed = interaction.allowedResponderUserIds;
  const canRespond = allowed == null || (myUserId != null && allowed.includes(myUserId));
  const pending = respond.isPending;
  const component = interaction.component;

  const onButtonClick = (button: InteractionButton) => {
    if (button.requiresComment) {
      setComment("");
      setCommenting(button);
      return;
    }

    submit({ messageId, responseKey: button.key, comment: null });
  };

  return (
    <div className="chat-card" data-state="Open">
      <InteractionTimeline interaction={interaction} members={members} />
      <QuorumTally interaction={interaction} />

      {component.kind === "form" ? (
        <FormBody
          component={component}
          disabled={pending || !canRespond}
          onSubmit={values => submit({ messageId, responseKey: "submit", comment: null, values })}
        />
      ) : commenting ? (
        <div className="chat-card-comment">
          <textarea
            className="chat-card-comment-input"
            value={comment}
            onChange={e => setComment(e.target.value)}
            placeholder={`Add a comment for “${commenting.label}”`}
            autoFocus
          />
          <div className="chat-card-comment-actions">
            <button type="button" className="btn btn-ghost" onClick={() => setCommenting(null)} disabled={pending}>Cancel</button>
            <button type="button" className={STYLE_CLASS[commenting.style]} onClick={() => submit({ messageId, responseKey: commenting.key, comment: comment.trim() })} disabled={pending || comment.trim() === ""}>{commenting.label}</button>
          </div>
        </div>
      ) : (
        <div className="chat-card-actions">
          {component.buttons.map(b => (
            <button key={b.key} type="button" title={b.description ?? undefined} className={STYLE_CLASS[b.style]} onClick={() => onButtonClick(b)} disabled={pending || !canRespond}>{b.label}</button>
          ))}
        </div>
      )}

      {permissionError && <div className="chat-card-error" role="alert"><Ic.Triangle size={12} /> {permissionError}</div>}

      {!canRespond && <span className="chat-card-hint">Only the requested reviewer can decide — anyone here can still comment.</span>}

      {/* Non-terminal discussion: any member can add to the thread; the card stays open for the decision.
          Hidden during the requires-comment flow (that composer is taking over the input). */}
      {!commenting && (
        <div className="chat-card-commentbox">
          <input
            className="chat-card-commentbox-input"
            value={draftComment}
            onChange={e => setDraftComment(e.target.value)}
            onKeyDown={e => { if (e.key === "Enter") { e.preventDefault(); submitComment(); } }}
            placeholder="Add a comment…"
            disabled={pending}
            aria-label="Add a comment"
          />
          <button type="button" className="btn btn-ghost" onClick={submitComment} disabled={pending || draftComment.trim() === ""}>Comment</button>
        </div>
      )}
    </div>
  );
}

/** The append-only collaboration timeline — every comment and action click, in order (who / what). */
function InteractionTimeline({ interaction, members }: { interaction: MessageInteractionView; members: Map<string, TeamMemberSummary> }) {
  if (interaction.responses.length === 0) return null;

  return (
    <ul className="chat-card-log">
      {interaction.responses.map((r, i) => {
        const name = members.get(r.byUserId)?.name ?? "Unknown";
        const color = avatarColor(r.byUserId);

        return (
          <li key={i} className="chat-card-log-row" data-kind={r.kind === "Comment" ? "comment" : "action"}>
            <span className="chat-card-log-av" style={{ background: color.bg, color: color.fg }}>{name.charAt(0).toUpperCase()}</span>
            <span className="chat-card-log-name">{name}</span>
            <span className="chat-card-log-what">{describeResponse(r, interaction)}</span>
          </li>
        );
      })}
    </ul>
  );
}

/** One log entry → human text: a comment shows its text; an action shows the button's label (+ any comment). */
function describeResponse(r: InteractionResponse, interaction: MessageInteractionView): string {
  if (r.kind === "Comment") return r.comment ?? "";

  const label = interaction.component.kind === "action_buttons"
    ? interaction.component.buttons.find(b => b.key === r.key)?.label ?? r.key ?? ""
    : "submitted";

  return r.comment ? `${label} — ${r.comment}` : label;
}

/** Live "N / count approved" progress for a quorum card (the leading action key's distinct, deduped responders). */
function QuorumTally({ interaction }: { interaction: MessageInteractionView }) {
  if (interaction.resolve.kind !== "Quorum") return null;

  return <div className="chat-card-tally"><Ic.Users size={11} /> {countLeadingApprovals(interaction)} / {interaction.resolve.count} approved</div>;
}

/** Distinct responders (last-vote-wins per person) for the most-supported terminal, non-veto action key. */
function countLeadingApprovals(interaction: MessageInteractionView): number {
  if (interaction.component.kind !== "action_buttons") return 0;

  const terminal = new Set(interaction.component.buttons.filter(b => (b.resolvesWait ?? true) && !b.vetoes).map(b => b.key));

  const latestByResponder = new Map<string, string>();
  for (const r of interaction.responses)
    if (r.kind === "Action" && r.key && terminal.has(r.key)) latestByResponder.set(r.byUserId, r.key);

  const perKey = new Map<string, number>();
  for (const key of latestByResponder.values()) perKey.set(key, (perKey.get(key) ?? 0) + 1);

  return perKey.size === 0 ? 0 : Math.max(...perKey.values());
}

/** Renders a form card's fields (via the shared schema-driven form) + a submit button. Owns the draft value. */
function FormBody({ component, disabled, onSubmit }: { component: FormComponent; disabled: boolean; onSubmit: (values: Record<string, unknown>) => void }) {
  const [value, setValue] = useState<Record<string, unknown>>({});
  const missing = missingRequired(component.fields, value);

  return (
    <div className="chat-card-form">
      <SchemaForm schema={component.fields} value={value} onChange={setValue} />
      <div className="chat-card-form-actions">
        <button type="button" className="btn btn-primary" onClick={() => onSubmit(value)} disabled={disabled || missing.length > 0}>{component.submitLabel || "Submit"}</button>
      </div>
    </div>
  );
}

/** The outcome line on a settled card: the chosen action / submitted form, who responded, any comment. */
function ResolutionStamp({ interaction, members }: { interaction: MessageInteractionView; members: Map<string, TeamMemberSummary> }) {
  const resolution = interaction.resolution;

  if (resolution == null) return <span className="chat-card-stamp chat-card-stamp-muted">Expired</span>;

  const label = interaction.component.kind === "action_buttons"
    ? interaction.component.buttons.find(b => b.key === resolution.responseKey)?.label ?? resolution.responseKey
    : "Submitted";
  const by = members.get(resolution.byUserId)?.name ?? "Unknown";

  return (
    <div className="chat-card-stamp">
      <Ic.Check size={13} />
      <span className="chat-card-stamp-label">{label}</span>
      <span className="chat-card-stamp-by">by {by}</span>
      {resolution.comment && <span className="chat-card-stamp-comment">“{resolution.comment}”</span>}
      {resolution.values && <span className="chat-card-stamp-comment">{summarizeValues(resolution.values)}</span>}
    </div>
  );
}

/** Required field names (per the form's JSON Schema) that are absent / empty in the draft. */
function missingRequired(fields: Record<string, unknown>, value: Record<string, unknown>): string[] {
  const required = Array.isArray((fields as { required?: unknown }).required) ? (fields as { required: string[] }).required : [];

  return required.filter(name => {
    const v = value[name];
    return v == null || (typeof v === "string" && v.trim() === "") || (Array.isArray(v) && v.length === 0);
  });
}

/** Compact "k: v · k: v" summary of a resolved form's submitted values. */
function summarizeValues(values: Record<string, unknown>): string {
  return Object.entries(values).map(([k, v]) => `${k}: ${String(v)}`).join(" · ");
}
