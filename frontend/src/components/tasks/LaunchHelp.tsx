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

function Node({ x, y, w, h = 32, title, sub, tone = "plain", dashed }: { x: number; y: number; w: number; h?: number; title: string; sub?: string; tone?: keyof typeof TONES; dashed?: boolean }) {
  const t = TONES[tone];
  return (
    <g>
      <rect x={x} y={y} width={w} height={h} rx={6} fill={dashed ? "transparent" : t.fill} stroke={dashed ? "#cfc6b8" : t.stroke} strokeWidth={1} strokeDasharray={dashed ? "4 3" : undefined} />
      <text x={x + w / 2} y={sub ? y + h / 2 - 2.5 : y + h / 2 + 3.5} textAnchor="middle" fontSize={11} fontWeight={600} fill={t.text}>{title}</text>
      {sub && <text x={x + w / 2} y={y + h / 2 + 11.5} textAnchor="middle" fontSize={10} fill={t.sub}>{sub}</text>}
    </g>
  );
}

function Flow({ d, tone = "plain", still }: { d: string; tone?: keyof typeof LINE; still?: boolean }) {
  return <path d={d} fill="none" stroke={LINE[tone]} strokeWidth={1.4} className={still ? undefined : "lt3-hd-flow"} markerEnd="url(#lt3hd-arr)" />;
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

function Tag({ x, y, text, tone = "plain" }: { x: number; y: number; text: string; tone?: keyof typeof LINE }) {
  return <text x={x} y={y} fontSize={10} fill={tone === "good" ? TONES.good.text : tone === "warn" ? TONES.warn.text : "#8a8378"}>{text}</text>;
}

/** Plan critic — the plan is reviewed BEFORE agents run; Gate hands the verdict to you, Improve feeds it back. */
export function PlanCriticDiagram() {
  return (
    <svg viewBox="0 0 380 212" role="img" aria-label="Plan critic flow: the plan is reviewed; pass proceeds; Gate routes concerns to your call; Improve replans once; both converge and the plan proceeds.">
      <Arr />
      <Node x={10} y={10} w={70} title="Plan v1" />
      <Flow d="M80 26 L104 26" />
      <Node x={106} y={10} w={142} title="Independent review" tone="purple" />
      <Tag x={300} y={20} text="pass" tone="good" />
      <Flow d="M248 26 L348 26 L348 176 L247 176" tone="good" />
      <Tag x={222} y={62} text="flagged" tone="warn" />
      <Flow d="M140 42 L140 66 L78 66 L78 86" tone="warn" />
      <Flow d="M214 42 L214 66 L272 66 L272 86" tone="warn" />
      <Node x={10} y={86} w={136} h={40} title="Gate — you decide" sub="evidence → your call" tone="warn" />
      <Node x={192} y={86} w={160} h={40} title="Improve — auto revise" sub="critique feeds a replan" tone="purple" />
      <Flow d="M78 126 L78 148 L160 148 L160 160" />
      <Flow d="M272 126 L272 148 L220 148 L220 160" />
      <Node x={135} y={160} w={110} title="Plan proceeds" />
    </svg>
  );
}

/** Evaluation — the result pipeline: objective checks, then the subjective critic, with the revise loop closing it. */
export function EvaluationPipelineDiagram() {
  return (
    <svg viewBox="0 0 380 248" role="img" aria-label="Evaluation pipeline: agent work passes objective checks, then the critic judges it; failures feed a bounded self-revise loop back to the agent; exhausted rounds are flagged for you; acceptance criteria are the shared yardstick.">
      <Arr />
      <Node x={10} y={10} w={88} h={40} title="Agent work" />
      <Flow d="M98 30 L112 30" />
      <Node x={114} y={10} w={90} h={40} title="① checks" sub="commands run" />
      <Flow d="M204 30 L218 30" />
      <Node x={220} y={10} w={110} h={40} title="③ critic" sub="② model / agent" tone="purple" />
      <Tag x={306} y={76} text="pass" tone="good" />
      <Flow d="M300 50 L300 96" tone="good" />
      <Node x={220} y={96} w={110} h={30} title="Succeeded" tone="good" />
      <Tag x={176} y={66} text="fail" tone="warn" />
      <Flow d="M250 50 L250 72 L120 72 L120 96" tone="warn" />
      <Flow d="M159 50 L159 72" tone="warn" still />
      <Node x={48} y={96} w={144} h={40} title="⑤ self-revise" sub="0–3 rounds" tone="warn" />
      <Flow d="M48 110 L30 110 L30 64 L54 64 L54 50" tone="warn" />
      <Flow d="M120 136 L120 158" tone="warn" still />
      <Node x={48} y={158} w={170} h={30} title="rounds spent → flagged" tone="warn" />
      <Node x={10} y={204} w={360} h={30} title="④ criteria — the shared yardstick" dashed />
    </svg>
  );
}

/** Decision critic — the hard gate on every supervisor move: revise, re-review, then escalate to the human. */
export function DecisionLadderDiagram() {
  return (
    <svg viewBox="0 0 380 220" role="img" aria-label="Decision critic ladder: a flagged decision does not execute — it self-revises, is re-reviewed, and only then escalates to your call; approve is a one-shot pass, anything else is guidance.">
      <Arr />
      <Node x={10} y={10} w={104} h={40} title="Decision" sub="spawn · stop …" />
      <Flow d="M114 30 L128 30" />
      <Node x={130} y={10} w={80} h={40} title="Review" tone="purple" />
      <Tag x={226} y={22} text="pass" tone="good" />
      <Flow d="M210 30 L266 30" tone="good" />
      <Node x={268} y={10} w={76} h={40} title="Execute" tone="good" />
      <Tag x={158} y={80} text="flagged (Gate)" tone="warn" />
      <Flow d="M150 50 L150 86 L56 86 L56 104" tone="warn" />
      <Node x={10} y={104} w={108} h={36} title="① self-revise" />
      <Flow d="M118 122 L132 122" />
      <Node x={134} y={104} w={108} h={36} title="② re-review" tone="purple" />
      <Flow d="M188 104 L188 94 L306 94 L306 50" tone="good" />
      <Flow d="M242 122 L262 122" tone="warn" />
      <Node x={264} y={104} w={106} h={36} title="③ your call" tone="warn" />
      <Flow d="M317 140 L317 178" tone="warn" still />
      <Node x={70} y={178} w={300} h={28} title="approve = one-shot pass · else guidance" dashed />
    </svg>
  );
}
