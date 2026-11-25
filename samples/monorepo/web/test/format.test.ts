import { describe, expect, it } from "vitest";
import { formatMoney, plural } from "../src/format";

describe("formatMoney", () => {
  it.each([
    [0, "0.00 EUR"],
    [5, "0.05 EUR"],
    [1250, "12.50 EUR"],
    [-199, "-1.99 EUR"],
  ])("formats %i cents", (cents, text) => {
    expect(formatMoney(cents, "EUR")).toBe(text);
  });

  it("refuses fractions of a cent", () => {
    expect(() => formatMoney(1.5, "EUR")).toThrow();
  });
});

describe("plural", () => {
  it("adds an s except for one", () => {
    expect([0, 1, 2].map((n) => plural(n, "item"))).toEqual(["0 items", "1 item", "2 items"]);
  });
});
