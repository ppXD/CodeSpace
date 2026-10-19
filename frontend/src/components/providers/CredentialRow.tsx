import type { ReactNode } from "react";

import { Ic } from "@/_imported/ai-code-space/icons";
import type { CredentialSummary, ProviderInstanceSummary, RelinkCredentialBindingsResponse, RelinkSuggestion } from "@/api/types";
import { RowMenu } from "@/components/settings/RowMenu";
import { useRelinkCredentialBindings, useRelinkSuggestions } from "@/hooks/use-credentials";
import { canMoveBindingsOff, credentialHealthWord } from "@/lib/credentialHealth";

import { AuthorizedSince, CredentialDependents, CredentialHealthChip, NoticeLine, type Notice } from "./credentialParts";

interface CredentialRowProps {
  credential: CredentialSummary;
  instance: ProviderInstanceSummary;
  /** What the last action on this credential said — a confirmation or the server's refusal. */
  notice: Notice | undefined;
  onNotice: (notice: Notice | undefined) => void;
  /** Said where the row will not be: moving what it held empties it, and an emptied dead row folds away with its confirmation. */
  onMoved: (text: string) => void;
  /** A connect for this provider is running, so its buttons wait. */
  isConnecting: boolean;
  isRevoking: boolean;
  /** Moving every repository at once takes `repos.manage` and `credentials.manage`. False hides the offer — and spares the read that finds where to move them. */
  mayMove: boolean;
  onReconnect: () => void;
  onReplaceToken: () => void;
  onDisconnect: () => void;
  /** What else hangs under the row (the capability warnings). */
  children?: ReactNode;
}

/**
 * One of the caller's credentials on a provider, and what can be done about it. A credential that does not work leads with the way back: moving what
 * it still holds to the person's own working connection when there is one, else bringing the credential itself back in place. A working sign-in keeps
 * re-authorizing in a menu — it is rarely the thing to press — and every action that lands says so in the row, because a re-authorization changes
 * nothing a person can otherwise see.
 */
export function CredentialRow({ credential: c, instance, notice, onNotice, onMoved, isConnecting, isRevoking, mayMove, onReconnect, onReplaceToken, onDisconnect, children }: CredentialRowProps) {
  const word = credentialHealthWord(c);
  const needsAttention = word !== "active";
  const target = useMoveTarget(c, mayMove);
  const relink = useRelinkCredentialBindings();

  const reconnectable = c.authType === "OAuth" && instance.oauthEnabled;
  const reconnectInMenu = reconnectable && !needsAttention;

  const move = async (to: RelinkSuggestion) => {
    onNotice(undefined);

    try {
      onMoved(movedSentence(await relink.mutateAsync({ fromCredentialId: c.id, toCredentialId: to.credentialId }), to.displayName));
    } catch (err) {
      onNotice({ tone: "err", text: err instanceof Error ? err.message : "Couldn't move them." });
    }
  };

  const secondary = target ? "btn btn-ghost cn-cred-x" : needsAttention ? "btn cn-cred-x" : "btn btn-ghost cn-cred-x";

  return (
    <div className="cn-cred">
      <div className="cn-cred-main">
        <span className="cn-cred-method">
          {c.authType === "OAuth" ? <><Ic.Link size={12} /> OAuth</> : <><Ic.Key size={12} /> Token</>}
        </span>
        <AuthorizedSince credential={c} />
        <CredentialHealthChip credential={c} />
        <CredentialDependents credential={c} />
        {target && (
          <button className="btn btn-primary cn-cred-x" onClick={() => void move(target)} disabled={relink.isPending}>
            {relink.isPending ? <><Ic.Clock size={13} /> Moving…</> : `Move them to ${target.displayName}`}
          </button>
        )}
        {/* Sign in again to the SAME credential (an OAuth sign-in), or paste a new token into it (a token): either way
            it keeps its id, so the repositories bound through it keep working. */}
        {reconnectable && !reconnectInMenu && (
          <button className={secondary} onClick={onReconnect} disabled={isConnecting}>
            {isConnecting ? <><Ic.Clock size={13} /> …</> : "Reconnect"}
          </button>
        )}
        {c.authType !== "OAuth" && (
          <button className={secondary} onClick={onReplaceToken}>Replace token</button>
        )}
        {word !== "disconnected" && (
          <button className="btn btn-ghost cn-cred-x" onClick={onDisconnect} disabled={isRevoking}>
            {isRevoking ? <><Ic.Clock size={13} /> …</> : "Disconnect"}
          </button>
        )}
        {reconnectInMenu && (
          <RowMenu label={`More actions for ${c.displayName}`} triggerClassName="btn btn-ghost cn-cred-x cn-cred-menu">
            {(close) => (
              <button className="sb-pop-item" onClick={() => { close(); onReconnect(); }} disabled={isConnecting}>
                <Ic.Link size={13} /> Re-authorize
              </button>
            )}
          </RowMenu>
        )}
      </div>
      {notice && <NoticeLine notice={notice} />}
      {children}
    </div>
  );
}

/**
 * Where "Move them" would send what the credential still holds: the person's own working connection, the first the server suggests of that kind. The read is
 * made only for a credential that has something to move and a caller who may move it. A team token is never the lead: it names a different identity, and
 * choosing it is what the repository's own Re-link dialog is for.
 */
function useMoveTarget(credential: CredentialSummary, mayMove: boolean): RelinkSuggestion | null {
  const eligible = mayMove && canMoveBindingsOff(credential);
  const suggestions = useRelinkSuggestions(credential.id, eligible);

  return eligible ? suggestions.data?.find(s => s.kind === "YourConnection") ?? null : null;
}

function movedSentence(moved: RelinkCredentialBindingsResponse, to: string): string {
  const parts = [plural(moved.movedRepositoryCount, "repository", "repositories"), plural(moved.movedConnectionWebhookCount, "group hook", "group hooks")].filter((p): p is string => p != null);

  return parts.length === 0 ? "Nothing was left on it to move." : `Moved ${parts.join(" and ")} to ${to}.`;
}

function plural(count: number, one: string, many: string): string | null {
  return count > 0 ? `${count} ${count === 1 ? one : many}` : null;
}
