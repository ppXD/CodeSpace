import { createContext, useCallback, useContext, useState, type ReactNode } from "react";

import { parseActorIdentityRequired } from "@/lib/actorIdentity";

import { IdentityLinkModal } from "./IdentityLinkModal";

interface ActorIdentityGateValue {
  /**
   * If `error` is the backend's 428 `actor_identity_required` signal, open the shared link modal
   * for the named provider instance and return true — the caller should stop its own error
   * handling. `retry` runs after a successful link, so the action the user attempted just works.
   * Returns false for any other error, leaving the caller to handle it.
   */
  prompt: (error: unknown, retry?: () => void) => boolean;
}

const ActorIdentityGateContext = createContext<ActorIdentityGateValue | null>(null);

export function useActorIdentityGate(): ActorIdentityGateValue {
  const value = useContext(ActorIdentityGateContext);
  if (!value) throw new Error("useActorIdentityGate must be used within an ActorIdentityProvider");
  return value;
}

interface PendingLink {
  providerInstanceId: string;
  providerLabel: string;
  retry?: () => void;
}

/**
 * App-wide reactive identity gate (Model B). Any act-as-user mutation routes its error through
 * `useActorIdentityGate().prompt(error, retry)`: when the backend says the caller must link an
 * identity first, this opens the shared {@link IdentityLinkModal} for the right provider instance
 * and, once linked, runs `retry`. One interceptor, reused by every act-as-user feature — the
 * frontend half of the seam the backend's `GlobalExceptionFilter` already builds.
 */
export function ActorIdentityProvider({ children }: { children: ReactNode }) {
  const [pending, setPending] = useState<PendingLink | null>(null);

  const prompt = useCallback((error: unknown, retry?: () => void) => {
    const info = parseActorIdentityRequired(error);
    if (!info) return false;

    setPending({ providerInstanceId: info.providerInstanceId, providerLabel: info.provider, retry });
    return true;
  }, []);

  // IdentityLinkModal fires onLinked() then onClose() on success, so retry runs first, then the
  // modal dismisses via close. A retry that 428s again simply re-opens the gate.
  const handleLinked = useCallback(() => pending?.retry?.(), [pending]);

  return (
    <ActorIdentityGateContext.Provider value={{ prompt }}>
      {children}
      {pending && (
        <IdentityLinkModal
          providerInstanceId={pending.providerInstanceId}
          providerLabel={pending.providerLabel}
          onClose={() => setPending(null)}
          onLinked={handleLinked}
        />
      )}
    </ActorIdentityGateContext.Provider>
  );
}
