export type OrderState = "new" | "paid" | "shipped" | "delivered" | "cancelled";

const steps: readonly OrderState[] = ["new", "paid", "shipped", "delivered"];

/** A step list with the current state marked; a cancelled order shows only that. */
export function OrderStatus({ state }: { state: OrderState }) {
  if (state === "cancelled") {
    return <p className="order order--cancelled">Cancelled</p>;
  }
  const at = steps.indexOf(state);
  return (
    <ol className="order">
      {steps.map((s, i) => (
        <li key={s} className={i < at ? "done" : i === at ? "current" : "todo"} aria-current={i === at ? "step" : undefined}>
          {s}
        </li>
      ))}
    </ol>
  );
}
