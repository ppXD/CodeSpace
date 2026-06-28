import { useEffect } from "react";
import { createPortal } from "react-dom";

import { Ic } from "@/_imported/ai-code-space/icons";

/**
 * Shared right-side drawer shell. Portals to document.body (outside .acs-root), so it carries the `mdl drw`
 * classes: `mdl` brings the warm form / control / button styling that's mirrored at root level, `drw` overrides
 * the geometry to a full-height panel anchored right. Each view supplies its own head + body + optional foot.
 * Escape and the backdrop dismiss it (suspendable while a layered confirm dialog is open).
 */
export function DrawerFrame({ onClose, escapeDisabled, head, foot, children }: { onClose: () => void; escapeDisabled?: boolean; head: React.ReactNode; foot?: React.ReactNode; children: React.ReactNode }) {
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => { if (e.key === "Escape" && !escapeDisabled) onClose(); };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [onClose, escapeDisabled]);

  return createPortal(
    <>
      <div className="mdl-mask" onClick={onClose} />
      <div className="mdl drw" role="dialog" aria-modal="true">
        {head}
        <div className="mdl-body">{children}</div>
        {foot}
      </div>
    </>,
    document.body,
  );
}

/** The drawer's close button — placed in each view's head. */
export function DrawerClose({ onClose }: { onClose: () => void }) {
  return <button type="button" className="mdl-x" onClick={onClose} title="Close" aria-label="Close"><Ic.X size={14} /></button>;
}
