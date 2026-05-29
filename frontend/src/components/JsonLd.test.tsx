import { describe, it, expect } from "vitest";
import { render } from "@testing-library/react";
import { JsonLd } from "./JsonLd";

describe("JsonLd", () => {
  it("escapes < so a string value cannot break out of the <script> tag (XSS guard)", () => {
    // This is the load-bearing security control: JSON.stringify does NOT
    // sanitize, so a `</script>` inside any value would otherwise close the
    // tag. Pin that the escaping neutralizes it.
    const { container } = render(
      <JsonLd data={{ "@type": "Thing", name: "</script><script>alert(1)</script>" }} />,
    );
    const script = container.querySelector('script[type="application/ld+json"]');
    expect(script).toBeTruthy();
    const raw = script!.innerHTML;
    expect(raw).not.toMatch(/<\/script>/i);
    expect(raw).not.toMatch(/<script/i);
    expect(raw).toContain("\\u003c");
    // …and the value still round-trips back intact for consumers.
    const parsed = JSON.parse(script!.textContent ?? "{}") as { name?: string };
    expect(parsed.name).toBe("</script><script>alert(1)</script>");
  });

  it("renders one <script> per node when given an array", () => {
    const { container } = render(<JsonLd data={[{ "@type": "A" }, { "@type": "B" }]} />);
    expect(container.querySelectorAll('script[type="application/ld+json"]').length).toBe(2);
  });
});
