/* eslint-disable react-refresh/only-export-components -- a context module deliberately co-locates its
   provider component with the hooks that read it (useConfirm/useAlert); fast-refresh granularity is moot here. */
import { createContext, useCallback, useContext, useMemo, useState, type ReactNode } from "react";

import { Dialog } from "./dialog";

/**
 * App-wide confirm + alert system. One Dialog instance lives at the app root; any
 * component can request a styled prompt via the `useConfirm()` / `useAlert()` hooks.
 *
 * Replaces the browser's native window.confirm / window.alert (which are jarring on a
 * styled SPA, can't be themed, and block the JS thread). The promise-based API keeps
 * call sites readable:
 *
 *   const ok = await confirm({ title: "Remove provider", destructive: true });
 *   if (!ok) return;
 */

export interface ConfirmOptions {
  title: string;
  message?: ReactNode;
  confirmLabel?: string;
  cancelLabel?: string;
  destructive?: boolean;
}

export interface AlertOptions {
  title: string;
  message?: ReactNode;
  okLabel?: string;
  variant?: "info" | "error";
}

// One internal state shape covers both modes — the only thing that differs is whether
// there's a Cancel button. Storing the resolver here lets the dialog buttons close the
// modal AND fulfil the promise the caller is awaiting in a single click.
type DialogState =
  | { mode: "confirm"; options: ConfirmOptions; resolve: (ok: boolean) => void }
  | { mode: "alert"; options: AlertOptions; resolve: () => void }
  | null;

interface DialogContextValue {
  confirm: (options: ConfirmOptions) => Promise<boolean>;
  alert: (options: AlertOptions) => Promise<void>;
}

const DialogContext = createContext<DialogContextValue | null>(null);

export function DialogProvider({ children }: { children: ReactNode }) {
  const [state, setState] = useState<DialogState>(null);

  const confirm = useCallback((options: ConfirmOptions) => {
    return new Promise<boolean>(resolve => {
      // If a dialog is already open, the new one replaces it — caller of the prior
      // request gets `false` to avoid leaving a dangling promise. Stacked dialogs would
      // be confusing and break focus management.
      setState(prev => {
        if (prev?.mode === "confirm") prev.resolve(false);
        if (prev?.mode === "alert") prev.resolve();
        return { mode: "confirm", options, resolve };
      });
    });
  }, []);

  const alert = useCallback((options: AlertOptions) => {
    return new Promise<void>(resolve => {
      setState(prev => {
        if (prev?.mode === "confirm") prev.resolve(false);
        if (prev?.mode === "alert") prev.resolve();
        return { mode: "alert", options, resolve };
      });
    });
  }, []);

  const handleConfirm = () => {
    if (state?.mode === "confirm") state.resolve(true);
    else if (state?.mode === "alert") state.resolve();
    setState(null);
  };

  const handleCancel = () => {
    if (state?.mode === "confirm") state.resolve(false);
    else if (state?.mode === "alert") state.resolve();
    setState(null);
  };

  const value = useMemo<DialogContextValue>(() => ({ confirm, alert }), [confirm, alert]);

  return (
    <DialogContext.Provider value={value}>
      {children}
      {state && (
        <Dialog
          mode={state.mode}
          // Cast back to the right options shape — TS narrows via the discriminant.
          options={state.mode === "confirm" ? state.options : state.options}
          onConfirm={handleConfirm}
          onCancel={handleCancel}
        />
      )}
    </DialogContext.Provider>
  );
}

export function useConfirm() {
  const ctx = useContext(DialogContext);
  if (!ctx) throw new Error("useConfirm must be used inside <DialogProvider>");
  return ctx.confirm;
}

export function useAlert() {
  const ctx = useContext(DialogContext);
  if (!ctx) throw new Error("useAlert must be used inside <DialogProvider>");
  return ctx.alert;
}
