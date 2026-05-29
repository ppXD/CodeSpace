import { useRef, useState, type KeyboardEvent } from "react";

/**
 * Message input. The textarea and Send sit inside one bordered box (focus ring on the box) so
 * they always line up — Send bottom-right, the textarea growing above it. Enter sends;
 * Shift+Enter inserts a newline. Whitespace-only can't be sent. The parent owns the mutation and
 * passes <c>disabled</c> while a send is in flight.
 */
export function MessageComposer({ onSend, disabled, placeholder }: { onSend: (body: string) => void; disabled?: boolean; placeholder?: string }) {
  const [text, setText] = useState("");

  // True while an IME composition is in flight (e.g. picking a Chinese/Japanese/Korean candidate).
  // The Enter that commits the candidate must NOT send — it's finishing the input, not submitting.
  const composing = useRef(false);

  const send = () => {
    const body = text.trim();
    if (!body || disabled) return;

    onSend(body);
    setText("");
  };

  const onKeyDown = (e: KeyboardEvent<HTMLTextAreaElement>) => {
    // Skip while composing (tracked event) or when the browser still reports this keydown as part
    // of the composition (isComposing) — some platforms surface the commit Enter only one of the two ways.
    if (e.key === "Enter" && !e.shiftKey && !composing.current && !e.nativeEvent.isComposing) {
      e.preventDefault();
      send();
    }
  };

  return (
    <div className="chat-composer-wrap">
      <div className="chat-composer">
        <textarea
          className="chat-composer-input"
          value={text}
          rows={1}
          placeholder={placeholder ?? "Write a message…"}
          onChange={(e) => setText(e.target.value)}
          onKeyDown={onKeyDown}
          onCompositionStart={() => { composing.current = true; }}
          onCompositionEnd={() => { composing.current = false; }}
        />
        <button className="btn btn-primary chat-composer-send" onClick={send} disabled={disabled || text.trim().length === 0} aria-label="Send">
          Send
        </button>
      </div>
    </div>
  );
}
