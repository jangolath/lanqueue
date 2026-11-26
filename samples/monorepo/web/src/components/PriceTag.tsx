import { formatMoney } from "../format";

export interface PriceTagProps {
  cents: number;
  currency: string;
  wasCents?: number;
}

/** A price, with the old price struck through when it was higher. */
export function PriceTag({ cents, currency, wasCents }: PriceTagProps) {
  const reduced = wasCents !== undefined && wasCents > cents;
  return (
    <span className={reduced ? "price price--reduced" : "price"}>
      {reduced && <s className="price__was">{formatMoney(wasCents, currency)}</s>}
      <strong>{formatMoney(cents, currency)}</strong>
    </span>
  );
}
