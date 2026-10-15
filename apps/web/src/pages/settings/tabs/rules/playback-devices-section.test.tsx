import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import {
  act,
  fireEvent,
  render,
  screen,
  waitFor,
} from "@testing-library/react";
import type { ReactNode } from "react";
import { afterEach, expect, it, vi } from "vitest";

import { LIVE_TOPIC_QUERIES } from "../../../../lib/live/live-topics";
import * as authQueries from "../../../../lib/auth/queries";
import * as api from "../../../../lib/processing/direct-play-api";
import type { DirectPlayDevices } from "../../../../lib/processing/direct-play-api";
import { PlaybackDevicesSection } from "./playback-devices-section";

const devices: DirectPlayDevices = {
  customised: false,
  devices: [
    {
      id: "apple_tv_4k",
      name: "Apple TV 4K",
      source: "https://www.apple.com/apple-tv-4k/specs/ (checked 2026-08-13)",
      note: "Apple's built-in player.",
      selected: true,
    },
    {
      id: "iphone",
      name: "iPhone",
      source: "https://support.apple.com/specs/iphone (checked 2026-08-06)",
      note: "The built-in video player.",
      selected: false,
    },
  ],
};

const queryClient = () =>
  new QueryClient({
    defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
  });

function wrapper({ children }: { children: ReactNode }) {
  return (
    <QueryClientProvider client={queryClient()}>{children}</QueryClientProvider>
  );
}

function asRole(role: string) {
  vi.spyOn(authQueries, "useMeQuery").mockReturnValue({
    data: { role },
  } as ReturnType<typeof authQueries.useMeQuery>);
}

afterEach(() => {
  vi.restoreAllMocks();
});

it("lists each device with its note and source, and saves the chosen ids", async () => {
  asRole("operator");
  vi.spyOn(api, "fetchDirectPlayDevices").mockResolvedValue(devices);
  const save = vi.spyOn(api, "putDirectPlayDevices").mockResolvedValue({
    ...devices,
    devices: devices.devices.map((d) => ({ ...d, selected: true })),
  });

  render(<PlaybackDevicesSection />, { wrapper });

  expect(
    await screen.findByText(
      "Each file shows whether these can Direct Play it: your media server plays it as it is, without converting. Information only.",
    ),
  ).toBeInTheDocument();
  expect(screen.getByText("Devices you watch on")).toBeInTheDocument();
  expect(
    screen.getByText("Nothing changes until you press Save."),
  ).toBeInTheDocument();
  expect(screen.getByText("Apple's built-in player.")).toBeInTheDocument();
  const source = screen.getByRole("link", { name: "Source for Apple TV 4K" });
  expect(source).toHaveAttribute(
    "href",
    "https://www.apple.com/apple-tv-4k/specs/",
  );
  expect(screen.getByText(/checked 2026-08-13/)).toBeInTheDocument();
  expect(
    screen.queryByTestId("processing-direct-play-customised"),
  ).not.toBeInTheDocument();

  // The ticks follow the saved selection a render after the list appears.
  expect(
    await screen.findByRole("checkbox", { name: "Apple TV 4K", checked: true }),
  ).toBeInTheDocument();
  const iphone = screen.getByRole("checkbox", { name: "iPhone" });
  expect(iphone).not.toBeChecked();
  expect(
    screen.queryByRole("button", { name: "Save devices" }),
  ).not.toBeInTheDocument();
  fireEvent.click(iphone);
  fireEvent.click(screen.getByRole("button", { name: "Save devices" }));

  await waitFor(() => {
    expect(save).toHaveBeenCalledWith(["apple_tv_4k", "iphone"]);
  });
});

it("says when the list comes from the operator's own file", async () => {
  asRole("admin");
  vi.spyOn(api, "fetchDirectPlayDevices").mockResolvedValue({
    ...devices,
    customised: true,
  });

  render(<PlaybackDevicesSection />, { wrapper });

  expect(
    await screen.findByTestId("processing-direct-play-customised"),
  ).toHaveTextContent(/your own direct-play-devices\.json/);
});

it("shows a load error instead of a blank panel when the playback devices fail to load", async () => {
  asRole("operator");
  vi.spyOn(api, "fetchDirectPlayDevices").mockRejectedValue(
    new Error("network down"),
  );

  render(<PlaybackDevicesSection />, { wrapper });

  expect(await screen.findByTestId("settings-load-error")).toHaveTextContent(
    "Weir couldn’t load your playback devices. Reload the page to try again.",
  );
});

it("shows a viewer the list read-only", async () => {
  asRole("viewer");
  vi.spyOn(api, "fetchDirectPlayDevices").mockResolvedValue(devices);
  const save = vi.spyOn(api, "putDirectPlayDevices");

  render(<PlaybackDevicesSection />, { wrapper });

  const iphone = await screen.findByRole("checkbox", { name: "iPhone" });
  expect(iphone).toBeDisabled();
  fireEvent.click(iphone);
  expect(
    screen.queryByRole("button", { name: "Save devices" }),
  ).not.toBeInTheDocument();
  expect(save).not.toHaveBeenCalled();
  expect(
    screen.getByText(/Only an operator or admin can change/),
  ).toBeInTheDocument();
});

it("shows a selection saved in another browser when the server says the devices changed", async () => {
  asRole("operator");
  const fetch = vi
    .spyOn(api, "fetchDirectPlayDevices")
    .mockResolvedValue(devices);
  const client = queryClient();

  render(<PlaybackDevicesSection />, {
    wrapper: ({ children }) => (
      <QueryClientProvider client={client}>{children}</QueryClientProvider>
    ),
  });
  expect(
    await screen.findByRole("checkbox", { name: "Apple TV 4K", checked: true }),
  ).toBeInTheDocument();

  fetch.mockResolvedValue({
    ...devices,
    devices: devices.devices.map((d) => ({
      ...d,
      selected: d.id === "iphone",
    })),
  });
  await act(async () => {
    await Promise.all(
      LIVE_TOPIC_QUERIES.direct_play_devices.map((queryKey) =>
        client.invalidateQueries({ queryKey }),
      ),
    );
  });

  expect(
    await screen.findByRole("checkbox", { name: "iPhone", checked: true }),
  ).toBeInTheDocument();
  expect(
    screen.getByRole("checkbox", { name: "Apple TV 4K" }),
  ).not.toBeChecked();
});
