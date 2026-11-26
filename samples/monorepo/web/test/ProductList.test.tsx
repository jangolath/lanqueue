import { renderToStaticMarkup } from "react-dom/server";
import { describe, expect, it } from "vitest";
import { type Product, ProductList } from "../src/components/ProductList";

const products: Product[] = [
  { sku: "B-2", name: "Bucket", cents: 799, tags: ["garden"] },
  { sku: "A-1", name: "Anvil Large", cents: 4999, tags: ["iron"] },
];

describe("ProductList", () => {
  it("sorts by name and links to the slug", () => {
    const html = renderToStaticMarkup(<ProductList products={products} currency="EUR" />);
    expect(html.indexOf("Anvil")).toBeLessThan(html.indexOf("Bucket"));
    expect(html).toContain('href="/p/anvil-large"');
  });

  it("filters by tag", () => {
    const html = renderToStaticMarkup(<ProductList products={products} currency="EUR" tag="garden" />);
    expect(html).toContain("Bucket");
    expect(html).not.toContain("Anvil");
  });

  it("says when nothing matches", () => {
    expect(renderToStaticMarkup(<ProductList products={products} currency="EUR" tag="none" />)).toContain("No products.");
  });
});
