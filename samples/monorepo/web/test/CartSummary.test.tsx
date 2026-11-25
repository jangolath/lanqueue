import { renderToStaticMarkup } from "react-dom/server";
import { describe, expect, it } from "vitest";
import { CartSummary } from "../src/components/CartSummary";

describe("CartSummary", () => {
  it("says when the cart is empty", () => {
    expect(renderToStaticMarkup(<CartSummary lines={[]} currency="EUR" />)).toContain("Your cart is empty.");
  });

  it("lists lines, counts items and sums", () => {
    const html = renderToStaticMarkup(
      <CartSummary
        lines={[
          { sku: "A-1", name: "Anvil", unitCents: 1000, quantity: 2 },
          { sku: "B-2", name: "Bucket", unitCents: 250, quantity: 1 },
        ]}
        currency="EUR"
      />,
    );
    expect(html).toContain("<h2>3 items</h2>");
    expect(html).toContain('data-sku="A-1"');
    expect(html).toContain("22.50 EUR");
  });
});
