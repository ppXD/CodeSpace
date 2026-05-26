import { useEffect, useRef } from "react";
import { createPortal } from "react-dom";

import type { AlertOptions, ConfirmOptions } from "./dialog-context";

/**
 * Presentation component — pure render. State + promise management lives in
 * dialog-context.tsx; this file just paints what the context tells it to.
 *
 * Visual contract: reuses the existing .mdl / .mdl-mask shell so the prompt feels like
 * part of the same modal family as Connect remote / Add repository, with a `.mdl-dialog`
 * modifier that tightens it to the compact (~440px) confirm-prompt size. No subtitle,
 * no scroll area — the body is the message inline.
 */

interface DialogProps {
  mode: "confirm" | "alert";
  options: ConfirmOptions | AlertOptions;
  onConfirm: () => void;
  onCancel: () => void;
}

export function Dialog({ mode, options, onConfirm, onCancel }: DialogProps) {
  // Auto-focus the primary action so Enter confirms. Destructive prompts are NOT auto-
  // confirmed by Enter — see keydown handler below — to avoid an accidental keypress
  // wiping data. The button is still focused for visual continuity.
  const primaryRef = useRef<HTMLButtonElement>(null);
  useEffect(() => { primaryRef.current?.focus(); }, []);

  // Escape always cancels. Enter confirms on alert (single-button) or non-destructive
  // confirm. Destructive confirms require an explicit click on the red button.
  useEffect(() => {
    const handler = (e: KeyboardEvent) => {
      if (e.key === "Escape") { e.preventDefault(); onCancel(); return; }
      if (e.key === "Enter") {
        const destructive = mode === "confirm" && (options as ConfirmOptions).destructive === true;
        if (destructive) return;
        e.preventDefault();
        onConfirm();
      }
    };
    window.addEventListener("keydown", handler);
    return () => window.removeEventListener("keydown", handler);
  }, [mode, options, onConfirm, onCancel]);

  const isConfirm = mode === "confirm";
  const confirmOptions = options as ConfirmOptions;
  const alertOptions = options as AlertOptions;

  const title = options.title;
  const message = options.message;
  const destructive = isConfirm && confirmOptions.destructive === true;

  return createPortal(
    <>
      {/* Mask is non-interactive — Escape or an explicit button cancels. Matches the
          rest of the modal family's behaviour so users don't lose work to a stray click. */}
      <div className="mdl-mask" />
      <div className="mdl mdl-dialog" role="alertdialog" aria-modal="true" aria-labelledby="dialog-title">
        <div className="mdl-dialog-head">
          <div className="mdl-dialog-title" id="dialog-title">{title}</div>
        </div>
        {message != null && (
          <div className="mdl-dialog-body">{message}</div>
        )}
        <div className="mdl-dialog-foot">
          {isConfirm && (
            <button className="btn" onClick={onCancel}>
              {confirmOptions.cancelLabel ?? "Cancel"}
            </button>
          )}
          <button
            ref={primaryRef}
            className={destructive ? "btn btn-danger" : "btn btn-primary"}
            onClick={onConfirm}
          >
            {isConfirm ? (confirmOptions.confirmLabel ?? "OK") : (alertOptions.okLabel ?? "OK")}
          </button>
        </div>
      </div>
    </>,
    document.body,
  );
}
