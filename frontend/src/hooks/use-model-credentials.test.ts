import { describe, expect, it } from "vitest";

import { MAX_PRICE_PER_MILLION_USD } from "@/api/modelCredentials";

import { completePrice, parsePrice, priceFieldIssue } from "./use-model-credentials";

/**
 * The pure price helpers behind the model manager. They decide what reaches the API, and the API rejects a
 * one-sided price outright — so a mistake here is not cosmetic: the editor reconciles a renamed row as
 * remove-then-add in ONE Promise.all, so sending a half price deletes the row and then fails the add, destroying a
 * model the operator only meant to rename.
 */
describe("model price helpers", () => {
  describe("parsePrice", () => {
    it.each([
      ["", null],
      ["   ", null],
      [undefined, null],
      ["0", 0],
      ["2", 2],
      ["0.075", 0.075],
    ])("reads %o as %o", (raw, expected) => {
      expect(parsePrice(raw as string | undefined)).toBe(expected);
    });

    it.each(["abc", "-1", "1e30", `${MAX_PRICE_PER_MILLION_USD + 1}`])("treats %o as unpriced rather than guessing", raw => {
      // Never 0: a $0 model would read as FREE and defeat the cap it is supposed to make enforceable.
      expect(parsePrice(raw)).toBeNull();
    });

    it("accepts the ceiling itself", () => {
      expect(parsePrice(`${MAX_PRICE_PER_MILLION_USD}`)).toBe(MAX_PRICE_PER_MILLION_USD);
    });
  });

  describe("completePrice", () => {
    it("keeps a complete pair", () => {
      expect(completePrice({ inputUsdPerMillion: "2", outputUsdPerMillion: "10" }))
        .toEqual({ inputUsdPerMillion: 2, outputUsdPerMillion: 10 });
    });

    it.each([
      ["input only", { inputUsdPerMillion: "2", outputUsdPerMillion: "" }],
      ["output only", { inputUsdPerMillion: "", outputUsdPerMillion: "10" }],
      ["a valid half and an invalid half", { inputUsdPerMillion: "2", outputUsdPerMillion: "abc" }],
    ])("drops %s — half a price prices nothing, and the API rejects it", (_label, row) => {
      expect(completePrice(row)).toEqual({ inputUsdPerMillion: null, outputUsdPerMillion: null });
    });

    it("carries a zero pair through — priced-and-free is a real answer, distinct from unpriced", () => {
      expect(completePrice({ inputUsdPerMillion: "0", outputUsdPerMillion: "0" }))
        .toEqual({ inputUsdPerMillion: 0, outputUsdPerMillion: 0 });
    });
  });

  describe("priceFieldIssue", () => {
    it("is silent on blank and on a valid pair", () => {
      expect(priceFieldIssue({})).toBeNull();
      expect(priceFieldIssue({ inputUsdPerMillion: "2", outputUsdPerMillion: "10" })).toBeNull();
    });

    it.each([
      ["two dollars", /must be a number/i],
      ["-3", /cannot be negative/i],
      [`${MAX_PRICE_PER_MILLION_USD + 1}`, /too large/i],
    ])("names what is wrong with %o", (raw, pattern) => {
      expect(priceFieldIssue({ inputUsdPerMillion: raw })).toMatch(pattern);
    });

    it("names the OUTPUT field when that is the bad one", () => {
      expect(priceFieldIssue({ inputUsdPerMillion: "2", outputUsdPerMillion: "nope" })).toMatch(/\$\/M out/);
    });
  });
});
