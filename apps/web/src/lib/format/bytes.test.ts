import { expect, it } from "vitest";

import { formatBytes } from "./bytes";

it("uses binary units with just enough decimals", () => {
  expect(formatBytes(0)).toBe("0 B");
  expect(formatBytes(512)).toBe("512 B");
  expect(formatBytes(1536)).toBe("1.5 KB");
  expect(formatBytes(318 * 1024 * 1024)).toBe("318 MB");
  expect(formatBytes(2.5 * 1024 ** 3)).toBe("2.50 GB");
});

it("keeps the sign and says nothing for a missing size", () => {
  expect(formatBytes(-2048)).toBe("-2.0 KB");
  expect(formatBytes(null)).toBe("");
  expect(formatBytes(undefined)).toBe("");
  expect(formatBytes(Number.NaN)).toBe("");
});

it("moves a size that rounds up to 1024 into the next unit", () => {
  expect(formatBytes(1024 * 1024 - 5)).toBe("1.0 MB");
  expect(formatBytes(1024 ** 3 - 1)).toBe("1.00 GB");
  expect(formatBytes(1024 ** 2 - 1024 * 0.4)).toBe("1.0 MB");
  expect(formatBytes(1024 ** 4 - 1)).toBe("1.00 TB");
  expect(formatBytes(1023 * 1024)).toBe("1023 KB");
});
