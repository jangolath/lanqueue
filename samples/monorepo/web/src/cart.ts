export interface CartLine {
  sku: string;
  name: string;
  unitCents: number;
  quantity: number;
}

/** Adds a line, or raises the quantity of the same SKU. Returns a new array. */
export function addLine(lines: readonly CartLine[], line: CartLine): CartLine[] {
  if (line.quantity <= 0) {
    throw new Error(`quantity of ${line.sku} must be positive`);
  }
  const existing = lines.find((l) => l.sku === line.sku);
  return existing
    ? lines.map((l) => (l.sku === line.sku ? { ...l, quantity: l.quantity + line.quantity } : l))
    : [...lines, line];
}

export function removeLine(lines: readonly CartLine[], sku: string): CartLine[] {
  return lines.filter((l) => l.sku !== sku);
}

export function subtotal(lines: readonly CartLine[]): number {
  return lines.reduce((sum, l) => sum + l.unitCents * l.quantity, 0);
}

/** Subtotal less a percentage discount, rounded half to even like the .NET side. */
export function total(lines: readonly CartLine[], discountPercent: number): number {
  if (discountPercent < 0 || discountPercent > 100) {
    throw new RangeError("discount must be 0..100");
  }
  const sub = subtotal(lines);
  return sub - roundHalfEven((sub * discountPercent) / 100);
}

export function roundHalfEven(x: number): number {
  const floor = Math.floor(x);
  const diff = x - floor;
  if (diff > 0.5) return floor + 1;
  if (diff < 0.5) return floor;
  return floor % 2 === 0 ? floor : floor + 1;
}
