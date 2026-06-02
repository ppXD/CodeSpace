import { useState } from "react";

import { Ic } from "@/_imported/ai-code-space/icons";
import type { FormComponent, InteractionButton, InteractionButtonStyle, MessageInteractionView } from "@/api/chat";
import type { TeamMemberSummary } from "@/api/teams";
import { useActorIdentityGate } from "@/components/identities/ActorIdentityGate";
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

  // Respond, routing a 428 actor_identity_required to the global gate: the run will act AS the
  // responder (e.g. a downstream git.pr_review), so if they haven't linked an identity the backend
  // refuses BEFORE resolving the wait. The gate opens the link modal and, on success, retries this
  // exact response — so the click succeeds after linking instead of the run failing in the background.
  const submit = (vars: RespondVars) => respond.mutate(vars, { onError: e => { gate.prompt(e, () => submit(vars)); } });

  if (interaction.state !== "Open") {
    return (
      <div className="chat-card" data-state={interaction.state}>
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

      {!canRespond && <span className="chat-card-hint">Only the requested reviewer can respond.</span>}
    </div>
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
