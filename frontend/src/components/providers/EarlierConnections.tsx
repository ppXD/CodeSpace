import { useState } from "react";

import { Ic } from "@/_imported/ai-code-space/icons";
import type { CredentialSummary } from "@/api/types";
import { useRemoveCredential } from "@/hooks/use-credentials";

import { AuthorizedSince, NoticeLine, type Notice } from "./credentialParts";

interface EarlierConnectionsProps {
  /** Disconnected credentials nothing leans on (`isHistory`): history, not connections. */
  credentials: CredentialSummary[];
  /** False when the caller may not remove these — the button is absent rather than present-and-refused. */
  mayRemove: boolean;
  /** What the last action on each row said, by credential id — a refused Remove or Reconnect, said beside the row it was for. */
  notices: Record<string, Notice | undefined>;
  onNotice: (credentialId: string, notice: Notice | undefined) => void;
  /** Sign in again to a folded sign-in, in place. Null when there is nothing to sign in with: the provider has no OAuth app. */
  onReconnect: ((credential: CredentialSummary) => void) | null;
  /** A connect for this provider is running, so its buttons wait. */
  isConnecting: boolean;
}

/**
 * The dead rows a Disconnect leaves behind, folded into one quiet line so the list opens on what works. Each Disconnect and reconnect adds one, and
 * nothing depends on any of them, so listing them only made a person look for the one that counts. Opening the line lists them, each with Remove — which the
 * server accepts only while nothing leans on the row (and says what does when something does, shown beside it as it was said) — and, for a sign-in, Reconnect:
 * for a caller who cannot start a plain connect (it takes credentials.manage) re-authorizing their own dead sign-in in place is the only way back to one.
 */
export function EarlierConnections({ credentials, mayRemove, notices, onNotice, onReconnect, isConnecting }: EarlierConnectionsProps) {
  const [open, setOpen] = useState(false);
  const [removingId, setRemovingId] = useState<string | null>(null);
  const remove = useRemoveCredential();

  const removeOne = async (credential: CredentialSummary) => {
    setRemovingId(credential.id);
    onNotice(credential.id, undefined);

    try {
      await remove.mutateAsync(credential.id);
    } catch (err) {
      onNotice(credential.id, { tone: "err", text: err instanceof Error ? err.message : "Couldn't remove it." });
    } finally {
      setRemovingId(null);
    }
  };

  return (
    <div className="cn-earlier">
      <button type="button" className="cn-earlier-toggle" aria-expanded={open} onClick={() => setOpen(o => !o)}>
        <Ic.ChevronRight size={11} /> {`${credentials.length} earlier ${credentials.length === 1 ? "connection" : "connections"} — disconnected`}
      </button>

      {open && (
        <div className="cn-earlier-list">
          {credentials.map(c => (
            <div key={c.id} className="cn-earlier-row">
              <div className="cn-cred-main">
                <span className="cn-cred-method">
                  {c.authType === "OAuth" ? <><Ic.Link size={12} /> OAuth</> : <><Ic.Key size={12} /> Token</>}
                </span>
                <AuthorizedSince credential={c} />
                {onReconnect && c.authType === "OAuth" && (
                  <button type="button" className="btn cn-cred-x" onClick={() => onReconnect(c)} disabled={isConnecting}>
                    {isConnecting ? <><Ic.Clock size={13} /> …</> : "Reconnect"}
                  </button>
                )}
                {mayRemove && (
                  <button type="button" className="btn btn-ghost cn-cred-x" onClick={() => void removeOne(c)} disabled={removingId === c.id}>
                    {removingId === c.id ? "Removing…" : "Remove"}
                  </button>
                )}
              </div>
              {notices[c.id] && <NoticeLine notice={notices[c.id]!} />}
            </div>
          ))}
        </div>
      )}
    </div>
  );
}
