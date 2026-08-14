import type { ProviderKind, RejectionReason, RepositoryWebhookAttemptDetail, RepositoryWebhookDetail } from "@/api/types";
import { relativeTime } from "./codeTree";

/**
 * What the Webhook tab says, as opposed to what the database stores.
 *
 * <p>The registration status is the queue's vocabulary — "DeadLettered" describes where a job ended
 * up, and the person reading the page is asking something else entirely: are events arriving. So
 * seven statuses collapse into three answers to that question, and nothing on the page renders a
 * status name.</p>
 *
 * <p>The tone travels with the label rather than replacing it. A list of forty repositories has to be
 * scannable, which is what the colour is for, and colour cannot be the only signal, which is what the
 * label is for.</p>
 */
export type WebhookTone = "good" | "work" | "bad" | "idle";

export interface WebhookState {
  tone: WebhookTone;
  label: string;
  /** The line under the name — this row's own terms, not a restatement of the label. */
  detail: string;
}

export function webhookState(hook: RepositoryWebhookDetail, provider: ProviderKind, now: number = Date.now()): WebhookState {
  // Disabled here beats every registration state: the hook may be perfectly registered and the
  // provider may be delivering to it, and every one of those deliveries is thrown away on arrival.
  // Reading "Delivering" off a Registered row would be the page's worst possible lie.
  if (!hook.active) return { tone: "bad", label: "Not delivering", detail: `Turned off here. ${providerName(provider)} still sends events and every one of them is rejected on arrival.` };

  switch (hook.registrationStatus) {
    case "Registered": return deliveringState(hook, provider, now);
    case "DeadLettered": return stoppedState(hook, now);
    case "Cancelled": return { tone: "idle", label: "Not delivering", detail: "Registration was abandoned before it finished — this hook was never created at the provider." };
    default: return registeringState(hook, now);
  }
}

function deliveringState(hook: RepositoryWebhookDetail, provider: ProviderKind, now: number): WebhookState {
  const hookId = hook.externalId ? `${providerName(provider)} hook ${formatHookId(hook.externalId)}` : `Registered at ${providerName(provider)}`;
  // "Registered but nothing has ever arrived" is the single most common way a hook is quietly broken
  // — the provider accepted it and cannot reach us — so the row has to say so rather than fall silent.
  const delivery = hook.lastReceivedDate ? `last delivery ${relativeTime(hook.lastReceivedDate, now)}` : "no event has arrived yet";

  return { tone: "good", label: "Delivering", detail: `${hookId} · ${delivery}` };
}

function registeringState(hook: RepositoryWebhookDetail, now: number): WebhookState {
  const attempt = hook.attempts + 1;

  if (hook.registrationStatus === "Registering") return { tone: "work", label: "Registering", detail: `Attempt ${attempt} is in flight.` };

  const detail = hook.attempts === 0
    ? "First attempt is queued."
    : `Attempt ${attempt} · next try ${inWords(new Date(hook.nextAttemptAt).getTime() - now)}`;

  return { tone: "work", label: "Registering", detail };
}

function stoppedState(hook: RepositoryWebhookDetail, now: number): WebhookState {
  const gaveUp = hook.attempts > 0 ? `Gave up after ${hook.attempts} attempts` : "Stopped retrying";
  // A hook can be dead-lettered on registration and still have received events — someone added it by
  // hand at the provider. Saying "nothing has arrived" there would send the reader hunting a fault
  // that was already fixed.
  const delivery = hook.lastReceivedDate ? `last delivery ${relativeTime(hook.lastReceivedDate, now)}` : "nothing has arrived at all";

  return { tone: "bad", label: "Not delivering", detail: `${gaveUp} · ${delivery}` };
}

/**
 * Whether the row can be handed back to the dispatcher. Mirrors the server's own rule so the button
 * is absent rather than present-and-refusing — the 400 names the actual state, but a reader should
 * not have to press something to be told it was never available.
 */
export function canRetryWebhook(hook: RepositoryWebhookDetail): boolean {
  return hook.registrationStatus === "Failed" || hook.registrationStatus === "DeadLettered";
}

export interface WebhookDiagnosis {
  /** One sentence naming the likely cause, in the reader's terms rather than the provider's. */
  cause: string;
  /** What the shape of the whole run says, which no single attempt can. */
  pattern: string;
}

/**
 * Why it failed. Null when nothing has — an unremarkable hook has no diagnosis to give.
 *
 * <p>Reads the NEWEST attempt for the cause and the whole run for the pattern, because they answer
 * different questions: the last answer says what to change, and the shape says whether changing it is
 * the fix. Ten straight 403s is a permission problem; nine timeouts and then a 403 is a network that
 * came back and a permission problem underneath it.</p>
 */
export function webhookDiagnosis(hook: RepositoryWebhookDetail, provider: ProviderKind): WebhookDiagnosis | null {
  const newest = hook.attemptTimeline.at(-1);

  // Anything that failed before the attempt table existed has only `lastError` to its name. Rendering
  // it verbatim is worse than nothing without saying that it IS the whole record.
  if (!newest) return hook.lastError ? { cause: hook.lastError, pattern: "This is the only account kept of that failure — it happened before per-attempt records were stored." } : null;

  return { cause: causeOf(newest, provider), pattern: attemptPattern(hook.attemptTimeline) };
}

function causeOf(attempt: RepositoryWebhookAttemptDetail, provider: ProviderKind): string {
  const who = providerName(provider);

  // No status code at all is not a missing field to render blank — it is the diagnosis. The request
  // was made and nothing on the other end answered it.
  if (attempt.statusCode == null) return `The call never got an answer — no HTTP response at all. ${who} was unreachable from here: a timeout, a DNS failure, or a refused connection.`;

  return STATUS_CAUSE[attempt.statusCode]?.(who, provider)
    ?? (attempt.statusCode >= 500 ? `${who} failed on its own side with ${attempt.statusCode}. Nothing here needs changing — this one is theirs.` : `${who} refused the call with ${attempt.statusCode}: ${attempt.error}`);
}

/**
 * The sentence a status code is worth. Each one names the thing to go and change, because a reader who
 * is told "403" has to already know that GitLab grades hook creation at Maintainer to get anywhere.
 */
const STATUS_CAUSE: Record<number, (who: string, provider: ProviderKind) => string> = {
  401: (who) => `${who} rejected the token outright. It has expired, been revoked, or never belonged to this connection.`,
  403: (who, provider) => `${who} refused to create the hook. ${HOOK_RIGHTS[provider]} — the token on this connection has less than that.`,
  404: (who) => `${who} says the repository is not there. A token that cannot see a repository gets the same answer as one that does not exist, so this is nearly always scope rather than a typo.`,
  422: (who, provider) => provider === "GitHub"
    ? `${who} rejected the hook's settings. It answers 422 both when the callback URL is not reachable from the public internet and when a hook with that URL already exists on the repository.`
    : `${who} rejected the hook's settings — most often the callback URL, which it will not accept if it cannot reach it.`,
  429: (who) => `${who} is rate-limiting this connection. Nothing about the hook is wrong; there have simply been too many calls on this token.`,
};

const HOOK_RIGHTS: Record<ProviderKind, string> = {
  GitLab: "Creating a project hook needs Maintainer",
  GitHub: "Creating a repository hook needs admin rights on the repository",
  Git: "Creating a hook needs rights to manage the repository",
};

/**
 * What the run as a whole did. "All 10 attempts answered 403" and "9 attempts got no answer at all,
 * then 1 attempt answered 403" are two different faults, and the last attempt alone cannot tell them
 * apart.
 */
export function attemptPattern(timeline: readonly RepositoryWebhookAttemptDetail[]): string {
  const runs = consecutiveRuns(timeline);

  if (runs.length === 0) return "";
  if (runs.length === 1) return `All ${runs[0].count} ${plural(runs[0].count, "attempt")} ${outcomeVerb(runs[0].statusCode)}.`;

  return `${runs.map((run) => `${run.count} ${plural(run.count, "attempt")} ${outcomeVerb(run.statusCode)}`).join(", then ")}.`;
}

interface AttemptRun { statusCode: number | null; count: number }

function consecutiveRuns(timeline: readonly RepositoryWebhookAttemptDetail[]): AttemptRun[] {
  const runs: AttemptRun[] = [];

  for (const attempt of timeline) {
    const open = runs.at(-1);

    if (open && open.statusCode === attempt.statusCode) open.count += 1;
    else runs.push({ statusCode: attempt.statusCode, count: 1 });
  }

  return runs;
}

function outcomeVerb(statusCode: number | null): string {
  return statusCode == null ? "got no answer at all" : `answered ${statusCode}`;
}

/** The outcome as it appears in the timeline column. Shares `null ⇒ no answer` with the sentence above so the two can't disagree. */
export function attemptOutcome(attempt: RepositoryWebhookAttemptDetail): string {
  return attempt.statusCode == null ? "no answer" : String(attempt.statusCode);
}

export interface RejectionCopy {
  tone: WebhookTone;
  /** What happened, in the reader's terms. Never the stored reason string — "signature_invalid" is an identifier, not news. */
  headline: string;
  /** What to actually do. The sentence that either sends someone to fix something or tells them there is nothing to fix. */
  remedy: string;
}

/**
 * What a refusal means, and what to do about it.
 *
 * <p>The whole point of this function is that the five reasons are NOT one severity, and the page
 * would lie by presenting them as one. A signature mismatch is broken and someone has to go and
 * change a secret. An unsubscribed event type is noise and the right action is usually none. And
 * "no workflow was listening" is the system doing exactly what it was configured to do — rendering
 * that in the same alarmed tone as the first would send an operator hunting a fault that does not
 * exist, which is a worse outcome than not showing the row at all.</p>
 *
 * <p>So the tone is part of the answer, not decoration on it, and each remedy names the thing to go
 * and change rather than restating the reason in longer words.</p>
 */
export function rejectionCopy(reason: RejectionReason, provider: ProviderKind): RejectionCopy {
  return REJECTION_COPY[reason]?.(providerName(provider)) ?? unrecognisedRejection();
}

const REJECTION_COPY: Record<string, (who: string) => RejectionCopy> = {
  signature_invalid: (who) => ({
    tone: "bad",
    headline: "The signature did not match",
    remedy: `The secret held at ${who} is not the one CodeSpace signs against, so every delivery is refused unread. Open the hook above and re-paste the current secret into the hook's own secret field at ${who}.`,
  }),
  webhook_inactive: (who) => ({
    tone: "bad",
    headline: "The hook is switched off here",
    remedy: `${who} is still sending, and CodeSpace discards every delivery on arrival. Nothing will run off this repository until the hook is switched back on.`,
  }),
  malformed_payload: (who) => ({
    tone: "bad",
    headline: "The body was not the shape it should be",
    remedy: `The delivery was signed correctly but did not carry what ${who}'s format promises. That is almost always something between ${who} and here rewriting the request — a proxy, a gateway, a filter that touches the body.`,
  }),
  event_not_mapped: (who) => ({
    tone: "idle",
    headline: "An event nothing here acts on",
    remedy: `Harmless. ${who} is sending an event type CodeSpace does not react to. Narrow the hook's subscription at ${who} if you would rather it stopped sending them; ignoring it costs nothing.`,
  }),
  // The expected traffic of a group hook, and the one refusal that is neither a fault nor rare: the
  // hook covers every project under the owner and only some of them are bound. Said plainly, with
  // the rate limit named, so a reader does not count the rows and conclude it happened twice today.
  repository_not_bound: (who) => ({
    tone: "idle",
    headline: "For a repository nothing here has bound",
    remedy: `Not a fault. This connection registers one hook per group at ${who}, so it receives every project under that group — including ones CodeSpace does not track. Bind the repository named below if it should be tracked; otherwise ignore it. At most one of these is recorded per repository per day, however many arrive.`,
  }),
  // Distinct from webhook_inactive: nobody switched this off, the connection moved off it. The hook
  // is still live at the provider, which is the thing the operator has to go and remove.
  webhook_retired: (who) => ({
    tone: "bad",
    headline: "The hook was retired and is still sending",
    remedy: `CodeSpace asked ${who} to delete this hook when the connection changed webhook scope, and could not. ${who} is still delivering to it and every delivery is discarded. Remove the hook by hand at ${who} — nothing here will run off it again.`,
  }),
  // Deliberately the friendliest of the five. This is not a failure — it is the delivery arriving,
  // being verified, being understood, and finding that nothing asked for it.
  no_matching_activation: () => ({
    tone: "good",
    headline: "Nothing was listening for it",
    remedy: "Not a fault. The delivery was verified and read, and no workflow subscribes to this event for this repository. If something should have run, add an activation for this event to that workflow.",
  }),
};

/**
 * A reason this build has no words for — a server that is ahead of this page. Says exactly that
 * rather than inventing a diagnosis, and points at the recorded detail, which is all there is.
 */
function unrecognisedRejection(): RejectionCopy {
  return {
    tone: "idle",
    headline: "Refused on arrival",
    remedy: "CodeSpace refused this delivery for a reason this page does not have wording for. What was recorded is below, verbatim.",
  };
}

/**
 * Said on every row that could not be placed. It is shown rather than hidden on purpose: a delivery
 * that arrived and was thrown away is the thing being looked for, and dropping the ones we cannot
 * attribute would drop them exactly when ingestion is failing earliest.
 */
export const UNPLACED_DELIVERY_NOTE = "CodeSpace could not tell which repository this was for — it was refused before anything resolved one.";

/**
 * What the list is, in the corner above it. At the cap it has to say so: an unreachable instance
 * retries on a ladder and writes thousands of these in an afternoon, and a list that silently
 * stopped at fifty would read as "fifty happened".
 *
 * <p>"The list stops there" rather than "older ones are not shown", because a full page can also be
 * exactly the whole of it — the read asks for the cap and cannot tell the two apart. Claiming there
 * is more would be the same kind of lie in the other direction.</p>
 */
export function rejectedDeliveriesNote(count: number, cap: number): string {
  return count >= cap ? `Newest ${cap} — the list stops there` : `${count} ${plural(count, "refusal")}`;
}

/**
 * The steps for finishing by hand, quoting the provider's own field labels — those are the words on
 * the screen the reader is actually looking at, and the two providers do not use the same ones.
 * `**` marks a phrase the provider owns; the renderer sets those apart so they can be matched by eye
 * against the other window.
 */
export type WebhookSetupStep =
  | { kind: "say"; text: string }
  | { kind: "paste"; label: string; into: "url" | "secret" };

export function webhookSetupSteps(provider: ProviderKind, fullPath: string): WebhookSetupStep[] {
  if (provider === "GitHub") return gitHubSteps(fullPath);
  if (provider === "GitLab") return gitLabSteps(fullPath);

  return [
    { kind: "say", text: `Open the webhook settings for ${fullPath} at your provider and add a new hook.` },
    { kind: "paste", label: "URL", into: "url" },
    { kind: "paste", label: "Secret", into: "secret" },
    { kind: "say", text: "Subscribe it to push and pull-request events only — CodeSpace rejects anything it did not ask for." },
    { kind: "say", text: "Save, then send a test event. The row above turns to **Delivering** on its own once the first one lands here." },
  ];
}

function gitLabSteps(fullPath: string): WebhookSetupStep[] {
  return [
    { kind: "say", text: `In GitLab, open **${fullPath} → Settings → Webhooks** and click **Add new webhook**.` },
    { kind: "paste", label: "URL", into: "url" },
    { kind: "paste", label: "Secret token", into: "secret" },
    { kind: "say", text: "Under **Trigger**, tick **Push events** and **Merge request events**. Leave the rest clear — CodeSpace rejects deliveries it did not subscribe to." },
    { kind: "say", text: "Save, then use **Test → Push events** in GitLab. The row above turns to **Delivering** on its own once the first event lands here." },
  ];
}

function gitHubSteps(fullPath: string): WebhookSetupStep[] {
  return [
    { kind: "say", text: `In GitHub, open **${fullPath} → Settings → Webhooks** and click **Add webhook**.` },
    { kind: "paste", label: "Payload URL", into: "url" },
    // Its own step rather than a note on the URL: GitHub defaults to a form encoding CodeSpace does
    // not parse, and a hook that is otherwise perfect fails silently on every delivery because of it.
    { kind: "say", text: "Set **Content type** to **application/json**. GitHub's default is a form encoding CodeSpace does not read." },
    { kind: "paste", label: "Secret", into: "secret" },
    { kind: "say", text: "Choose **Let me select individual events**, then tick **Pushes** and **Pull requests** only." },
    { kind: "say", text: "Save, then use **Recent Deliveries → Redeliver** to send a test event. The row above turns to **Delivering** on its own once the first one lands here." },
  ];
}

/** "the provider" reads better than "Git" in a sentence about who refused a call. */
function providerName(provider: ProviderKind): string {
  return provider === "Git" ? "The provider" : provider;
}

/** GitLab and GitHub both hand back numeric hook ids; `#` makes one read as an id rather than a count. */
function formatHookId(externalId: string): string {
  return /^\d+$/.test(externalId) ? `#${externalId}` : externalId;
}

/** Forward-looking counterpart to `relativeTime` — "in 4 minutes", or "now" once the deadline has passed. */
function inWords(ms: number): string {
  if (ms <= 0) return "now";

  const minutes = Math.round(ms / 60_000);

  if (minutes < 1) return "in less than a minute";
  if (minutes < 60) return `in ${minutes} ${plural(minutes, "minute")}`;

  const hours = Math.round(minutes / 60);

  return `in ${hours} ${plural(hours, "hour")}`;
}

function plural(n: number, unit: string): string {
  return n === 1 ? unit : `${unit}s`;
}

/**
 * What the tab says at the top when the repository has no hook of its own because the connection
 * registers above it. Stated rather than left to be inferred from an empty list: the two look
 * identical to a reader, and one of them means everything is fine.
 */
export function connectionCoverageNote(ownerPath: string | null, provider: ProviderKind): string {
  const who = providerName(provider);

  if (ownerPath == null) {
    return `This connection registers one hook per group at ${who}, so this repository has none of its own — and no hook covering it exists yet. Nothing is arriving for it.`;
  }

  return `This repository has no hook of its own. One hook on ${ownerPath} at ${who} covers it, along with every other repository under that owner.`;
}
