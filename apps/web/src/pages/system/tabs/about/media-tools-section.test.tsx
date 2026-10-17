import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render, screen, within } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { afterEach, describe, expect, it, vi } from "vitest";

import { systemReplies } from "../../../../test/recorded-system";
import {
  serveRecordedReplies,
  type RecordedReply,
} from "../../../../test/recorded-weir";
import { MediaToolsSection } from "./media-tools-section";

const MEDIA_TOOLS = "GET /api/v1/system/media-tools";

function renderTools() {
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false } },
  });
  return render(
    <QueryClientProvider client={client}>
      <MemoryRouter>
        <MediaToolsSection />
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

/** The `dd` beside a fact's name. */
function fact(name: string): HTMLElement {
  const row = screen.getByText(name, { selector: "dt" }).parentElement;
  return within(row as HTMLElement).getByRole("definition");
}

afterEach(() => vi.unstubAllGlobals());

describe("MediaToolsSection", () => {
  it("says Checking… for both tools until the server has answered", async () => {
    let answer: (reply: RecordedReply) => void = () => undefined;
    serveRecordedReplies({
      [MEDIA_TOOLS]: () =>
        new Promise<RecordedReply>((resolve) => {
          answer = resolve;
        }),
    });

    renderTools();

    expect(fact("FFmpeg")).toHaveTextContent("Checking…");
    expect(fact("mkvmerge")).toHaveTextContent("Checking…");
    answer(systemReplies.mediaTools);
    expect(await screen.findByText("9.0.1")).toBeInTheDocument();
    expect(screen.queryByText("Checking…")).not.toBeInTheDocument();
  });

  it("shows the version Weir found for FFmpeg and calls a missing mkvmerge optional", async () => {
    serveRecordedReplies({ [MEDIA_TOOLS]: systemReplies.mediaTools });

    renderTools();

    expect(await screen.findByText("9.0.1")).toBeInTheDocument();
    const ffmpeg = fact("FFmpeg");
    expect(ffmpeg).not.toHaveAttribute("data-status");
    expect(ffmpeg).toHaveAttribute(
      "title",
      "ffmpeg version n9.0.1-11-ge47273f4d9-20260831 Copyright (c) 2000-2026 the FFmpeg developers",
    );
    expect(fact("mkvmerge")).toHaveTextContent("Not installed (optional)");
    expect(fact("mkvmerge")).not.toHaveAttribute("data-status");
    expect(fact("Writes with")).toHaveTextContent(/^FFmpeg$/);
  });

  it("marks a missing FFmpeg as broken and says nothing can be processed without it", async () => {
    serveRecordedReplies({
      [MEDIA_TOOLS]: systemReplies.mediaToolsNoneInstalled,
    });

    renderTools();

    expect(await screen.findByText("Not found")).toBeInTheDocument();
    const ffmpeg = fact("FFmpeg");
    expect(ffmpeg).toHaveAttribute("data-status", "broken");
    expect(ffmpeg).toHaveAttribute(
      "title",
      "Nothing can be processed without it.",
    );
    expect(fact("mkvmerge")).toHaveTextContent("Not installed (optional)");
  });
});
