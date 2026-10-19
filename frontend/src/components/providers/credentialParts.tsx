import { Ic } from "@/_imported/ai-code-space/icons";
import type { CredentialSummary } from "@/api/types";
import { credentialHealthWord, dependentsLabel, isStaticToken } from "@/lib/credentialHealth";
import { formatRelative } from "@/lib/formatRelative";

/**
 * The small pieces every credential row in the Providers dialog is made of — what its health says, how many repositories lean on it, when it was last
 * authorized, and the line a finished action leaves behind — shared by the Personal tab's rows, the Team tab's and the folded history.
 */

/** What a finished action says in the row it was for: a confirmation (`ok`) or the server's own refusal (`err`). */
export interface Notice {
  tone: "ok" | "err";
  text: string;
}

/**
 * What a credential's health says to the person — every state, so no credential reads as healthy by saying nothing: working,
 * a token about to expire (and how long it has), one the provider refused (how to come back), one disconnected, one lent by
 * someone who left the team. The words come from `credentialHealthWord`, which folds `status` in for a server that sends no `health`.
 */
export function CredentialHealthChip({ credential }: { credential: CredentialSummary }) {
  const word = credentialHealthWord(credential);

  if (word === "rejected") {
    return (
      <span className="cn-status cn-status-warn" title={credential.lastError ?? "The provider refused this credential, or its token has expired."}>
        <Ic.Triangle size={10} /> {isStaticToken(credential) ? "needs a new token" : "needs reconnect"}
      </span>
    );
  }

  if (word === "disconnected") return <span className="cn-status cn-status-revoked" title="This credential was disconnected: its token is gone. Reconnect it to bring it back, or link what leans on it to another credential."><Ic.Triangle size={10} /> disconnected</span>;
  if (word === "owner_left") return <span className="cn-status cn-status-warn" title="This credential belongs to someone who left the team and is about to be retired. Link what leans on it to a credential that stays."><Ic.Triangle size={10} /> owner left</span>;

  if (word === "expiring") {
    const days = credential.daysToExpiry ?? 0;

    return (
      <span className="cn-status cn-status-warn" title="The provider ends this token on its expiry date. Replace it before then, in place, and nothing bound through it stops working.">
        <Ic.Clock size={10} /> {days === 0 ? "expires today" : `expires in ${days} ${days === 1 ? "day" : "days"}`}
      </span>
    );
  }

  return <span className="cn-status cn-status-active" title="Nothing recorded says this credential will not work."><span className="cn-status-dot" /> active</span>;
}

/** What still leans on a credential — the repositories a reconnect, replace or disconnect puts at stake, or what a dead one is still held by (repositories, group hooks, act-as identities). Nothing when none do. */
export function CredentialDependents({ credential }: { credential: CredentialSummary }) {
  const label = dependentsLabel(credential);

  return label ? <span className="cn-cred-since" data-testid="credential-dependents">{label}</span> : null;
}

/**
 * When the provider last confirmed this authorization: a reconnect or a replaced token stamps it, and so does the periodic health check, so it
 * reads "authorized" however recently the person signed in. A row nothing has confirmed says when it was connected instead. The tooltip keeps both dates.
 */
export function AuthorizedSince({ credential }: { credential: CredentialSummary }) {
  const connected = `Connected ${formatDate(credential.createdDate)}`;
  const confirmed = credential.lastValidatedDate;

  if (!confirmed) return <span className="cn-cred-since" title={connected}>{`connected ${formatRelative(credential.createdDate)}`}</span>;

  return <span className="cn-cred-since" title={`${connected}. The provider last confirmed this authorization ${formatDate(confirmed)}.`}>{`authorized ${formatRelative(confirmed)}`}</span>;
}

/** The line an action leaves in its row: green and polite for what landed, the row's error red and assertive for what was refused. */
export function NoticeLine({ notice }: { notice: Notice }) {
  const ok = notice.tone === "ok";

  return (
    <div className={ok ? "cn-note-ok" : "cn-row-err"} role={ok ? "status" : "alert"}>
      {ok && <Ic.Check size={12} />}
      <span>{notice.text}</span>
    </div>
  );
}

function formatDate(iso: string) {
  return new Date(iso).toLocaleString(undefined, { year: "numeric", month: "short", day: "numeric", hour: "2-digit", minute: "2-digit" });
}
