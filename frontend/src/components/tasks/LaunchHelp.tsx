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
      <button ref={btnRef} type="button" className="lt3-help" aria-label={`How ${title} works`}
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

const BOX = { fill: "#faf6f0", stroke: "#e5ddd1" };
const PURPLE = { fill: "#F2ECF7", stroke: "#E4D9EF", text: "#7D5FA6" };
const INK = "#57524b";
const MUT = "#8a8378";

function Node({ x, y, w, h = 30, title, sub, tone }: { x: number; y: number; w: number; h?: number; title: string; sub?: string; tone?: "purple" | "good" | "warn" | "plain" | "dashed" }) {
  const fill = tone === "purple" ? PURPLE.fill : tone === "good" ? "var(--rm-good-bg)" : tone === "warn" ? "var(--rm-warn-bg)" : BOX.fill;
  const stroke = tone === "purple" ? PURPLE.stroke : tone === "good" ? "var(--rm-good-line)" : tone === "warn" ? "var(--rm-warn-line)" : BOX.stroke;
  const text = tone === "purple" ? PURPLE.text : tone === "good" ? "var(--rm-good)" : tone === "warn" ? "var(--rm-warn)" : INK;
  return (
    <g>
      <rect x={x} y={y} width={w} height={h} rx={6} fill={tone === "dashed" ? "transparent" : fill} stroke={tone === "dashed" ? "#d5ccbf" : stroke} strokeWidth={1} strokeDasharray={tone === "dashed" ? "4 3" : undefined} />
      <text x={x + w / 2} y={sub ? y + h / 2 - 3 : y + h / 2 + 4} textAnchor="middle" fontSize={11} fontWeight={600} fill={tone === "dashed" ? MUT : text}>{title}</text>
      {sub && <text x={x + w / 2} y={y + h / 2 + 11} textAnchor="middle" fontSize={10} fill={tone === "dashed" ? MUT : text} opacity={0.75}>{sub}</text>}
    </g>
  );
}

function Flow({ d, tone, still }: { d: string; tone?: "good" | "warn" | "plain"; still?: boolean }) {
  const stroke = tone === "good" ? "var(--rm-good)" : tone === "warn" ? "var(--rm-warn)" : "#b3aa9c";
  return <path d={d} fill="none" stroke={stroke} strokeWidth={1.4} className={still ? undefined : "lt3-hd-flow"} markerEnd="url(#lt3hd-arr)" />;
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

function Tag({ x, y, text, tone }: { x: number; y: number; text: string; tone?: "good" | "warn" }) {
  return <text x={x} y={y} fontSize={10} fill={tone === "good" ? "var(--rm-good)" : tone === "warn" ? "var(--rm-warn)" : MUT}>{text}</text>;
}

/** Plan critic — the plan is reviewed BEFORE agents run; Gate hands the verdict to you, Improve feeds it back. */
export function PlanCriticDiagram() {
  return (
    <svg viewBox="0 0 380 208" role="img" aria-label="Plan critic flow: the plan is reviewed; pass proceeds; Gate annotates concerns for your call; Improve replans once.">
      <Arr />
      <Node x={8} y={10} w={78} title="Plan v1" />
      <Flow d="M86 25 L116 25" />
      <Node x={118} y={10} w={136} title="Independent review" tone="purple" />
      <Flow d="M254 25 L330 25 L330 158 L262 158" tone="good" />
      <Tag x={262} y={19} text="pass" tone="good" />
      <Flow d="M150 40 L150 64 L96 64 L96 84" tone="warn" />
      <Flow d="M222 40 L222 84" tone="warn" />
      <Tag x={158} y={58} text="flagged" tone="warn" />
      <Node x={8} y={84} w={176} h={44} title="Gate — you decide" sub="concerns + evidence → your call" tone="warn" />
      <Node x={204} y={84} w={168} h={44} title="Improve — auto revise" sub="critique feeds one replan" tone="purple" />
      <Flow d="M96 128 L96 150 L146 150 L146 166" />
      <Flow d="M288 128 L288 146 L226 146 L226 166" />
      <Node x={108} y={166} w={164} title="Plan proceeds" />
    </svg>
  );
}

/** Evaluation — the result pipeline: objective checks, then the subjective critic, with the revise loop closing it. */
export function EvaluationPipelineDiagram() {
  return (
    <svg viewBox="0 0 380 240" role="img" aria-label="Evaluation pipeline: agent work passes objective checks, then the critic judges it; failures feed a bounded self-revise loop; exhausted rounds land NeedsReview.">
      <Arr />
      <Node x={8} y={8} w={86} title="Agent work" />
      <Flow d="M94 23 L124 23" />
      <Node x={126} y={8} w={100} h={44} title="① checks" sub="commands really run" />
      <Flow d="M226 23 L252 23" />
      <Node x={254} y={8} w={118} h={44} title="③ critic" sub="② by model / agent" tone="purple" />
      <Flow d="M313 52 L313 88" tone="good" />
      <Tag x={321} y={74} text="pass" tone="good" />
      <Node x={254} y={88} w={118} title="Succeeded" tone="good" />
      <Flow d="M270 52 L270 70 L166 70 L166 88" tone="warn" />
      <Flow d="M176 52 L176 70" tone="warn" still />
      <Tag x={196} y={64} text="fail" tone="warn" />
      <Node x={100} y={88} w={132} h={44} title="⑤ self-revise" sub="feed back · 0–3 rounds" tone="warn" />
      <Flow d="M100 110 L52 110 L52 40" tone="warn" />
      <Flow d="M166 132 L166 156 L175 156" tone="warn" still />
      <Node x={177} y={142} w={140} h={28} title="rounds spent → flagged" tone="warn" />
      <Node x={8} y={196} w={364} h={30} title="④ acceptance criteria — the yardstick every judge measures against" tone="dashed" />
    </svg>
  );
}

/** Decision critic — the hard gate on every supervisor move: revise, re-review, then escalate to the human. */
export function DecisionLadderDiagram() {
  return (
    <svg viewBox="0 0 380 214" role="img" aria-label="Decision critic ladder: a flagged decision does not execute — it self-revises, is re-reviewed, and only then escalates to you; approve is a one-shot pass.">
      <Arr />
      <Node x={8} y={8} w={84} title="Decision" sub="spawn · stop …" h={38} />
      <Flow d="M92 27 L122 27" />
      <Node x={124} y={8} w={96} h={38} title="Review" tone="purple" />
      <Flow d="M220 27 L284 27" tone="good" />
      <Tag x={234} y={20} text="pass" tone="good" />
      <Node x={286} y={8} w={86} h={38} title="Execute" tone="good" />
      <Flow d="M156 46 L156 82 L63 82 L63 108" tone="warn" />
      <Tag x={162} y={70} text="flagged (Gate)" tone="warn" />
      <Node x={8} y={108} w={110} h={38} title="① self-revise" />
      <Flow d="M118 127 L142 127" />
      <Node x={144} y={108} w={110} h={38} title="② re-review" tone="purple" />
      <Flow d="M199 108 L199 92 L340 92 L340 46" tone="good" />
      <Flow d="M254 127 L276 127" tone="warn" />
      <Node x={278} y={108} w={94} h={38} title="③ your call" tone="warn" />
      <Flow d="M325 146 L325 168" tone="warn" still />
      <Node x={120} y={168} w={252} h={28} title="approve = one-shot pass · else guidance" tone="dashed" />
    </svg>
  );
}
