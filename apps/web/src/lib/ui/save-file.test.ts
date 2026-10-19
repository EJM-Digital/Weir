import { afterEach, expect, it, vi } from "vitest";

import { saveBlobAs } from "./save-file";

afterEach(() => {
  vi.restoreAllMocks();
  vi.unstubAllGlobals();
});

it("hands the file to the browser's own download under the name given, then lets go of it", () => {
  const blob = new Blob(["log"], { type: "text/plain" });
  const created = vi.fn(() => "blob:weir-download");
  const revoked = vi.fn();
  vi.stubGlobal("URL", {
    createObjectURL: created,
    revokeObjectURL: revoked,
  });
  const clicked: { href: string; download: string }[] = [];
  vi.spyOn(HTMLAnchorElement.prototype, "click").mockImplementation(function (
    this: HTMLAnchorElement,
  ) {
    clicked.push({ href: this.href, download: this.download });
    expect(revoked).not.toHaveBeenCalled();
  });

  saveBlobAs(blob, "weir-log.txt");

  expect(created).toHaveBeenCalledWith(blob);
  expect(clicked).toEqual([
    { href: "blob:weir-download", download: "weir-log.txt" },
  ]);
  expect(revoked).toHaveBeenCalledWith("blob:weir-download");
});
