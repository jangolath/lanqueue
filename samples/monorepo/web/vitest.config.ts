import { defineConfig } from "vitest/config";

// Node environment: components are rendered to strings with react-dom/server, so no DOM package is needed.
export default defineConfig({
  esbuild: { jsx: "automatic" },
  test: {
    environment: "node",
    include: ["test/**/*.test.{ts,tsx}"],
  },
});
