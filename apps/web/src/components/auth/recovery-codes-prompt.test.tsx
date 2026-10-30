import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import {
  cleanup,
  fireEvent,
  render,
  screen,
  waitFor,
} from "@testing-library/react";
import { MemoryRouter, Route, Routes, useLocation } from "react-router-dom";
import { afterEach, describe, expect, it, vi } from "vitest";

import { recoveryPromptPutAside } from "../../lib/auth/recovery-prompt";
import {
  serveRecordedReplies,
  type RecordedReply,
  type RecordedWeir,
} from "../../test/recorded-weir";
import {
  recoveryCodesNone,
  recoveryCodesNotSaved,
  recoveryCodesSaved,
  signedInAdmin,
  signedInViewer,
} from "../../test/recovery-replies";
import { RecoveryCodesPrompt } from "./recovery-codes-prompt";

const STATUS = "GET /api/v1/auth/recovery-codes";
const ME = "GET /api/v1/auth/me";

let weir: RecordedWeir | undefined;

afterEach(() => {
  cleanup();
  expect(weir?.unrecorded ?? []).toEqual([]);
  weir = undefined;
  window.sessionStorage.clear();
  vi.unstubAllGlobals();
});

function Where() {
  const location = useLocation();
  return <p data-testid="where">{location.pathname + location.search}</p>;
}

function renderPrompt(
  status: RecordedReply,
  entry = "/",
  me: RecordedReply = signedInAdmin,
) {
  weir = serveRecordedReplies({ [ME]: me, [STATUS]: status });
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false } },
  });
  render(
    <QueryClientProvider client={client}>
      <MemoryRouter initialEntries={[entry]}>
        <RecoveryCodesPrompt />
        <Routes>
          <Route path="*" element={<Where />} />
        </Routes>
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

describe("the prompt to make recovery codes after signing in", () => {
  it("asks an account with no codes to make a set, and says what happens if it never does", async () => {
    renderPrompt(recoveryCodesNone);

    const dialog = await screen.findByRole("dialog", {
      name: "Make your recovery codes",
    });
    expect(dialog).toHaveTextContent("has no recovery codes yet");
    expect(dialog).toHaveTextContent(
      "there is no way back in. The only way forward is a fresh install with a new data folder.",
    );
  });

  it("asks again, in other words, when the codes made were never marked as saved", async () => {
    renderPrompt(recoveryCodesNotSaved);

    expect(await screen.findByRole("dialog")).toHaveTextContent(
      "never marked as saved",
    );
  });

  it("asks nothing of an account with a saved set", async () => {
    renderPrompt(recoveryCodesSaved);

    await waitFor(() =>
      expect(weir?.sent("GET", "/api/v1/auth/recovery-codes")).toHaveLength(1),
    );
    expect(screen.queryByRole("dialog")).toBeNull();
  });

  it("asks nothing while the answer has not come, or when it cannot come", async () => {
    renderPrompt({
      status: 500,
      body: '{"detail":"Something went wrong inside Weir."}',
    });

    await waitFor(() =>
      expect(weir?.sent("GET", "/api/v1/auth/recovery-codes")).toHaveLength(1),
    );
    expect(screen.queryByRole("dialog")).toBeNull();
  });

  it("puts itself aside for this sign-in on Not now, and stays gone", async () => {
    renderPrompt(recoveryCodesNone);
    fireEvent.click(await screen.findByRole("button", { name: "Not now" }));

    expect(screen.queryByRole("dialog")).toBeNull();
    expect(recoveryPromptPutAside()).toBe(true);
  });

  it("puts itself aside on Escape too", async () => {
    renderPrompt(recoveryCodesNone);
    await screen.findByRole("dialog");

    fireEvent.keyDown(document, { key: "Escape" });

    await waitFor(() => expect(screen.queryByRole("dialog")).toBeNull());
    expect(recoveryPromptPutAside()).toBe(true);
  });

  it("does not show again once it was put aside", async () => {
    window.sessionStorage.setItem("mm:recovery-prompt-put-aside", "1");
    renderPrompt(recoveryCodesNone);

    await waitFor(() =>
      expect(weir?.sent("GET", "/api/v1/auth/recovery-codes")).toHaveLength(1),
    );
    expect(screen.queryByRole("dialog")).toBeNull();
  });

  it("takes the person to System › Security to make the set, and is put aside on the way", async () => {
    renderPrompt(recoveryCodesNone);

    fireEvent.click(
      await screen.findByRole("button", { name: "Make recovery codes" }),
    );

    expect(screen.getByTestId("where")).toHaveTextContent(
      "/system?tab=security",
    );
    expect(screen.queryByRole("dialog")).toBeNull();
    expect(recoveryPromptPutAside()).toBe(true);
  });

  it("does not stand in the way on System › Security, where the set is made", async () => {
    renderPrompt(recoveryCodesNone, "/system?tab=security");

    await waitFor(() =>
      expect(weir?.sent("GET", "/api/v1/auth/recovery-codes")).toHaveLength(1),
    );
    expect(screen.queryByRole("dialog")).toBeNull();
  });

  it("does not ask someone who only reads, and does not even look", async () => {
    renderPrompt(recoveryCodesNone, "/", signedInViewer);

    await waitFor(() =>
      expect(weir?.sent("GET", "/api/v1/auth/me")).toHaveLength(1),
    );
    expect(screen.queryByRole("dialog")).toBeNull();
    expect(weir?.sent("GET", "/api/v1/auth/recovery-codes")).toEqual([]);
  });

  it("still asks on another tab of System", async () => {
    renderPrompt(recoveryCodesNone, "/system?tab=logs");

    expect(await screen.findByRole("dialog")).toBeVisible();
  });
});
