// @vitest-environment node
/// <reference types="node" />
import { mkdirSync, writeFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { expect, it } from "vitest";

import { buildFieldSchema, type InputFieldDraft } from "./inputFieldSchema";

it("exports all seven real editor field schemas for the backend contract", () => {
  const drafts: Record<string, InputFieldDraft> = {
    text: { type: "text", maxLength: 5, hidden: true },
    paragraph: { type: "paragraph", maxLength: 20 },
    select: { type: "select", options: ["a", "b"] },
    number: { type: "number" },
    boolean: { type: "boolean" },
    repository: { type: "repository" },
    conversation: { type: "conversation" },
  };
  const properties = Object.fromEntries(Object.entries(drafts).map(([name, draft]) => [name, buildFieldSchema(draft)]));
  const schema = { type: "object", properties, required: Object.keys(properties), additionalProperties: false };
  expect(properties.text).toEqual({ type: "string", maxLength: 5, "x-hidden": true });
  expect(properties.paragraph).toEqual({ type: "string", maxLength: 20, "x-long": true });
  expect(properties.select).toEqual({ type: "string", enum: ["a", "b"] });
  const destination = fileURLToPath(new URL("../../../tmp/", import.meta.url));
  mkdirSync(destination, { recursive: true });
  writeFileSync(`${destination}/external-trigger-schema-fixture.json`, JSON.stringify(schema));
});
