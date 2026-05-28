import { describe, expect, it } from "vitest";

import type { ConversationSummary } from "@/api/chat";
import type { TeamMemberSummary } from "@/api/teams";

import { conversationTitle } from "./conversationTitle";

const member = (userId: string, name: string): TeamMemberSummary => ({ userId, name, email: `${name}@x`, avatarUrl: null });
const members = new Map([["me", member("me", "Me")], ["alice", member("alice", "Alice")]]);

function conv(partial: Partial<ConversationSummary>): ConversationSummary {
  return {
    id: "c", kind: "Channel", slug: null, name: null, description: null,
    visibility: "Public", archived: false, memberCount: 0, memberUserIds: [], createdDate: "",
    lastMessage: null, lastActivityDate: "",
    ...partial,
  };
}

describe("conversationTitle", () => {
  it("renders a channel as #slug", () => {
    expect(conversationTitle(conv({ kind: "Channel", slug: "general" }), members, "me")).toBe("#general");
  });

  it("renders a group as its name", () => {
    expect(conversationTitle(conv({ kind: "Group", name: "Squad" }), members, "me")).toBe("Squad");
  });

  it("renders a DM as the OTHER participant's name", () => {
    expect(conversationTitle(conv({ kind: "Direct", memberUserIds: ["me", "alice"] }), members, "me")).toBe("Alice");
  });

  it("falls back when the other DM participant isn't in the member map", () => {
    expect(conversationTitle(conv({ kind: "Direct", memberUserIds: ["me", "ghost"] }), members, "me")).toBe("Direct message");
  });
});
