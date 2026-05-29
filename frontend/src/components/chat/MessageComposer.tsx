import { useRef, useState, type ClipboardEvent, type KeyboardEvent } from "react";

import type { TeamMemberSummary } from "@/api/teams";
import { useTeamMembers } from "@/hooks/use-team-members";
import { findActiveMention, matchMembers, mentionAttributes, serializeEditor } from "@/lib/mentionInput";

import { MentionPicker } from "./MentionPicker";

/**
 * Message input. A contenteditable surface (not a textarea) so an @-mention is an inline,
 * non-editable chip carrying the structured reference, while the editor + Send still share one
 * bordered box. Typing `@` opens the member picker; choosing one inserts a chip that serializes to
 * the generic `<user:id|label>` token on send. Enter sends (or picks the highlighted member when
 * the picker is open); Shift+Enter newlines; an IME composition's commit-Enter never sends. The
 * parent owns the mutation and passes <c>disabled</c> while a send is in flight.
 */
interface PickerState {
  query: string;
  candidates: TeamMemberSummary[];
  index: number;
}

export function MessageComposer({ onSend, disabled, placeholder }: { onSend: (body: string) => void; disabled?: boolean; placeholder?: string }) {
  const editorRef = useRef<HTMLDivElement | null>(null);
  const composing = useRef(false);
  const [empty, setEmpty] = useState(true);
  const [picker, setPicker] = useState<PickerState | null>(null);

  const roster = useTeamMembers().data ?? [];

  const refreshEmpty = () => setEmpty((editorRef.current?.textContent ?? "").trim().length === 0);

  const syncPicker = () => {
    const query = activeQueryAtCaret(editorRef.current);
    const candidates = query == null ? [] : matchMembers(roster, query);

    if (query == null || candidates.length === 0) {
      setPicker(null);
      return;
    }

    setPicker((prev) => ({ query, candidates, index: prev?.query === query ? Math.min(prev.index, candidates.length - 1) : 0 }));
  };

  const onInput = () => {
    refreshEmpty();
    syncPicker();
  };

  const insert = (member: TeamMemberSummary) => {
    insertMentionChip(editorRef.current, member);
    setPicker(null);
    refreshEmpty();
  };

  const submit = () => {
    const editor = editorRef.current;
    if (!editor) return;

    const body = serializeEditor(editor).trim();
    if (!body || disabled) return;

    onSend(body);
    editor.innerHTML = "";
    setEmpty(true);
    setPicker(null);
  };

  const onKeyDown = (e: KeyboardEvent<HTMLDivElement>) => {
    if (picker) {
      if (e.key === "ArrowDown") { e.preventDefault(); setPicker(p => (p ? { ...p, index: (p.index + 1) % p.candidates.length } : p)); return; }
      if (e.key === "ArrowUp") { e.preventDefault(); setPicker(p => (p ? { ...p, index: (p.index - 1 + p.candidates.length) % p.candidates.length } : p)); return; }
      if (e.key === "Enter" || e.key === "Tab") { e.preventDefault(); insert(picker.candidates[picker.index]); return; }
      if (e.key === "Escape") { e.preventDefault(); setPicker(null); return; }
    }

    // Enter sends — unless Shift (newline) or an IME composition is committing its candidate.
    if (e.key === "Enter" && !e.shiftKey && !composing.current && !e.nativeEvent.isComposing) {
      e.preventDefault();
      submit();
    }
  };

  const onPaste = (e: ClipboardEvent<HTMLDivElement>) => {
    e.preventDefault();   // strip rich HTML — the editor only carries text + mention chips
    insertPlainText(e.clipboardData.getData("text/plain"));
    refreshEmpty();
    syncPicker();
  };

  return (
    <div className="chat-composer-wrap">
      {picker && (
        <MentionPicker
          candidates={picker.candidates}
          activeIndex={picker.index}
          onPick={insert}
          onHover={(index) => setPicker(p => (p ? { ...p, index } : p))}
        />
      )}
      <div className="chat-composer">
        <div
          ref={editorRef}
          className="chat-composer-input"
          contentEditable={!disabled}
          suppressContentEditableWarning
          role="textbox"
          aria-multiline="true"
          aria-label="Message"
          data-empty={empty}
          data-placeholder={placeholder ?? "Write a message…"}
          onInput={onInput}
          onKeyDown={onKeyDown}
          onPaste={onPaste}
          onCompositionStart={() => { composing.current = true; }}
          onCompositionEnd={() => { composing.current = false; syncPicker(); }}
        />
        <button className="btn btn-primary chat-composer-send" onClick={submit} disabled={disabled || empty} aria-label="Send">
          Send
        </button>
      </div>
    </div>
  );
}

// ─── Caret-bound DOM glue (browser-only; the pure parsing lives in lib/mentionInput) ─────────────

/** The in-progress @-query at the caret, or null. Reads the text before the caret in the current
 *  text node and defers the grammar to findActiveMention. */
function activeQueryAtCaret(editor: HTMLElement | null): string | null {
  const selection = window.getSelection();
  if (!editor || !selection || selection.rangeCount === 0) return null;

  const { startContainer, startOffset } = selection.getRangeAt(0);
  if (startContainer.nodeType !== Node.TEXT_NODE || !editor.contains(startContainer)) return null;

  const before = (startContainer.textContent ?? "").slice(0, startOffset);
  return findActiveMention(before)?.query ?? null;
}

/** Replace the typed `@query` run at the caret with a non-editable mention chip + a trailing space. */
function insertMentionChip(editor: HTMLElement | null, member: TeamMemberSummary) {
  const selection = window.getSelection();
  if (!editor || !selection || selection.rangeCount === 0) return;

  const range = selection.getRangeAt(0);
  const node = range.startContainer;
  if (node.nodeType !== Node.TEXT_NODE) return;

  const offset = range.startOffset;
  const active = findActiveMention((node.textContent ?? "").slice(0, offset));
  if (!active) return;

  const replace = document.createRange();
  replace.setStart(node, offset - active.query.length - 1);   // include the '@'
  replace.setEnd(node, offset);
  replace.deleteContents();

  const chip = document.createElement("span");
  chip.className = "chat-mention";
  chip.contentEditable = "false";
  Object.entries(mentionAttributes("user", member.userId, member.name)).forEach(([k, v]) => chip.setAttribute(k, v));
  chip.textContent = `@${member.name}`;

  const space = document.createTextNode(" ");   // a plain space after the chip; caret lands here
  replace.insertNode(space);
  replace.insertNode(chip);   // inserts before `space` → [chip][space]

  const after = document.createRange();
  after.setStartAfter(space);
  after.collapse(true);
  selection.removeAllRanges();
  selection.addRange(after);
  editor.focus();
}

/** Insert plain text at the caret (used by paste, which we sanitize to text-only). */
function insertPlainText(text: string) {
  const selection = window.getSelection();
  if (!selection || selection.rangeCount === 0) return;

  const range = selection.getRangeAt(0);
  range.deleteContents();

  const node = document.createTextNode(text);
  range.insertNode(node);
  range.setStartAfter(node);
  range.collapse(true);
  selection.removeAllRanges();
  selection.addRange(range);
}
