/**
 * Renders structured data as an inline `<script type="application/ld+json">`.
 *
 * Per Next.js's official JSON-LD guidance we use a native `<script>` (NOT
 * `next/script`, which is for executable JS — this is data) and escape `<` to
 * its unicode form `<`. `JSON.stringify` does NOT sanitize against XSS, so
 * a `<` inside any string value could otherwise break out of the script tag.
 * The escaping is unconditional and lives here so no caller has to remember it
 * — the same "treat rendered values as untrusted" discipline the public portal
 * routes follow (see CLAUDE.md).
 *
 * Accepts a single node or an array; an array renders one `<script>` per node,
 * which Google parses more reliably than a single bag of mixed types.
 */
import type { JsonLdData } from "@/lib/structured-data";

function serialize(data: JsonLdData): string {
  return JSON.stringify(data).replace(/</g, "\\u003c");
}

export function JsonLd({ data }: { data: JsonLdData | readonly JsonLdData[] }) {
  const nodes = Array.isArray(data) ? data : [data as JsonLdData];
  return (
    <>
      {nodes.map((node, index) => (
        <script
          // Index keys are stable here: the array is a fixed, ordered list of
          // structured-data nodes built at render time, never reordered.
          key={index}
          type="application/ld+json"
          dangerouslySetInnerHTML={{ __html: serialize(node) }}
        />
      ))}
    </>
  );
}

export default JsonLd;
