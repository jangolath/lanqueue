import { describe, expect, it } from "vitest";
import { slugify } from "../src/format";

describe("slugify", () => {
  it.each([
    ["Hello World", "hello-world"],
    ["  --Many   spaces--  ", "many-spaces"],
    ["C# & .NET 10!", "c-net-10"],
    ["", ""],
  ])("%s", (title, slug) => {
    expect(slugify(title)).toBe(slug);
  });

  it("cuts at the max length without a trailing hyphen", () => {
    expect(slugify("abc def", 4)).toBe("abc");
  });
});
