import { describe, expect, it } from "vitest";

import { endpointChanged, isHttpsUpgrade, keyMustBeReentered, providerEndpointMoves, providerSecretMustBeReentered, showsMaskedCredentials } from "./credentialEndpoint";

describe("endpointChanged", () => {
  it.each([
    ["https://gw.example/v1", "https://gw.example/v2"],
    ["https://gw.example/v1", "https://GW.example:443/v1"],
    ["https://***:***@gw.example/v1?api-key=***", "https://other:creds@gw.example/v1"],
    [null, ""],
    ["", null],
    ["gw.example:4000", "gw.example:4000"],
  ])("keeps %s → %s on the same endpoint (scheme, host, port)", (original, next) => {
    expect(endpointChanged(original, next)).toBe(false);
  });

  it.each([
    ["https://gw.example/v1", "https://attacker.example/v1"],
    ["https://gw.example/v1", "http://gw.example/v1"],
    ["https://gw.example/v1", "https://gw.example:8443/v1"],
    [null, "https://gw.example/v1"],
    ["https://gw.example/v1", ""],
    ["gw.example:4000", "gw.example:5000"],
  ])("moves %s → %s to another endpoint", (original, next) => {
    expect(endpointChanged(original, next)).toBe(true);
  });
});

describe("isHttpsUpgrade", () => {
  it.each([
    ["http://gitlab.acme.test", "https://gitlab.acme.test"],
    ["http://gitlab.acme.test:80", "https://gitlab.acme.test:443"],
    ["http://gitlab.acme.test:8080", "https://gitlab.acme.test:8080"],
    ["http://GitLab.Acme.test/", "https://gitlab.acme.test/gitlab"],
  ])("treats %s → %s as an upgrade of the same host", (original, next) => {
    expect(isHttpsUpgrade(original, next)).toBe(true);
  });

  it.each([
    ["http://gitlab.acme.test", "https://gitlab.acme.test:8443"],
    ["http://gitlab.acme.test:8080", "https://gitlab.acme.test"],
    ["http://gitlab.acme.test:443", "https://gitlab.acme.test"],
    ["http://gitlab.acme.test", "https://gitlab-alias.acme.test"],
    ["https://gitlab.acme.test", "http://gitlab.acme.test"],
    ["https://gitlab.acme.test", "https://gitlab.acme.test"],
    ["gitlab.acme.test", "https://gitlab.acme.test"],
    [null, "https://gitlab.acme.test"],
    ["http://gitlab.acme.test", ""],
  ])("does not treat %s → %s as one", (original, next) => {
    expect(isHttpsUpgrade(original, next)).toBe(false);
  });
});

describe("providerEndpointMoves", () => {
  it("is a change of endpoint bar an http to https upgrade of the same host", () => {
    expect(providerEndpointMoves("http://gitlab.acme.test", "https://gitlab.acme.test")).toBe(false);
    expect(providerEndpointMoves("http://gitlab.acme.test", "https://gitlab.acme.test:8443")).toBe(true);
    expect(providerEndpointMoves("https://gitlab.acme.test", "https://attacker.test")).toBe(true);
    expect(providerEndpointMoves("https://gitlab.acme.test", "https://gitlab.acme.test/gitlab")).toBe(false);
  });
});

describe("keyMustBeReentered", () => {
  it("asks for the key when a stored key would follow the endpoint somewhere new", () => {
    expect(keyMustBeReentered({ keyHint: "····a1b2", keyUnreadable: false, baseUrl: "https://gw.example/v1" }, "https://attacker.example/v1")).toBe(true);
  });

  it("asks for it for an unreadable stored key too — it is still a stored key", () => {
    expect(keyMustBeReentered({ keyHint: null, keyUnreadable: true, baseUrl: "https://gw.example/v1" }, "https://other.example/v1")).toBe(true);
  });

  it("never asks for a keyless credential", () => {
    expect(keyMustBeReentered({ keyHint: null, keyUnreadable: false, baseUrl: "http://localhost:11434" }, "http://ollama.internal:11434")).toBe(false);
  });

  it("never asks for a path-only change", () => {
    expect(keyMustBeReentered({ keyHint: "····a1b2", keyUnreadable: false, baseUrl: "https://gw.example/v1" }, "https://gw.example/v2")).toBe(false);
  });
});

describe("providerSecretMustBeReentered", () => {
  it("asks for the OAuth secret when a stored one would follow the provider to another host", () => {
    expect(providerSecretMustBeReentered({ oauthEnabled: true, baseUrl: "https://gitlab.acme.test" }, "https://attacker.test")).toBe(true);
  });

  it("never asks when no secret is stored", () => {
    expect(providerSecretMustBeReentered({ oauthEnabled: false, baseUrl: "https://gitlab.acme.test" }, "https://gitlab.new.test")).toBe(false);
  });

  it("never asks for an http to https upgrade of the same host — the backend lets it through while tokens are connected", () => {
    expect(providerSecretMustBeReentered({ oauthEnabled: true, baseUrl: "http://gitlab.acme.test" }, "https://gitlab.acme.test")).toBe(false);
    expect(providerSecretMustBeReentered({ oauthEnabled: true, baseUrl: "http://gitlab.acme.test" }, "https://gitlab.acme.test:8443")).toBe(true);
  });

  it("never asks for a path change or the masked URL sent back", () => {
    expect(providerSecretMustBeReentered({ oauthEnabled: true, baseUrl: "https://gitlab.acme.test" }, "https://gitlab.acme.test/gitlab")).toBe(false);
    expect(providerSecretMustBeReentered({ oauthEnabled: true, baseUrl: "https://***:***@gitlab.acme.test" }, "https://***:***@gitlab.acme.test")).toBe(false);
  });
});

describe("showsMaskedCredentials", () => {
  it.each([
    ["https://***:***@gitlab.acme.test", true],
    ["https://gitlab.acme.test/api?private_token=***", true],
    ["https://gitlab.acme.test", false],
    [null, false],
  ])("reads %s as carrying masked credentials: %s", (url, expected) => {
    expect(showsMaskedCredentials(url)).toBe(expected);
  });
});
