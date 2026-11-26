import { type CartLine, subtotal } from "../cart";
import { plural } from "../format";
import { PriceTag } from "./PriceTag";

export interface CartSummaryProps {
  lines: readonly CartLine[];
  currency: string;
}

/** The cart's lines and subtotal, or an empty-cart message. */
export function CartSummary({ lines, currency }: CartSummaryProps) {
  if (lines.length === 0) {
    return <p className="cart cart--empty">Your cart is empty.</p>;
  }
  const items = lines.reduce((n, l) => n + l.quantity, 0);
  return (
    <section className="cart">
      <h2>{plural(items, "item")}</h2>
      <ul>
        {lines.map((l) => (
          <li key={l.sku} data-sku={l.sku}>
            {l.name} x {l.quantity} <PriceTag cents={l.unitCents * l.quantity} currency={currency} />
          </li>
        ))}
      </ul>
      <p className="cart__subtotal">
        Subtotal <PriceTag cents={subtotal(lines)} currency={currency} />
      </p>
    </section>
  );
}
