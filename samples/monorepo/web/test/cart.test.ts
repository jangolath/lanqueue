import { describe, expect, it } from "vitest";
import { addLine, type CartLine, removeLine, subtotal, total } from "../src/cart";

const anvil: CartLine = { sku: "A-1", name: "Anvil", unitCents: 1000, quantity: 2 };
const bucket: CartLine = { sku: "B-2", name: "Bucket", unitCents: 250, quantity: 4 };

describe("cart", () => {
  it("adds new lines and merges the same SKU", () => {
    const lines = addLine(addLine(addLine([], anvil), bucket), { ...anvil, quantity: 1 });
    expect(lines.map((l) => [l.sku, l.quantity])).toEqual([
      ["A-1", 3],
      ["B-2", 4],
    ]);
  });

  it("never changes the input array", () => {
    const lines: CartLine[] = [anvil];
    addLine(lines, bucket);
    expect(lines).toHaveLength(1);
  });

  it("refuses a zero quantity", () => {
    expect(() => addLine([], { ...anvil, quantity: 0 })).toThrow(/positive/);
  });

  it("removes by SKU", () => {
    expect(removeLine([anvil, bucket], "A-1")).toEqual([bucket]);
  });

  it("sums the subtotal", () => {
    expect(subtotal([anvil, bucket])).toBe(3000);
  });

  it("applies a discount", () => {
    expect(total([anvil, bucket], 10)).toBe(2700);
  });

  it("refuses a discount out of range", () => {
    expect(() => total([anvil], 101)).toThrow(RangeError);
  });
});
