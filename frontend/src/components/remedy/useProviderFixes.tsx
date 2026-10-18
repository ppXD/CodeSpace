import { useCallback, useState, type ReactNode } from "react";

import { ConnectRemoteModal } from "@/_imported/ai-code-space/connect-remote-modal";
import { ApiError } from "@/api/request";
import type { AuthType, ProviderInstanceSummary } from "@/api/types";
import { useAlert } from "@/components/dialog";
import { ReplaceTokenModal } from "@/components/identities/ReplaceTokenModal";
import { useProviderInstances } from "@/hooks/use-credentials";
import { describeOAuthFlowError, OAuthFlowError, useOAuthFlow } from "@/hooks/use-oauth-flow";

import { RelinkDialog, type RelinkRepository } from "./RelinkDialog";

/** What a fix needs to know of a credential: which one, what to call it, where it lives. A remedy's credential and a list row both fit. */
export interface FixCredential {
  id: string;
  displayName: string;
  providerInstanceId: string;
  authType: AuthType;
}

type OpenDialog =
  | { kind: "relink"; repositories: RelinkRepository[]; providerInstanceId: string; fromCredentialId: string | null }
  | { kind: "replace-token"; credential: FixCredential; instance: ProviderInstanceSummary | undefined }
  | { kind: "providers"; editInstanceId?: string; suggestedBaseUrl?: string };

/**
 * The fixes a provider failure is repaired with — reconnect in place, replace a token, re-link a repository, correct a provider's
 * address, open the Providers dialog — wherever the failure is shown: the run page's card, a repository row, a refused delivery.
 * One place so each fix does the same thing everywhere and refuses the same way. Nothing here decides WHETHER a fix is offered: the
 * backend says that, per person, and the caller renders only what it was told is enabled.
 *
 * `fix.dialogs` must be rendered once by the caller, next to the buttons: the fixes that need a form (a token, a credential to pick,
 * a provider address) open it there. A reconnect needs none — it runs the provider's own sign-in in a popup and the credential keeps
 * its id, so everything bound through it comes back at once.
 */
export function useProviderFixes({ onFixed }: { onFixed?: () => void } = {}) {
  const instances = useProviderInstances();
  const runOAuth = useOAuthFlow();
  const alert = useAlert();
  const [dialog, setDialog] = useState<OpenDialog | null>(null);
  const [reconnectingId, setReconnectingId] = useState<string | null>(null);

  const instanceOf = useCallback((id: string) => instances.data?.find(i => i.id === id), [instances.data]);

  const reconnect = useCallback(async (credential: FixCredential) => {
    setReconnectingId(credential.id);

    try {
      await runOAuth({ providerInstanceId: credential.providerInstanceId, displayName: credential.displayName, replaceCredentialId: credential.id });
      onFixed?.();
    } catch (err) {
      const message = err instanceof OAuthFlowError ? describeOAuthFlowError(err) : err instanceof ApiError || err instanceof Error ? err.message : "Authorization failed.";
      await alert({ title: "Couldn't reconnect", message, variant: "error" });
    } finally {
      setReconnectingId(null);
    }
  }, [runOAuth, alert, onFixed]);

  const close = useCallback(() => setDialog(null), []);

  const dialogs: ReactNode = (
    <>
      {dialog?.kind === "relink" && (
        <RelinkDialog
          repositories={dialog.repositories}
          providerInstanceId={dialog.providerInstanceId}
          fromCredentialId={dialog.fromCredentialId}
          onClose={close}
          onRelinked={onFixed}
          onOpenProviders={() => setDialog({ kind: "providers" })}
        />
      )}
      {dialog?.kind === "replace-token" && (
        <ReplaceTokenModal
          credential={dialog.credential}
          providerLabel={dialog.instance ? `${dialog.instance.provider} · ${dialog.instance.displayName}` : "this provider"}
          onClose={close}
          onReplaced={onFixed}
        />
      )}
      {dialog?.kind === "providers" && <ConnectRemoteModal onClose={close} editInstanceId={dialog.editInstanceId} suggestedBaseUrl={dialog.suggestedBaseUrl} />}
    </>
  );

  return {
    /** The credential a reconnect is running for, so its button can say so. */
    reconnectingId,
    reconnect,
    replaceToken: (credential: FixCredential) => setDialog({ kind: "replace-token", credential, instance: instanceOf(credential.providerInstanceId) }),
    relink: (repositories: RelinkRepository[], providerInstanceId: string, fromCredentialId: string | null) => setDialog({ kind: "relink", repositories, providerInstanceId, fromCredentialId }),
    /** Open the provider's own edit form, starting from `suggestedBaseUrl` — the address it answered on. */
    editProviderAddress: (providerInstanceId: string, suggestedBaseUrl?: string | null) => setDialog({ kind: "providers", editInstanceId: providerInstanceId, suggestedBaseUrl: suggestedBaseUrl ?? undefined }),
    openProviders: () => setDialog({ kind: "providers" }),
    dialogs,
  };
}
