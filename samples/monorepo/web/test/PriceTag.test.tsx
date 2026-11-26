import { renderToStaticMarkup } from "react-dom/server";
import { describe, expect, it } from "vitest";
import { PriceTag } from "../src/components/PriceTag";

describe("PriceTag", () => {
  it("shows the price", () => {
    expect(renderToStaticMarkup(<PriceTag cents={1250} currency="EUR" />)).toBe('<span class="price"><strong>12.50 EUR</strong></span>');
  });

  it("strikes through a higher old price", () => {
    const html = renderToStaticMarkup(<PriceTag cents={900} currency="EUR" wasCents={1200} />);
    expect(html).toContain("price--reduced");
    expect(html).toContain("<s class=\"price__was\">12.00 EUR</s>");
  });

  it("ignores an old price that was lower", () => {
    expect(renderToStaticMarkup(<PriceTag cents={900} currency="EUR" wasCents={800} />)).not.toContain("price__was");
  });
});
