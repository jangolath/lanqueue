import { describe, expect, it } from "vitest";
import { subtotal } from "../src/cart";

// The one slow file (about 10 seconds): tests in a file run one after another, so lanq split and shard timing can see it.
const pause = (ms: number) => new Promise((resolve) => setTimeout(resolve, ms));

describe("quarterly report (slow)", () => {
  it.each([1, 2, 3, 4, 5])("quarter %i", async (q) => {
    await pause(2000);
    expect(subtotal([{ sku: `Q${q}`, name: "x", unitCents: 100 * q, quantity: 3 }])).toBe(300 * q);
  }, 10_000);
});
