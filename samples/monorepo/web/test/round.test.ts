import { describe, expect, it } from "vitest";
import { roundHalfEven } from "../src/cart";

describe("roundHalfEven", () => {
  it.each([
    [0.5, 0],
    [1.5, 2],
    [2.5, 2],
    [2.4, 2],
    [2.6, 3],
    [-0.5, 0],
  ])("%d -> %d", (x, rounded) => {
    expect(roundHalfEven(x)).toBe(rounded);
  });
});
