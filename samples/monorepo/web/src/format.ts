/** Formats whole cents as "12.50 EUR" (invariant: a dot, two decimals). */
export function formatMoney(cents: number, currency: string): string {
  if (!Number.isInteger(cents)) {
    throw new Error("cents must be a whole number");
  }
  const sign = cents < 0 ? "-" : "";
  const abs = Math.abs(cents);
  return `${sign}${Math.floor(abs / 100)}.${String(abs % 100).padStart(2, "0")} ${currency}`;
}

/** Lower-case ASCII slug, hyphen separated, like Sample.Core's Slug. */
export function slugify(title: string, maxLength = 60): string {
  const slug = title
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, "-")
    .replace(/^-+|-+$/g, "");
  return slug.length <= maxLength ? slug : slug.slice(0, maxLength).replace(/-+$/, "");
}

/** "1 item", "2 items". */
export function plural(count: number, word: string): string {
  return `${count} ${word}${count === 1 ? "" : "s"}`;
}
