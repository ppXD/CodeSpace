import { useEffect, useRef, useState, type ReactNode } from "react";
import { createPortal } from "react-dom";

/**
 * The ⋯ menu on a list row.
 *
 * <p>Rendered through a portal because the list it sits in is <code>overflow: hidden</code> — it has
 * to be, since the rows share a rounded border the rows themselves must not poke through. A menu
 * positioned inside that box gets clipped the moment it is taller than the remaining row, which is
 * every menu with more than one item.</p>
 *
 * <p>Position is measured from the trigger at open time and fixed to the viewport. It also flips
 * above the trigger when there is no room below, so the last row of a list opens upward instead of
 * off-screen.</p>
 */
export function RowMenu({ label, children }: { label: string; children: (close: () => void) => ReactNode }) {
  const trigger = useRef<HTMLButtonElement>(null);
  const [at, setAt] = useState<{ top: number; right: number; flip: boolean } | null>(null);

  const close = () => setAt(null);

  useEffect(() => {
    if (at == null) return;

    // Any scroll or resize invalidates a measured position, and a menu that stays behind while the
    // page moves is worse than one that closes.
    const dismiss = () => setAt(null);

    window.addEventListener("scroll", dismiss, true);
    window.addEventListener("resize", dismiss);

    return () => {
      window.removeEventListener("scroll", dismiss, true);
      window.removeEventListener("resize", dismiss);
    };
  }, [at]);

  const open = () => {
    const rect = trigger.current?.getBoundingClientRect();

    if (!rect) return;

    const spaceBelow = window.innerHeight - rect.bottom;

    setAt({ top: rect.bottom + 4, right: window.innerWidth - rect.right, flip: spaceBelow < 200 });
  };

  return (
    <>
      <button ref={trigger} className="btn btn-icon" aria-label={label} aria-haspopup="menu" aria-expanded={at != null} onClick={() => (at ? close() : open())}>⋯</button>

      {at != null && createPortal(
        <>
          {/* Click-anywhere-to-close, and it also stops a click landing on the row underneath. */}
          <div style={{ position: "fixed", inset: 0, zIndex: 90 }} onClick={close} />
          <div
            role="menu"
            className="sb-pop sb-pop-menu"
            style={{
              position: "fixed",
              top: at.flip ? undefined : at.top,
              bottom: at.flip ? window.innerHeight - at.top + 8 : undefined,
              right: at.right,
              zIndex: 91,
              minWidth: 190,
            }}
          >
            {children(close)}
          </div>
        </>,
        document.body,
      )}
    </>
  );
}
