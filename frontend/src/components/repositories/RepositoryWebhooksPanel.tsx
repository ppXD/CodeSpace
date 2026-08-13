import { Fragment, useState } from "react";

import { Ic } from "@/_imported/ai-code-space/icons";
import { ApiError } from "@/api/request";
import type { ProviderKind, RepositoryWebhookAttemptDetail, RepositoryWebhookDetail } from "@/api/types";
import { useRepositoryWebhooks, useRetryWebhookRegistration, useRevealWebhookSecret } from "@/hooks/use-repository-webhooks";
import { TeamPermissions, useTeamPermissions } from "@/hooks/use-team-management";
import { attemptOutcome, canRetryWebhook, webhookDiagnosis, webhookSetupSteps, webhookState, type WebhookSetupStep } from "@/lib/webhookState";

/**
 * Repository → Webhook. What the repository's hooks are doing, why they are not, and how to finish
 * one by hand when the automatic registration cannot.
 *
 * <p>Overview counts only Registered hooks, so a repository whose one hook is dead-lettered reads
 * there exactly like a repository that never had one. This tab is that distinction — and it says it
 * in the operator's terms, because "DeadLettered" describes where a job ended up and the question
 * being asked is whether events are arriving.</p>
 *
 * <p>Provider and fullPath are props rather than re-resolved here: every word on the page depends on
 * which provider this is, and a component that guessed while its lookup was in flight would render
 * GitLab's field labels to somebody looking at GitHub.</p>
 */
interface RepositoryWebhooksPanelProps {
  repositoryId: string;
  fullPath: string;
  provider: ProviderKind;
}

export function RepositoryWebhooksPanel({ repositoryId, fullPath, provider }: RepositoryWebhooksPanelProps) {
  const webhooks = useRepositoryWebhooks(repositoryId);
  const mayManage = useTeamPermissions().can(TeamPermissions.ReposManage);

  const hooks = webhooks.data ?? [];

  return (
    <div style={{ margin: "16px 0 28px", display: "flex", flexDirection: "column", gap: 10 }}>
      <div className="cn-field-h" style={{ maxWidth: "56em" }}>
        CodeSpace registers these itself when a repository is bound. Nothing here needs setting up by
        hand until one of them fails to register — and then this is where the reason is.
      </div>

      {webhooks.isLoading && <div className="ct-empty"><div className="ct-empty-h">Loading…</div></div>}

      {webhooks.error instanceof ApiError && (
        <div className="cn-banner cn-banner-err">
          <div className="cn-banner-h">Couldn't load the webhooks</div>
          <div className="cn-banner-p">{webhooks.error.message}</div>
        </div>
      )}

      {!webhooks.isLoading && hooks.length === 0 && (
        <div className="ct-empty">
          <div className="ct-empty-h">This repository has no webhook</div>
          <div className="ct-empty-p">Nothing will arrive from {provider} until one exists. Binding the repository again creates one.</div>
        </div>
      )}

      {hooks.length > 0 && (
        <div className="cn-list">
          {hooks.map((hook) => <WebhookRow key={hook.id} hook={hook} repositoryId={repositoryId} fullPath={fullPath} provider={provider} mayManage={mayManage} />)}
        </div>
      )}
    </div>
  );
}

interface WebhookRowProps extends RepositoryWebhooksPanelProps {
  hook: RepositoryWebhookDetail;
  mayManage: boolean;
}

function WebhookRow({ hook, repositoryId, fullPath, provider, mayManage }: WebhookRowProps) {
  const [open, setOpen] = useState(false);
  const retry = useRetryWebhookRegistration(repositoryId);

  const state = webhookState(hook, provider);
  const diagnosis = webhookDiagnosis(hook, provider);
  const retryable = mayManage && canRetryWebhook(hook);

  return (
    <div className="cn-row hk-row" data-tone={state.tone}>
      <div className="cn-row-head">
        <div className="cn-mark" data-p={provider.toLowerCase()}><Ic.Bell size={14} /></div>

        {/* No inline flex here: `.hk-row .cn-meta` gives it a 320px basis so the actions drop to
            their own line in a narrow pane instead of squeezing the sentence out of the row. */}
        <div className="cn-meta">
          <div className="cn-name">
            {eventsLabel(hook.subscribedEvents)}
            {/* `idle` takes `.cn-status` bare — its neutral grey is already the right answer, and a
                modifier class with no rule behind it would only look like one that got lost. */}
            <span className={state.tone === "idle" ? "cn-status" : `cn-status hk-status-${state.tone}`}><span className="cn-status-dot" />{state.label}</span>
          </div>
          <div className="cn-sub"><span>{state.detail}</span></div>
        </div>

        {retryable && (
          <button className="btn" disabled={retry.isPending} onClick={() => retry.mutate(hook.id)}>
            <Ic.Sync size={12} /> {retry.isPending ? "Queueing…" : "Retry now"}
          </button>
        )}

        {/* One control, and its label is the question the row's state makes the reader ask. */}
        <button className={state.tone === "bad" ? "btn btn-primary" : "btn"} aria-expanded={open} onClick={() => setOpen(!open)}>
          {state.tone === "bad" ? "Why, and how to fix it" : open ? "Hide details" : "Details"}
        </button>
      </div>

      {retry.error instanceof ApiError && <div className="cn-sub" style={{ color: "var(--danger)", paddingLeft: 46 }}>{retry.error.message}</div>}

      {open && (
        <div className="hk-open">
          {diagnosis && <WebhookDiagnosisCard hook={hook} cause={diagnosis.cause} pattern={diagnosis.pattern} />}
          <WebhookManualSetup hook={hook} repositoryId={repositoryId} fullPath={fullPath} provider={provider} mayManage={mayManage} />
        </div>
      )}
    </div>
  );
}

/**
 * Why it failed: the sentence, the shape of the run, then the raw exchange underneath. In that order
 * on purpose — a reader who already knows what a 403 on hook creation means can skip straight to the
 * request, and one who does not is told rather than left to infer it.
 */
function WebhookDiagnosisCard({ hook, cause, pattern }: { hook: RepositoryWebhookDetail; cause: string; pattern: string }) {
  const newest = hook.attemptTimeline.at(-1);

  return (
    <div className="hk-diag">
      <div className="hk-diag-h">
        <div className="hk-diag-cause">{cause}</div>
        {pattern && <div className="hk-diag-pattern">{pattern}</div>}
      </div>

      {newest && <AttemptExchange attempt={newest} />}

      {hook.attemptTimeline.length > 0 && (
        <>
          <div className="hk-pre-h">{hook.attemptTimeline.length === 1 ? "The attempt" : `All ${hook.attemptTimeline.length} attempts`}</div>
          {hook.attemptTimeline.map((attempt) => <AttemptTimelineRow key={`${attempt.attemptNumber}-${attempt.attemptedAt}`} attempt={attempt} />)}
        </>
      )}
    </div>
  );
}

/** The request we sent and the answer we got, verbatim. Everything shown was masked at capture. */
function AttemptExchange({ attempt }: { attempt: RepositoryWebhookAttemptDetail }) {
  const request = formatRequest(attempt);

  return (
    <>
      {request && (
        <>
          <div className="hk-pre-h">What we sent</div>
          <pre className="hk-pre">{request}</pre>
        </>
      )}

      <div className="hk-pre-h">What came back</div>
      <pre className="hk-pre">
        {attempt.statusCode == null
          // The absence of a code IS the diagnosis, so it is stated as a finding rather than left as
          // an empty line the reader has to interpret.
          ? <><span className="hk-noanswer">No answer at all — the request never reached an HTTP response.</span>{`\n\n${attempt.error}`}</>
          : <>{`${attempt.statusCode}\n\n${attempt.responseBody ?? attempt.error}`}</>}
      </pre>
    </>
  );
}

function AttemptTimelineRow({ attempt }: { attempt: RepositoryWebhookAttemptDetail }) {
  return (
    <div className="hk-tl-row">
      <span className="hk-tl-n">#{attempt.attemptNumber}</span>
      <span className="hk-tl-at">{formatClock(attempt.attemptedAt)}</span>
      <span className="hk-tl-code">{attemptOutcome(attempt)}</span>
      <span className="hk-tl-err" title={attempt.error}>{attempt.error}</span>
    </div>
  );
}

/**
 * Finish it by hand. The steps quote the provider's own field labels because those are the words on
 * the screen the reader is looking at while they read this one, and the last step closes the loop:
 * send a test event, and the row above answers by itself.
 */
function WebhookManualSetup({ hook, repositoryId, fullPath, provider, mayManage }: WebhookRowProps) {
  const steps = webhookSetupSteps(provider, fullPath);

  return (
    <div>
      <div className="hk-open-h" style={{ marginBottom: 10 }}>Set it up by hand at {provider}</div>

      <div className="hk-steps">
        {steps.map((step, index) => (
          <div className="hk-step" key={index}>
            <span className="hk-step-n">{index + 1} ·</span>
            <div className="hk-step-b">
              <SetupStepBody step={step} />
              {step.kind === "paste" && step.into === "url" && <CopyableValue value={hook.callbackUrl} />}
              {step.kind === "paste" && step.into === "secret" && <SecretValue webhookId={hook.id} repositoryId={repositoryId} mayManage={mayManage} />}
            </div>
          </div>
        ))}
      </div>
    </div>
  );
}

function SetupStepBody({ step }: { step: WebhookSetupStep }) {
  if (step.kind === "paste") return <span>Paste this into <strong>{step.label}</strong></span>;

  return <span>{emphasise(step.text)}</span>;
}

function CopyableValue({ value }: { value: string }) {
  return (
    <div className="hk-val">
      <code>{value}</code>
      <div className="hk-val-a"><CopyButton value={value} label="Copy the callback URL" /></div>
    </div>
  );
}

/**
 * Masked until asked for. The plaintext arrives on its own endpoint, so simply opening this tab never
 * puts on the wire the one value that can forge a delivery this repository would accept — and the
 * copy button appears only once there is something the operator has actually seen to copy.
 */
function SecretValue({ webhookId, repositoryId, mayManage }: { webhookId: string; repositoryId: string; mayManage: boolean }) {
  const reveal = useRevealWebhookSecret(repositoryId);
  const [secret, setSecret] = useState<string | null>(null);

  const hide = () => { setSecret(null); reveal.reset(); };

  return (
    <>
      <div className="hk-val">
        <code className={secret == null ? "hk-val-mask" : undefined}>{secret ?? "•".repeat(32)}</code>
        <div className="hk-val-a">
          {/* Absent rather than present-and-refusing: a Member can read the whole diagnosis, and the
              secret is the one part of it that is an Admin's to hand over. */}
          {mayManage && (
            secret == null
              ? <button className="btn" disabled={reveal.isPending} onClick={async () => setSecret((await reveal.mutateAsync(webhookId)).secret)}>
                  <Ic.Eye size={12} /> {reveal.isPending ? "Revealing…" : "Reveal"}
                </button>
              : <>
                  <button className="btn" onClick={hide}><Ic.EyeOff size={12} /> Hide</button>
                  <CopyButton value={secret} label="Copy the signing secret" />
                </>
          )}
        </div>
      </div>

      {!mayManage && <div className="cn-field-h">Only an admin can reveal the signing secret.</div>}
      {reveal.error instanceof ApiError && <div className="cn-field-h" style={{ color: "var(--danger)" }}>{reveal.error.message}</div>}
    </>
  );
}

function CopyButton({ value, label }: { value: string; label: string }) {
  const [copied, setCopied] = useState(false);

  return (
    <button
      className="btn"
      aria-label={label}
      onClick={async () => { await navigator.clipboard.writeText(value); setCopied(true); }}
    >
      {copied ? <><Ic.Check size={12} /> Copied</> : <><Ic.Copy size={12} /> Copy</>}
    </button>
  );
}

/**
 * `**like this**` becomes a `<strong>`. The marked phrases are the provider's own words — a field
 * label, a menu path, a checkbox — and setting them apart is what lets the reader match them against
 * the other window without reading the sentence twice.
 */
function emphasise(text: string) {
  return text.split("**").map((part, index) => (
    index % 2 === 1 ? <strong key={index}>{part}</strong> : <Fragment key={index}>{part}</Fragment>
  ));
}

/** The provider's request as it went out. Empty when the attempt predates request capture. */
function formatRequest(attempt: RepositoryWebhookAttemptDetail): string {
  if (!attempt.requestUrl) return "";

  const start = `${attempt.requestMethod ?? "POST"} ${attempt.requestUrl}`;
  const headers = parseHeaders(attempt.requestHeadersJson).map(([name, value]) => `${name}: ${value}`);

  return [start, ...headers, ...(attempt.requestBody ? ["", attempt.requestBody] : [])].join("\n");
}

/** Stored as a JSON object in a string, so it is parsed here rather than re-serialized by the server. */
function parseHeaders(json: string | null): Array<[string, string]> {
  if (!json) return [];

  try {
    const parsed: unknown = JSON.parse(json);

    if (parsed == null || typeof parsed !== "object" || Array.isArray(parsed)) return [];

    return Object.entries(parsed as Record<string, unknown>).map(([name, value]) => [name, String(value)]);
  } catch {
    // A header record we cannot read is not worth failing the whole diagnosis over — the request
    // line and the body are still the useful part.
    return [];
  }
}

/** Absolute local time on the timeline: relative ages hide the gaps that the backoff ladder puts there. */
function formatClock(iso: string): string {
  const at = new Date(iso);

  return Number.isNaN(at.getTime()) ? iso : at.toLocaleString();
}

/** What the hook subscribes to, as a name — "Push and Merge request events" beats a bare uuid. */
function eventsLabel(events: readonly string[]): string {
  return events.length === 0 ? "Webhook" : events.join(", ");
}
