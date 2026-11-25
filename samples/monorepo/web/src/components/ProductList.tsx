import { slugify } from "../format";
import { PriceTag } from "./PriceTag";

export interface Product {
  sku: string;
  name: string;
  cents: number;
  tags: readonly string[];
}

/** Products sorted by name, each linking to its slug, filtered by an optional tag. */
export function ProductList({ products, currency, tag }: { products: readonly Product[]; currency: string; tag?: string }) {
  const shown = products.filter((p) => tag === undefined || p.tags.includes(tag)).sort((a, b) => a.name.localeCompare(b.name, "en"));
  if (shown.length === 0) {
    return <p className="products products--none">No products.</p>;
  }
  return (
    <ul className="products">
      {shown.map((p) => (
        <li key={p.sku}>
          <a href={`/p/${slugify(p.name)}`}>{p.name}</a> <PriceTag cents={p.cents} currency={currency} />
        </li>
      ))}
    </ul>
  );
}
