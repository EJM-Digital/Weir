import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import type { DirectPlayDevices } from "../../../../lib/processing/direct-play-api";
import { recordedBody, recordedResponse } from "../../../../test/recorded";
import {
  jsonAnswer,
  stubWeirApi,
  type StubbedApi,
} from "../../../../test/stub-weir-api";
import { CSRF_TOKEN } from "../../../../test/wire";
import { DevicesTab } from "./devices-tab";

// The tab here is the real one, reading from a stand-in server at `fetch` that answers as the recorded replies did. The file
// is about choosing devices and saving the choice: when Save is offered, what is sent, and what a slow or refused save looks like.

const DEVICES = "/api/v1/processing/direct-play/devices";

/** The list as the server sent it, with the first device already chosen, so a choice can be taken back to nothing. */
const FIRST_CHOSEN: DirectPlayDevices = (() => {
  const recorded = recordedBody<DirectPlayDevices>(`GET ${DEVICES}`);
  return {
    ...recorded,
    devices: recorded.devices.map((device, index) => ({
      ...device,
      selected: index === 0,
    })),
  };
})();
const FIRST = FIRST_CHOSEN.devices[0]!;
const SECOND = FIRST_CHOSEN.devices[1]!;

let api: StubbedApi;

beforeEach(() => {
  api = stubWeirApi({
    "GET /api/v1/auth/me": recordedResponse("GET /api/v1/auth/me"),
    "GET /api/v1/auth/csrf": { csrf_token: CSRF_TOKEN },
    [`GET ${DEVICES}`]: FIRST_CHOSEN,
    [`PUT ${DEVICES}`]: recordedResponse(`PUT ${DEVICES}`),
  });
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

async function openDevices() {
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
  });
  render(
    <QueryClientProvider client={client}>
      <DevicesTab />
    </QueryClientProvider>,
  );
  await screen.findByText(SECOND.name);
}

const box = (name: string) => screen.getByRole("checkbox", { name });
const saveButton = () =>
  screen.queryByRole("button", { name: /^(Save devices|Saving…)$/ });
const saved = () => api.to(`PUT ${DEVICES}`);

describe("the device checklist", () => {
  it("offers Save only while the ticks differ from the saved choice, and takes it away when they are put back", async () => {
    await openDevices();
    expect(box(FIRST.name)).toBeChecked();
    expect(saveButton()).toBeNull();

    fireEvent.click(box(SECOND.name));
    expect(saveButton()).toBeEnabled();

    fireEvent.click(box(SECOND.name));
    expect(saveButton()).toBeNull();
    fireEvent.click(box(FIRST.name));
    expect(saveButton()).toBeEnabled();
    fireEvent.click(box(FIRST.name));
    expect(saveButton()).toBeNull();
  });

  it("saves an empty list when every device is unticked, and leaves the checklist empty", async () => {
    api.set(`PUT ${DEVICES}`, {
      ...FIRST_CHOSEN,
      devices: FIRST_CHOSEN.devices.map((device) => ({
        ...device,
        selected: false,
      })),
    });
    await openDevices();

    fireEvent.click(box(FIRST.name));
    fireEvent.click(saveButton()!);

    await vi.waitFor(() => expect(saved()).toHaveLength(1));
    expect(saved()[0]!.body).toEqual({ selected: [], csrf_token: CSRF_TOKEN });
    await vi.waitFor(() => expect(saveButton()).toBeNull());
    for (const checkbox of screen.getAllByRole("checkbox")) {
      expect(checkbox).not.toBeChecked();
    }
  });
});

describe("saving the choice", () => {
  it("reads Saving… and holds every tick while the server works", async () => {
    api.set(`PUT ${DEVICES}`, () => new Promise<never>(() => undefined));
    await openDevices();
    fireEvent.click(box(SECOND.name));

    fireEvent.click(saveButton()!);

    expect(
      await screen.findByRole("button", { name: "Saving…" }),
    ).toBeDisabled();
    for (const checkbox of screen.getAllByRole("checkbox")) {
      expect(checkbox).toBeDisabled();
    }
  });

  it("says why when the server refuses it, and keeps the ticks", async () => {
    api.set(
      `PUT ${DEVICES}`,
      jsonAnswer({ detail: "One of those devices is not on the list." }, 400),
    );
    await openDevices();
    fireEvent.click(box(SECOND.name));

    fireEvent.click(saveButton()!);

    expect(await screen.findByRole("alert")).toHaveTextContent(
      "One of those devices is not on the list.",
    );
    expect(box(SECOND.name)).toBeChecked();
    expect(box(FIRST.name)).toBeChecked();
    expect(saveButton()).toBeEnabled();
  });

  it("says Weir cannot be reached when the server does not answer", async () => {
    api.set(`PUT ${DEVICES}`, () => {
      throw new TypeError("Failed to fetch");
    });
    await openDevices();
    fireEvent.click(box(SECOND.name));

    fireEvent.click(saveButton()!);

    expect(await screen.findByRole("alert")).toHaveTextContent(
      "Can't reach Weir. Check it's still running.",
    );
    expect(box(SECOND.name)).toBeChecked();
  });
});
