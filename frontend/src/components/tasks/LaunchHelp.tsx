import { useRef, useState, type ReactNode } from "react";
import { createPortal } from "react-dom";

import { Ic } from "@/_imported/ai-code-space/icons";

/**
 * A hover "?" beside a settings row — opens a floating card with an ANIMATED mini flow diagram, so Gate / Improve /
 * reviewer mechanics are SHOWN flowing, not just named. Hover-in opens; a short close delay lets the pointer cross
 * into the card; focus/blur mirror it for keyboards. Portal-fixed so the modal never clips it; flips above when the
 * card would run off the bottom.
 */
export function HelpTip({ title, note, children }: { title: string; note?: string; children: ReactNode }) {
  const [pos, setPos] = useState<{ left: number; top: number } | null>(null);
  const btnRef = useRef<HTMLButtonElement>(null);
  const timer = useRef<ReturnType<typeof setTimeout> | undefined>(undefined);

  const CARD_W = 408;
  const CARD_H = 330;

  const open = () => {
    if (timer.current) clearTimeout(timer.current);
    const r = btnRef.current?.getBoundingClientRect();
    if (!r) return;
    const left = Math.max(8, Math.min(window.innerWidth - CARD_W - 8, r.right - CARD_W));
    const top = r.bottom + 8 + CARD_H > window.innerHeight ? Math.max(8, r.top - CARD_H - 8) : r.bottom + 8;
    setPos({ left, top });
  };
  const closeSoon = () => { timer.current = setTimeout(() => setPos(null), 150); };
  const hold = () => { if (timer.current) clearTimeout(timer.current); };

  return (
    <>
      <button ref={btnRef} type="button" className="lt3-qhelp" aria-label={`How ${title} works`}
        onMouseEnter={open} onMouseLeave={closeSoon} onFocus={open} onBlur={closeSoon} onClick={open}>
        <Ic.Help size={14} />
      </button>
      {pos && createPortal(
        <div className="lt3-helpcard" style={{ position: "fixed", left: pos.left, top: pos.top, width: CARD_W }}
          onMouseEnter={hold} onMouseLeave={closeSoon}>
          <div className="lt3-helpcard-t">{title}</div>
          {children}
          {note && <div className="lt3-hd-note">{note}</div>}
        </div>,
        document.body,
      )}
    </>
  );
}

// The palette is HARDCODED warm hexes on purpose: the card portals to document.body where the room's --rm-* variables
// may not resolve — an unresolved var() in an SVG fill renders BLACK. The app font is Geist Mono, so text width is
// deterministic: chars × fontSize × 0.6 — every box below is sized from that (title 11px, subtitle 10px, +16px pad).
const TONES = {
  plain: { fill: "#faf6f0", stroke: "#e5ddd1", text: "#57524b", sub: "#8a8378" },
  purple: { fill: "#F2ECF7", stroke: "#E4D9EF", text: "#7D5FA6", sub: "#9B85B8" },
  good: { fill: "#EAF3DE", stroke: "#CBE0A9", text: "#3B6D11", sub: "#6F944C" },
  warn: { fill: "#FAEEDA", stroke: "#EFD9AC", text: "#915F0B", sub: "#B08542" },
} as const;

const LINE = { plain: "#b3aa9c", good: "#7BA254", warn: "#D9A253" } as const;

function Flow({ d, tone = "plain", still }: { d: string; tone?: keyof typeof LINE; still?: boolean }) {
  return <path d={d} fill="none" stroke={LINE[tone]} strokeWidth={1.4} className={still ? undefined : "lt3-hd-flow"} markerEnd="url(#lt3hd-arr)" />;
}

// ── Option-lane layout: ONE strictly-horizontal lane per dropdown option, widths COMPUTED from Geist Mono
//    character counts (10.5px title ≈ 6.3px/ch, 9.5px sub ≈ 5.7px/ch) so nothing can overflow, everything aligns.
const CH_T = 6.3;
const CH_S = 5.7;
const LANE_X0 = 76;   // pill column (62) + gap (10) + left margin (4)
const LANE_AR = 12;   // arrow length between boxes
const LANE_STEP = 44; // vertical rhythm per lane

interface LaneBox { t: string; sub?: string; tone: keyof typeof TONES }

function laneBoxW(b: LaneBox) {
  return Math.max(Math.round(b.t.length * CH_T), b.sub ? Math.round(b.sub.length * CH_S) : 0) + 12;
}

/** One option's lane: [option pill] box ──▸ box ──▸ box, left-to-right only — no branches, no crossings. The lane
 *  matching the CURRENT dropdown value stays vivid; the others dim, so "what will MY choice do" reads at a glance. */
function Lane({ index, pill, pillTone, boxes, active }: { index: number; pill: string; pillTone: keyof typeof TONES; boxes: LaneBox[]; active: boolean }) {
  const y = 8 + index * LANE_STEP;
  const pt = TONES[pillTone];
  const placed = boxes.reduce<{ list: (LaneBox & { x: number; w: number })[]; x: number }>(
    (acc, b) => { const w = laneBoxW(b); return { list: [...acc.list, { ...b, x: acc.x, w }], x: acc.x + w + LANE_AR }; },
    { list: [], x: LANE_X0 },
  ).list;
  return (
    <g opacity={active ? 1 : 0.35}>
      <rect x={4} y={y + 2} width={62} height={20} rx={10} fill={pt.fill} stroke={active ? pt.text : pt.stroke} strokeWidth={active ? 1.2 : 1} />
      <text x={35} y={y + 15.5} textAnchor="middle" fontSize={10.5} fontWeight={600} fill={pt.text}>{pill}</text>
      {placed.map((b, i) => {
        const t = TONES[b.tone];
        const h = b.sub ? 32 : 24;
        const top = b.sub ? y - 4 : y;
        return (
          <g key={i}>
            {i > 0 && <Flow d={`M${b.x - LANE_AR} ${y + 12} L${b.x - 2} ${y + 12}`} tone={b.tone === "good" ? "good" : b.tone === "warn" ? "warn" : "plain"} still={!active} />}
            <rect x={b.x} y={top} width={b.w} height={h} rx={6} fill={t.fill} stroke={t.stroke} strokeWidth={1} />
            <text x={b.x + b.w / 2} y={b.sub ? top + 13 : y + 16} textAnchor="middle" fontSize={10.5} fontWeight={600} fill={t.text}>{b.t}</text>
            {b.sub && <text x={b.x + b.w / 2} y={top + 25} textAnchor="middle" fontSize={9.5} fill={t.sub}>{b.sub}</text>}
          </g>
        );
      })}
    </g>
  );
}

/** Map a critic dropdown value onto its lane, so the hovered card highlights the CURRENTLY selected path. */
function laneActive(current: string | undefined, lane: "None" | "Gate" | "Improve") {
  return !current || current === lane;
}

function Arr() {
  return (
    <defs>
      <marker id="lt3hd-arr" viewBox="0 0 10 10" refX="8" refY="5" markerWidth="5.5" markerHeight="5.5" orient="auto-start-reverse">
        <path d="M2 1L8 5L2 9" fill="none" stroke="context-stroke" strokeWidth="1.6" strokeLinecap="round" strokeLinejoin="round" />
      </marker>
    </defs>
  );
}

/** Plan critic — one lane per option: what happens to the plan under Off / Gate / Improve. */
export function PlanCriticDiagram({ current }: { current?: string }) {
  return (
    <svg viewBox="0 0 380 128" role="img" aria-label="Plan critic options, one lane each: Off — the plan just runs; Gate — a flagged plan carries the reviewer's notes to your call, then runs; Improve — a flagged plan is replanned once automatically, then runs.">
      <Arr />
      <Lane index={0} pill="Off" pillTone="plain" active={laneActive(current, "None")} boxes={[
        { t: "plan", tone: "plain" }, { t: "agents run", tone: "good" }]} />
      <Lane index={1} pill="Gate" pillTone="warn" active={laneActive(current, "Gate")} boxes={[
        { t: "plan", tone: "plain" }, { t: "review ⚠", tone: "purple" }, { t: "notes → you", tone: "warn" }, { t: "runs", tone: "good" }]} />
      <Lane index={2} pill="Improve" pillTone="purple" active={laneActive(current, "Improve")} boxes={[
        { t: "plan", tone: "plain" }, { t: "review ⚠", tone: "purple" }, { t: "replan ×1", tone: "warn" }, { t: "runs", tone: "good" }]} />
    </svg>
  );
}

/** Agent output critic — one lane per option: what happens to an agent's finished work under Off / Gate / Improve. */
export function EvaluationPipelineDiagram({ current }: { current?: string }) {
  return (
    <svg viewBox="0 0 380 128" role="img" aria-label="Agent output critic options, one lane each: Off — the work is done as produced; Gate — flagged work waits for your review; Improve — flagged work is revised by the same agent up to N rounds, then passes.">
      <Arr />
      <Lane index={0} pill="Off" pillTone="plain" active={laneActive(current, "None")} boxes={[
        { t: "works", tone: "plain" }, { t: "done", tone: "good" }]} />
      <Lane index={1} pill="Gate" pillTone="warn" active={laneActive(current, "Gate")} boxes={[
        { t: "works", tone: "plain" }, { t: "critic ⚠", tone: "purple" }, { t: "flagged — you look", tone: "warn" }]} />
      <Lane index={2} pill="Improve" pillTone="purple" active={laneActive(current, "Improve")} boxes={[
        { t: "works", tone: "plain" }, { t: "critic ⚠", tone: "purple" }, { t: "revise ×N", tone: "warn" }, { t: "done ✓", tone: "good" }]} />
    </svg>
  );
}

/** Decision critic — one lane per option: what happens to each supervisor move under Off / Improve / Gate. */
export function DecisionLadderDiagram({ current }: { current?: string }) {
  return (
    <svg viewBox="0 0 380 132" role="img" aria-label="Decision critic options, one lane each: Off — every decision executes unreviewed; Improve — a flagged decision is revised once, then executes; Gate — a flagged decision gets one fix and a re-review, and only a still-flagged one becomes your call.">
      <Arr />
      <Lane index={0} pill="Off" pillTone="plain" active={laneActive(current, "None")} boxes={[
        { t: "decision", tone: "plain" }, { t: "executes", tone: "good" }]} />
      <Lane index={1} pill="Improve" pillTone="purple" active={laneActive(current, "Improve")} boxes={[
        { t: "decision", tone: "plain" }, { t: "review ⚠", tone: "purple" }, { t: "revise ×1", tone: "warn" }, { t: "executes", tone: "good" }]} />
      <Lane index={2} pill="Gate" pillTone="warn" active={laneActive(current, "Gate")} boxes={[
        { t: "decide", tone: "plain" }, { t: "review ⚠", tone: "purple" }, { t: "fix ×1", tone: "warn" }, { t: "your call ✋", sub: "fixed → runs", tone: "warn" }]} />
    </svg>
  );
}
