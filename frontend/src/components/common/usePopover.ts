import { useEffect, useLayoutEffect, useRef, useState } from "react";

/**
 * Anchored-popover state: tracks open + the button/popover refs, positions the menu under the button (fixed),
 * and closes on outside-click or scroll. Shared by the warm dropdowns ({@link Combo} + the launch composer's RowPop).
 * Its own file so it doesn't share a module with a component (react-refresh wants component-only exports).
 */
export function usePopover() {
  const [open, setOpen] = useState(false);
  const btnRef = useRef<HTMLButtonElement>(null);
  const popRef = useRef<HTMLDivElement>(null);
  const [pos, setPos] = useState<{ left: number; top: number; width: number } | null>(null);
  useLayoutEffect(() => {
    if (open && btnRef.current) {
      const r = btnRef.current.getBoundingClientRect();
      setPos({ left: r.left, top: r.bottom + 5, width: Math.max(r.width, 200) });
    }
  }, [open]);
  useEffect(() => {
    if (!open) return;
    const inside = (n: EventTarget | null) => btnRef.current?.contains(n as Node) || popRef.current?.contains(n as Node);
    const onDown = (e: MouseEvent) => { if (!inside(e.target)) setOpen(false); };
    const onScroll = (e: Event) => { if (!popRef.current?.contains(e.target as Node)) setOpen(false); };
    document.addEventListener("mousedown", onDown);
    window.addEventListener("scroll", onScroll, true);
    return () => { document.removeEventListener("mousedown", onDown); window.removeEventListener("scroll", onScroll, true); };
  }, [open]);
  return { open, setOpen, btnRef, popRef, pos };
}
