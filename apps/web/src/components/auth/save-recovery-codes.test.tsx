import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

import {
  serveRecordedReplies,
  type RecordedReply,
  type RecordedWeir,
} from "../../test/recorded-weir";
import {
  csrfToken,
  recoveryCodesConfirmed,
  setupCodes,
} from "../../test/recovery-replies";
import { SaveRecoveryCodes } from "./save-recovery-codes";

const saveBlobAs = vi.fn();
vi.mock("../../lib/ui/save-file", () => ({
  saveBlobAs: (...args: unknown[]) => saveBlobAs(...args),
}));

const CSRF = "GET /api/v1/auth/csrf";
const CONFIRM = "POST /api/v1/auth/recovery-codes/confirm";

let weir: RecordedWeir | undefined;

afterEach(() => {
  cleanup();
  expect(weir?.unrecorded ?? []).toEqual([]);
  weir = undefined;
  saveBlobAs.mockReset();
  vi.unstubAllGlobals();
});

function renderCodes(
  confirm: RecordedReply | RecordedReply[] | (() => Promise<RecordedReply>),
  onSaved = vi.fn(),
) {
  weir = serveRecordedReplies({ [CSRF]: csrfToken, [CONFIRM]: confirm });
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
  });
  render(
    <QueryClientProvider client={client}>
      <SaveRecoveryCodes codes={setupCodes} onSaved={onSaved} />
    </QueryClientProvider>,
  );
  return onSaved;
}

describe("the recovery codes, shown once", () => {
  it("lists every code, says they are shown once and that without them and the password there is no way back", () => {
    renderCodes(recoveryCodesConfirmed);

    const items = screen
      .getAllByRole("listitem")
      .map((item) => item.textContent);
    expect(items).toEqual(setupCodes);
    expect(screen.getByRole("list", { name: "Recovery codes" })).toBeVisible();
    expect(screen.getByText(/Weir shows these codes only once/)).toBeVisible();
    expect(
      screen.getByText(
        "If you lose your password and every recovery code, there is no way back in. The only way forward is a fresh install with a new data folder.",
      ),
    ).toBeVisible();
  });

  it("does not continue until the person says they saved them, then tells Weir and carries on", async () => {
    const onSaved = renderCodes(recoveryCodesConfirmed);
    const next = screen.getByRole("button", { name: "Continue" });
    expect(next).toBeDisabled();
    fireEvent.click(next);
    expect(weir?.sent("POST", "/api/v1/auth/recovery-codes/confirm")).toEqual(
      [],
    );

    fireEvent.click(screen.getByLabelText("I have saved these codes"));
    expect(next).toBeEnabled();
    fireEvent.click(next);

    await vi.waitFor(() => expect(onSaved).toHaveBeenCalledTimes(1));
    expect(weir?.sent("POST", "/api/v1/auth/recovery-codes/confirm")).toEqual([
      expect.objectContaining({
        body: {
          csrf_token: "csrf.anonymous.auMOaA.2XNpaZ88IDzKnV9V3SO4ykv4tlo",
        },
      }),
    ]);
  });

  it("can be unticked again, which stops it continuing", () => {
    renderCodes(recoveryCodesConfirmed);
    const check = screen.getByLabelText("I have saved these codes");

    fireEvent.click(check);
    fireEvent.click(check);

    expect(screen.getByRole("button", { name: "Continue" })).toBeDisabled();
  });

  it("says it is saving while Weir answers and cannot be pressed twice", async () => {
    let answer: (reply: RecordedReply) => void = () => {};
    const onSaved = renderCodes(
      () => new Promise<RecordedReply>((resolve) => (answer = resolve)),
    );
    fireEvent.click(screen.getByLabelText("I have saved these codes"));

    fireEvent.click(screen.getByRole("button", { name: "Continue" }));

    const busy = await screen.findByRole("button", { name: "Saving…" });
    expect(busy).toBeDisabled();
    expect(screen.getByLabelText("I have saved these codes")).toBeDisabled();
    answer(recoveryCodesConfirmed);
    await vi.waitFor(() => expect(onSaved).toHaveBeenCalledTimes(1));
    expect(
      weir?.sent("POST", "/api/v1/auth/recovery-codes/confirm"),
    ).toHaveLength(1);
  });

  it("stays on the codes and says why when Weir cannot be told, and tries again", async () => {
    const onSaved = renderCodes([
      { status: 500, body: '{"detail":"Something went wrong inside Weir."}' },
      recoveryCodesConfirmed,
    ]);
    fireEvent.click(screen.getByLabelText("I have saved these codes"));

    fireEvent.click(screen.getByRole("button", { name: "Continue" }));

    expect(await screen.findByRole("alert")).toBeVisible();
    expect(onSaved).not.toHaveBeenCalled();
    expect(screen.getByRole("button", { name: "Continue" })).toBeEnabled();
    fireEvent.click(screen.getByRole("button", { name: "Continue" }));
    await vi.waitFor(() => expect(onSaved).toHaveBeenCalledTimes(1));
  });

  it("copies the codes, one to a line", async () => {
    const writeText = vi.fn().mockResolvedValue(undefined);
    vi.stubGlobal("navigator", { clipboard: { writeText } });
    renderCodes(recoveryCodesConfirmed);

    fireEvent.click(
      screen.getByRole("button", { name: "Copy the recovery codes" }),
    );

    await vi.waitFor(() =>
      expect(writeText).toHaveBeenCalledWith(setupCodes.join("\n")),
    );
  });

  it("downloads the codes as a text file with a short note and one code to a line", async () => {
    renderCodes(recoveryCodesConfirmed);

    fireEvent.click(screen.getByRole("button", { name: "Download as text" }));

    expect(saveBlobAs).toHaveBeenCalledTimes(1);
    const [blob, name] = saveBlobAs.mock.calls[0] as [Blob, string];
    expect(name).toBe("weir-recovery-codes.txt");
    expect(blob.type).toBe("text/plain");
    expect(await blob.text()).toBe(
      [
        "Weir recovery codes",
        "Each code works once. Keep this file somewhere safe and private.",
        "",
        ...setupCodes,
        "",
      ].join("\n"),
    );
  });
});
