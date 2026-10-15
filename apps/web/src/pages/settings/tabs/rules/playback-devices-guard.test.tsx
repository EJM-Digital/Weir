import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import {
  act,
  fireEvent,
  render,
  screen,
  waitFor,
} from "@testing-library/react";
import { createMemoryRouter, RouterProvider } from "react-router-dom";
import { afterEach, describe, expect, it, vi } from "vitest";

import { setupRoutes } from "../../../../app/setup-routes";
import * as authQueries from "../../../../lib/auth/queries";
import * as api from "../../../../lib/processing/direct-play-api";
import type { DirectPlayDevices } from "../../../../lib/processing/direct-play-api";

// The Playback devices tab inside its real setup area; the other tabs of the area are stood in for.
vi.mock("./profiles-tab", () => ({
  ProfilesTab: () => <div>Profiles content</div>,
}));

const devices: DirectPlayDevices = {
  customised: false,
  devices: [
    {
      id: "apple_tv_4k",
      name: "Apple TV 4K",
      source: "builtin",
      note: "Apple's built-in player.",
      selected: true,
    },
    {
      id: "iphone",
      name: "iPhone",
      source: "builtin",
      note: "The built-in video player.",
      selected: false,
    },
  ],
};

/** How long a lazily loaded tab may take on a busy machine before the test gives up. */
const CEILING = { timeout: 10_000 };

async function openDevices() {
  vi.spyOn(authQueries, "useMeQuery").mockReturnValue({
    data: { role: "operator" },
  } as ReturnType<typeof authQueries.useMeQuery>);
  vi.spyOn(api, "fetchDirectPlayDevices").mockResolvedValue(devices);
  const router = createMemoryRouter(setupRoutes(null), {
    initialEntries: ["/setup/rules/devices"],
  });
  render(
    <QueryClientProvider client={new QueryClient()}>
      <RouterProvider router={router} />
    </QueryClientProvider>,
  );
  return {
    router,
    iphone: await screen.findByRole("checkbox", { name: "iPhone" }, CEILING),
  };
}

afterEach(() => {
  vi.restoreAllMocks();
});

describe("leaving the playback devices tab", () => {
  it("asks first when ticks are unsaved, and keeps them when told to stay", async () => {
    const { router, iphone } = await openDevices();
    fireEvent.click(iphone);

    fireEvent.click(screen.getByRole("tab", { name: "Profiles" }));

    expect(screen.getByTestId("settings-unsaved-changes")).toHaveTextContent(
      "You have unsaved changes to your playback devices. Leave without saving?",
    );
    fireEvent.click(screen.getByTestId("settings-unsaved-changes-cancel"));
    expect(router.state.location.pathname).toBe("/setup/rules/devices");
    expect(screen.getByRole("checkbox", { name: "iPhone" })).toBeChecked();
    expect(
      screen.getByRole("button", { name: "Save devices" }),
    ).toBeInTheDocument();
  });

  it("moves on once the person chooses to leave without saving", async () => {
    const { router, iphone } = await openDevices();
    fireEvent.click(iphone);

    fireEvent.click(screen.getByRole("tab", { name: "Profiles" }));
    fireEvent.click(screen.getByTestId("settings-unsaved-changes-confirm"));

    await waitFor(
      () => expect(router.state.location.pathname).toBe("/setup/rules"),
      CEILING,
    );
    expect(
      await screen.findByText("Profiles content", undefined, CEILING),
    ).toBeVisible();
  });

  it("does not ask when the ticks are back to what is saved", async () => {
    const { router, iphone } = await openDevices();
    fireEvent.click(iphone);
    fireEvent.click(iphone);

    fireEvent.click(screen.getByRole("tab", { name: "Profiles" }));

    await waitFor(
      () => expect(router.state.location.pathname).toBe("/setup/rules"),
      CEILING,
    );
    expect(screen.queryByTestId("settings-unsaved-changes")).toBeNull();
  });

  it("does not ask once the ticks are saved", async () => {
    const { router, iphone } = await openDevices();
    vi.spyOn(api, "putDirectPlayDevices").mockResolvedValue({
      ...devices,
      devices: devices.devices.map((d) => ({ ...d, selected: true })),
    });
    fireEvent.click(iphone);
    fireEvent.click(screen.getByRole("button", { name: "Save devices" }));
    await waitFor(
      () =>
        expect(
          screen.queryByRole("button", { name: "Save devices" }),
        ).toBeNull(),
      CEILING,
    );
    // The area learns there is nothing unsaved a render after the checklist does.
    await act(async () => undefined);

    fireEvent.click(screen.getByRole("tab", { name: "Profiles" }));

    await waitFor(
      () => expect(router.state.location.pathname).toBe("/setup/rules"),
      CEILING,
    );
    expect(screen.queryByTestId("settings-unsaved-changes")).toBeNull();
  });
});
