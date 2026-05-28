import { describe, expect, it } from "vitest";

import { parseMessageBody } from "./messageReferences";

/**
 * The display-side tokenizer must agree with the backend grammar AND preserve every
 * occurrence in order (chips render inline, so no dedup). These pin the segment shape the
 * MessageBody renderer depends on.
 */
describe("parseMessageBody", () => {
  it("returns nothing for an empty body", () => {
    expect(parseMessageBody("")).toEqual([]);
  });

  it("returns a single text segment when there are no tokens", () => {
    expect(parseMessageBody("just plain text")).toEqual([{ kind: "text", text: "just plain text" }]);
  });

  it("splits text around a reference and keeps the label", () => {
    expect(parseMessageBody("hi <user:u1|Alice> there")).toEqual([
      { kind: "text", text: "hi " },
      { kind: "ref", refType: "user", refId: "u1", label: "Alice" },
      { kind: "text", text: " there" },
    ]);
  });

  it("normalizes a missing/empty label to null", () => {
    expect(parseMessageBody("<workflow:wf9>")).toEqual([{ kind: "ref", refType: "workflow", refId: "wf9", label: null }]);
    expect(parseMessageBody("<user:x|>")).toEqual([{ kind: "ref", refType: "user", refId: "x", label: null }]);
  });

  it("keeps colons in a code-location refid and a PR hash", () => {
    const segs = parseMessageBody("<code_location:repo:sha:src/F.cs:42|F.cs:42> and <pull_request:repo#7|PR 7>");
    expect(segs).toEqual([
      { kind: "ref", refType: "code_location", refId: "repo:sha:src/F.cs:42", label: "F.cs:42" },
      { kind: "text", text: " and " },
      { kind: "ref", refType: "pull_request", refId: "repo#7", label: "PR 7" },
    ]);
  });

  it("preserves EVERY occurrence (no dedup — each mention is its own chip)", () => {
    const segs = parseMessageBody("<user:u1|A> <user:u1|A>");
    expect(segs.filter(s => s.kind === "ref")).toHaveLength(2);
  });

  it("leaves malformed tokens as plain text", () => {
    // No colon, uppercase type, unclosed — none are references.
    const body = "<user> <User:x> <user:x";
    expect(parseMessageBody(body)).toEqual([{ kind: "text", text: body }]);
  });

  it("does not leak regex state across calls", () => {
    // A shared /g RegExp would carry lastIndex between calls and drop the leading match
    // on the second call. Each call must start clean.
    const first = parseMessageBody("<user:a|A>");
    const second = parseMessageBody("<user:b|B>");
    expect(first).toEqual([{ kind: "ref", refType: "user", refId: "a", label: "A" }]);
    expect(second).toEqual([{ kind: "ref", refType: "user", refId: "b", label: "B" }]);
  });
});
