import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render, screen } from "@testing-library/react";
import { useEffect, useLayoutEffect, useState } from "react";
import { afterEach, describe, expect, it, vi } from "vitest";

import * as api from "../../../../lib/media-managers/media-managers-api";
import type { MediaManagerConnection } from "../../../../lib/media-managers/media-managers-api";
import { NewConnectionSecretPrompt } from "./new-connection-secret-prompt";

const added = {
  id: 1,
  name: "New One",
  kind: "deluno",
} as MediaManagerConnection;

afterEach(() => {
  vi.restoreAllMocks();
});

/** Shows the prompt a moment after mounting, the way a finished request does. */
function PromptShownLater() {
  const [shown, setShown] = useState(false);
  useEffect(() => {
    const t = setTimeout(() => setShown(true), 0);
    return () => clearTimeout(t);
  }, []);
  return shown ? <PromptClickedAsItAppears /> : null;
}

/** Asks for the secret in the same instant the prompt is on screen, before the browser has idled. */
function PromptClickedAsItAppears() {
  useLayoutEffect(() => {
    queueMicrotask(() =>
      screen.getByTestId("media-manager-new-secret-create").click(),
    );
  }, []);
  return <NewConnectionSecretPrompt connection={added} onDismiss={() => {}} />;
}

describe("NewConnectionSecretPrompt", () => {
  it("shows the secret even when it is asked for the instant the prompt appears", async () => {
    vi.spyOn(api, "generateMediaManagerWebhookSecret").mockResolvedValue({
      connection_id: 1,
      webhook_secret: "brand-new-secret",
      webhook_url_path: "/api/v1/intake/webhook/deluno",
      header_name: "X-Webhook-Secret",
    });
    render(
      <QueryClientProvider client={new QueryClient()}>
        <PromptShownLater />
      </QueryClientProvider>,
    );

    expect(await screen.findByTestId("media-manager-secret")).toHaveTextContent(
      "brand-new-secret",
    );
  });
});
