import { useEffect, type RefObject } from "react";

/**
 * The two things every dialog in this app owes a keyboard: Escape closes it, and focus starts inside it.
 *
 * The confirm dialog does both (`components/dialog/dialog.tsx`); the feature dialogs each rolled their own portal and
 * did neither, so a keyboard operator landed on a page whose content was behind a mask with no way out but the mouse.
 * Shared here rather than repeated, so a new dialog cannot forget one of the two.
 *
 * Deliberately not a focus TRAP. No modal in this app has one, and adding it to three dialogs would make their
 * behaviour diverge from every other modal an operator meets here — worth doing, but as one change to all of them.
 */
export function useDialogKeys(dialog: RefObject<HTMLElement | null>, onEscape: () => void) {
  useEffect(() => {
    const escape = (event: KeyboardEvent) => {
      if (event.key !== "Escape") return;
      event.stopPropagation();
      onEscape();
    };
    document.addEventListener("keydown", escape);
    return () => document.removeEventListener("keydown", escape);
  }, [onEscape]);

  useEffect(() => {
    const surface = dialog.current;
    if (surface == null) return;
    // The first control, or the surface itself — never the page behind the mask.
    const target = surface.querySelector<HTMLElement>('input, select, textarea, button, [contenteditable="true"], [tabindex]:not([tabindex="-1"])') ?? surface;
    target.focus({ preventScroll: true });
  }, [dialog]);
}
