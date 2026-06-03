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
        <VoteStanding interaction={interaction} members={members} />
        <CommentThread interaction={interaction} members={members} />
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
      <VoteStanding interaction={interaction} members={members} />
      <CommentThread interaction={interaction} members={members} />

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

/**
 * Each responder's CURRENT terminal vote — their last terminal action (last-wins, so a changed vote counts
 * once), with the button's label + any attached comment. Mirrors the backend's CurrentTerminalVotes, so the
 * card's standing matches exactly what resolves the wait. Comments and non-terminal acks aren't votes here.
 */
function currentTerminalVotes(interaction: MessageInteractionView): Array<{ byUserId: string; key: string; label: string; comment: string | null }> {
  if (interaction.component.kind !== "action_buttons") return [];

  const buttons = interaction.component.buttons;
  const isTerminal = (key: string) => { const b = buttons.find(x => x.key === key); return b != null && (b.resolvesWait ?? true); };

  const latest = new Map<string, InteractionResponse>();
  for (const r of interaction.responses)
    if (r.kind === "Action" && r.key != null && isTerminal(r.key)) latest.set(r.byUserId, r);

  return [...latest.values()].map(r => {
    const key = r.key as string;
    return { byUserId: r.byUserId, key, label: buttons.find(x => x.key === key)?.label ?? key, comment: r.comment };
  });
}

/**
 * The decision standing — one row per responder showing their CURRENT vote (last-wins), plus a per-option
 * summary built from the buttons' OWN labels. Deliberately generic: it never prints a hardcoded verb like
 * "approved" — the only human text is the author's labels — and it makes last-wins visible (a reviewer who
 * switched Approve → Request changes shows only their current vote, so the counts always add up).
 */
function VoteStanding({ interaction, members }: { interaction: MessageInteractionView; members: Map<string, TeamMemberSummary> }) {
  const votes = currentTerminalVotes(interaction);
  const quorum = interaction.resolve.kind === "Quorum" ? interaction.resolve.count : null;

  if (votes.length === 0)
    return quorum != null ? <div className="chat-card-standing-hint"><Ic.Users size={11} /> Needs {quorum} of one option to decide</div> : null;

  const perOption = new Map<string, { label: string; count: number }>();
  for (const v of votes) {
    const entry = perOption.get(v.key);
    if (entry) entry.count += 1; else perOption.set(v.key, { label: v.label, count: 1 });
  }

  return (
    <div className="chat-card-standing">
      <div className="chat-card-standing-head">
        <Ic.Users size={11} />
        <span className="chat-card-standing-rule">{quorum != null ? `Needs ${quorum} of one option` : "Current votes"}</span>
        {[...perOption.values()].map(o => <span key={o.label} className="chat-card-standing-chip">{o.label} {o.count}</span>)}
      </div>
      <ul className="chat-card-standing-list">
        {votes.map(v => {
          const name = members.get(v.byUserId)?.name ?? "Unknown";
          const color = avatarColor(v.byUserId);

          return (
            <li key={v.byUserId} className="chat-card-log-row" data-kind="vote">
              <span className="chat-card-log-av" style={{ background: color.bg, color: color.fg }}>{name.charAt(0).toUpperCase()}</span>
              <span className="chat-card-log-name">{name}</span>
              <span className="chat-card-log-what">{v.comment ? `${v.label} — ${v.comment}` : v.label}</span>
            </li>
          );
        })}
      </ul>
    </div>
  );
}

/** The discussion thread — non-terminal comments only (decisions live in the standing above). */
function CommentThread({ interaction, members }: { interaction: MessageInteractionView; members: Map<string, TeamMemberSummary> }) {
  const comments = interaction.responses.filter(r => r.kind === "Comment");

  if (comments.length === 0) return null;

  return (
    <ul className="chat-card-log">
      {comments.map((r, i) => {
        const name = members.get(r.byUserId)?.name ?? "Unknown";
        const color = avatarColor(r.byUserId);

        return (
          <li key={i} className="chat-card-log-row" data-kind="comment">
            <span className="chat-card-log-av" style={{ background: color.bg, color: color.fg }}>{name.charAt(0).toUpperCase()}</span>
            <span className="chat-card-log-name">{name}</span>
            <span className="chat-card-log-what">{r.comment}</span>
          </li>
        );
      })}
    </ul>
  );
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
