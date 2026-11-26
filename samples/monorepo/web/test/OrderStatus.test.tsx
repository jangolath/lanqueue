import { renderToStaticMarkup } from "react-dom/server";
import { describe, expect, it } from "vitest";
import { OrderStatus } from "../src/components/OrderStatus";

describe("OrderStatus", () => {
  it("marks done, current and todo steps", () => {
    const html = renderToStaticMarkup(<OrderStatus state="paid" />);
    expect(html).toContain('<li class="done">new</li>');
    expect(html).toContain('<li class="current" aria-current="step">paid</li>');
    expect(html).toContain('<li class="todo">delivered</li>');
  });

  it("shows only Cancelled for a cancelled order", () => {
    expect(renderToStaticMarkup(<OrderStatus state="cancelled" />)).toBe('<p class="order order--cancelled">Cancelled</p>');
  });
});
