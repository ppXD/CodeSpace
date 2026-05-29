import { describe, expect, it } from "vitest";

import type { TeamMemberSummary } from "@/api/teams";

import { findActiveMention, matchMembers, mentionAttributes, serializeEditor } from "./mentionInput";

const member = (userId: string, name: string, email = `${name.toLowerCase()}@x`): TeamMemberSummary => ({ userId, name, email, avatarUrl: null });

describe("serializeEditor", () => {
  function chip(refType: string, refId: string, label: string, text = label): HTMLElement {
    const span = document.createElement("span");
    Object.entries(mentionAttributes(refType, refId, label)).forEach(([k, v]) => span.setAttribute(k, v));
    span.textContent = text;
    return span;
  }

  function editor(...nodes: Array<Node | string>): HTMLElement {
    const root = document.createElement("div");
    nodes.forEach((n) => root.append(typeof n === "string" ? document.createTextNode(n) : n));
    return root;
  }

  it("turns a mention chip into its token, keeping surrounding text", () => {
    const root = editor("hey ", chip("user", "u1", "Alice", "@Alice"), " welcome");
    expect(serializeEditor(root)).toBe("hey <user:u1|Alice> welcome");
  });

  it("is generic over reference type — not hardcoded to user", () => {
    const root = editor("see ", chip("pull_request", "repo#42", "PR #42"));
    expect(serializeEditor(root)).toBe("see <pull_request:repo#42|PR #42>");
  });

  it("serializes a <br> as a newline", () => {
    const root = editor("line one", document.createElement("br"), "line two");
    expect(serializeEditor(root)).toBe("line one\nline two");
  });

  it("recurses into a wrapper element so content isn't dropped", () => {
    const wrapper = document.createElement("div");
    wrapper.append(document.createTextNode("inner "), chip("user", "u2", "Bob", "@Bob"));
    expect(serializeEditor(editor(wrapper))).toBe("inner <user:u2|Bob>");
  });

  it("is empty for an empty editor", () => {
    expect(serializeEditor(document.createElement("div"))).toBe("");
  });
});

describe("findActiveMention", () => {
  it("matches an @ at the start of the text", () => {
    expect(findActiveMention("@al")).toEqual({ query: "al" });
  });

  it("matches an @ after whitespace, returning the query so far", () => {
    expect(findActiveMention("hey @Bo")).toEqual({ query: "Bo" });
    expect(findActiveMention("line1\n@c")).toEqual({ query: "c" });
  });

  it("treats a bare @ as an empty query (offer the whole roster)", () => {
    expect(findActiveMention("hey @")).toEqual({ query: "" });
  });

  it("does not trigger inside an email — the @ isn't at a word boundary", () => {
    expect(findActiveMention("write me@x")).toBeNull();
  });

  it("closes the mention once a space follows", () => {
    expect(findActiveMention("hey @alice ")).toBeNull();
  });
});

describe("matchMembers", () => {
  const roster = [member("1", "Alice"), member("2", "Albert"), member("3", "Bob"), member("4", "Cara")];

  it("ranks name-prefix matches first, alphabetically within a tier", () => {
    expect(matchMembers(roster, "al").map((m) => m.name)).toEqual(["Albert", "Alice"]);
  });

  it("falls back to substring matches", () => {
    expect(matchMembers(roster, "ar").map((m) => m.name)).toEqual(["Cara"]);
  });

  it("lists everyone for an empty query", () => {
    expect(matchMembers(roster, "").map((m) => m.name)).toEqual(["Albert", "Alice", "Bob", "Cara"]);
  });

  it("caps the result count", () => {
    expect(matchMembers(roster, "", 2)).toHaveLength(2);
  });

  it("returns nothing when no name or email matches", () => {
    expect(matchMembers(roster, "zzz")).toEqual([]);
  });
});
