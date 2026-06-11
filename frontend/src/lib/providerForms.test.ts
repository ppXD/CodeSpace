import { describe, expect, it } from "vitest";

import { PROVIDER_FORMS, providerForm } from "./providerForms";

describe("providerForms", () => {
  it("has a unique provider tag per form", () => {
    const tags = PROVIDER_FORMS.map(f => f.provider);
    expect(new Set(tags).size).toBe(tags.length);
  });

  it("maps every field key onto an AddModelCredentialInput field", () => {
    const allowed = new Set(["apiKey", "baseUrl"]);
    for (const form of PROVIDER_FORMS)
      for (const field of form.fields) expect(allowed.has(field.key)).toBe(true);
  });

  it("renders a required secret key + an optional advanced base URL for a direct provider", () => {
    const anthropic = providerForm("Anthropic")!;
    expect(anthropic.keyless).toBe(false);

    const key = anthropic.fields.find(f => f.key === "apiKey")!;
    expect(key.secret).toBe(true);
    expect(key.required).toBe(true);

    const baseUrl = anthropic.fields.find(f => f.key === "baseUrl")!;
    expect(baseUrl.secret).toBe(false);
    expect(baseUrl.required).toBe(false);
    expect(baseUrl.advanced).toBe(true);
  });

  it("renders no secret field and a required base URL for a keyless provider", () => {
    const ollama = providerForm("Ollama")!;
    expect(ollama.keyless).toBe(true);
    expect(ollama.fields.some(f => f.secret)).toBe(false);

    const baseUrl = ollama.fields.find(f => f.key === "baseUrl")!;
    expect(baseUrl.required).toBe(true);
  });

  it("requires both a token and a base URL for the custom gateway", () => {
    const custom = providerForm("Custom")!;
    expect(custom.fields.find(f => f.key === "apiKey")).toMatchObject({ secret: true, required: true });
    expect(custom.fields.find(f => f.key === "baseUrl")).toMatchObject({ required: true });
  });

  it("looks up case-insensitively and returns undefined for an unknown provider", () => {
    expect(providerForm("openrouter")?.provider).toBe("OpenRouter");
    expect(providerForm("Bedrock")).toBeUndefined();
  });
});
