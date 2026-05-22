import "@testing-library/jest-dom/vitest";
import { afterEach } from "vitest";
import { cleanup } from "@testing-library/react";

// With `globals: false`, RTL can't auto-register its cleanup on the global afterEach,
// so unmount between tests explicitly — otherwise renders accumulate in the jsdom
// document and queries leak across tests.
afterEach(() => cleanup());
