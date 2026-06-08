import { useCallback, useEffect, useState, type PointerEvent as ReactPointerEvent } from "react";

export type ResizablePane = "palette" | "inspector";

/** Per-rail bounds. A rail can't be dragged below its min (so it never collapses to nothing) or past its max. */
const BOUNDS: Record<ResizablePane, { min: number; max: number; default: number }> = {
  palette: { min: 180, max: 460, default: 220 },
  inspector: { min: 320, max: 680, default: 440 },
};

const STORAGE_KEY = "codespace.editor.pane-widths";

/** Clamp a requested pane width to its [min, max] bounds (whole px). Pure — unit-tested. */
export function clampPaneWidth(pane: ResizablePane, raw: number): number {
  const { min, max } = BOUNDS[pane];
  return Math.max(min, Math.min(max, Math.round(raw)));
}

interface PaneWidths { palette: number; inspector: number; }

function initialWidths(): PaneWidths {
  try {
    const raw = localStorage.getItem(STORAGE_KEY);
    if (raw) {
      const stored = JSON.parse(raw) as Partial<PaneWidths>;
      return {
        palette: clampPaneWidth("palette", stored.palette ?? BOUNDS.palette.default),
        inspector: clampPaneWidth("inspector", stored.inspector ?? BOUNDS.inspector.default),
      };
    }
  } catch {
    // ignore unreadable / corrupt storage — fall back to defaults
  }
  return { palette: BOUNDS.palette.default, inspector: BOUNDS.inspector.default };
}

/**
 * Drag-to-resize widths for the editor's left palette + right inspector rails. Widths are clamped
 * to a sensible [min, max] (a rail can never be dragged to nothing) and persisted to localStorage
 * so the operator's layout sticks across reloads. `startResize(pane, e)` wires to a rail handle's
 * onPointerDown and tracks the pointer on the document until release.
 */
export function usePaneResize() {
  const [widths, setWidths] = useState<PaneWidths>(initialWidths);

  useEffect(() => {
    try { localStorage.setItem(STORAGE_KEY, JSON.stringify(widths)); } catch { /* storage may be unavailable */ }
  }, [widths]);

  const startResize = useCallback((pane: ResizablePane, e: ReactPointerEvent) => {
    e.preventDefault();
    const startX = e.clientX;
    const startWidth = widths[pane];

    const onMove = (ev: PointerEvent) => {
      // Palette grows dragging its handle right; the inspector grows dragging LEFT.
      const delta = pane === "palette" ? ev.clientX - startX : startX - ev.clientX;
      setWidths((w) => ({ ...w, [pane]: clampPaneWidth(pane, startWidth + delta) }));
    };
    const onUp = () => {
      document.removeEventListener("pointermove", onMove);
      document.removeEventListener("pointerup", onUp);
      document.body.style.removeProperty("user-select");
      document.body.style.removeProperty("cursor");
    };

    document.body.style.userSelect = "none";
    document.body.style.cursor = "col-resize";
    document.addEventListener("pointermove", onMove);
    document.addEventListener("pointerup", onUp);
  }, [widths]);

  return { paletteWidth: widths.palette, inspectorWidth: widths.inspector, startResize };
}
