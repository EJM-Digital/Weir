import { act, fireEvent, render, screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

import * as settingsQueries from "../../../../lib/settings/queries";
import type {
  NetworkAccessPutBody,
  NetworkAccessStatus,
} from "../../../../lib/settings/types";
import { NetworkAccessSetting } from "./network-access-setting";

type MutateOptions = { onSuccess?: () => void };

const mutate =
  vi.fn<(body: NetworkAccessPutBody, options?: MutateOptions) => void>();
const reset = vi.fn();

const ADDRESS = "http://10.0.0.196:9347";

function status(over: Partial<NetworkAccessStatus> = {}): NetworkAccessStatus {
  return {
    state: "this_pc_only",
    summary: "Only this PC can reach Weir.",
    scope: "this_pc_only",
    pending_scope: null,
    firewall: "not_checked",
    port: 9347,
    machine_name: "MEDIA-PC",
    addresses: [],
    ...over,
  };
}

const allowed = status({
  state: "allowed",
  scope: "network",
  firewall: "allowed",
  addresses: [ADDRESS],
});

function setup(
  data: NetworkAccessStatus | undefined,
  {
    isPending = false,
    mutation = {},
  }: { isPending?: boolean; mutation?: Record<string, unknown> } = {},
) {
  const query = vi
    .spyOn(settingsQueries, "useNetworkAccessQuery")
    .mockReturnValue({
      data,
      isPending,
      error: null,
    } as unknown as ReturnType<typeof settingsQueries.useNetworkAccessQuery>);
  vi.spyOn(settingsQueries, "useNetworkAccessMutation").mockReturnValue({
    mutate,
    reset,
    isPending: false,
    isError: false,
    error: null,
    ...mutation,
  } as unknown as ReturnType<typeof settingsQueries.useNetworkAccessMutation>);
  return query;
}

const control = () => screen.getByTestId("network-access-scope");
const option = (name: string) =>
  screen.getByRole("button", { name }) as HTMLButtonElement;

afterEach(() => {
  vi.useRealTimers();
  vi.restoreAllMocks();
  mutate.mockReset();
  reset.mockReset();
});

describe("NetworkAccessSetting", () => {
  it("offers the two choices, with this PC only selected, and says so", () => {
    setup(status());

    render(<NetworkAccessSetting editable />);

    expect(option("This PC only")).toHaveAttribute("aria-pressed", "true");
    expect(option("Devices on my network")).toHaveAttribute(
      "aria-pressed",
      "false",
    );
    expect(screen.getByTestId("network-access-status")).toHaveTextContent(
      "This PC only",
    );
  });

  it("says where other devices can reach Weir, with the address to copy", () => {
    const writeText = vi.fn().mockResolvedValue(undefined);
    vi.stubGlobal("navigator", { clipboard: { writeText } });
    setup(allowed);

    render(<NetworkAccessSetting editable />);
    fireEvent.click(screen.getByRole("button", { name: `Copy ${ADDRESS}` }));

    expect(option("Devices on my network")).toHaveAttribute(
      "aria-pressed",
      "true",
    );
    expect(screen.getByTestId("network-access-status")).toHaveTextContent(
      `Reachable from your network:${ADDRESS}`,
    );
    expect(writeText).toHaveBeenCalledWith(ADDRESS);
    vi.unstubAllGlobals();
  });

  it("shows the person's choice as selected while it waits for approval on this PC, by name", () => {
    setup(
      status({
        pending_scope: "network",
        firewall: "blocked",
        addresses: [ADDRESS],
      }),
    );

    render(<NetworkAccessSetting editable />);

    expect(option("Devices on my network")).toHaveAttribute(
      "aria-pressed",
      "true",
    );
    expect(screen.getByTestId("network-access-status")).toHaveTextContent(
      "Waiting for approval on MEDIA-PC",
    );
    expect(
      screen.getByText(/Approve the Windows prompt on that PC/),
    ).toBeInTheDocument();
  });

  it("says Weir is only restarting when Windows already allows it", () => {
    setup(
      status({
        pending_scope: "network",
        firewall: "allowed",
        addresses: [ADDRESS],
      }),
    );

    render(<NetworkAccessSetting editable />);

    expect(screen.getByTestId("network-access-status")).toHaveTextContent(
      "Restarting Weir for your network…",
    );
  });

  it("says Windows Firewall blocks, and offers to try again", () => {
    setup(status({ state: "blocked", scope: "network", firewall: "blocked" }));

    render(<NetworkAccessSetting editable />);
    expect(screen.getByTestId("network-access-status")).toHaveTextContent(
      "Blocked by Windows Firewall",
    );
    fireEvent.click(screen.getByRole("button", { name: "Try again" }));

    expect(mutate).toHaveBeenCalledWith({ scope: "network" });
    expect(
      screen.getByText("Windows may be asking for approval on MEDIA-PC."),
    ).toBeInTheDocument();
  });

  it("asks before opening Weir to the network, and does nothing until the person confirms", () => {
    setup(status());

    render(<NetworkAccessSetting editable />);
    fireEvent.click(option("Devices on my network"));

    expect(
      screen.getByText(
        "Other devices on your network will be able to reach Weir's sign-in page.",
      ),
    ).toBeInTheDocument();
    expect(mutate).not.toHaveBeenCalled();

    fireEvent.click(screen.getByRole("button", { name: "Confirm" }));

    expect(mutate).toHaveBeenCalledWith(
      { scope: "network" },
      expect.anything(),
    );
  });

  it("closes the question once the choice has been saved", () => {
    setup(status());

    render(<NetworkAccessSetting editable />);
    fireEvent.click(option("Devices on my network"));
    fireEvent.click(screen.getByRole("button", { name: "Confirm" }));
    act(() => {
      mutate.mock.calls[0][1]?.onSuccess?.();
    });

    expect(
      screen.queryByTestId("network-access-confirm"),
    ).not.toBeInTheDocument();
  });

  it("leaves things as they are when the person cancels the question", () => {
    setup(status());

    render(<NetworkAccessSetting editable />);
    fireEvent.click(option("Devices on my network"));
    fireEvent.click(screen.getByRole("button", { name: "Cancel" }));

    expect(mutate).not.toHaveBeenCalled();
    expect(
      screen.queryByTestId("network-access-confirm"),
    ).not.toBeInTheDocument();
  });

  it("limits Weir to this PC at once, with nothing to confirm", () => {
    setup(allowed);

    render(<NetworkAccessSetting editable />);
    fireEvent.click(option("This PC only"));

    expect(mutate).toHaveBeenCalledWith({ scope: "this_pc_only" });
    expect(
      screen.queryByTestId("network-access-confirm"),
    ).not.toBeInTheDocument();
  });

  it("says why limiting Weir to this PC could not be saved", () => {
    setup(allowed, {
      mutation: {
        isError: true,
        error: new Error("Weir couldn’t save that. Try again."),
      },
    });

    render(<NetworkAccessSetting editable />);

    expect(screen.getByRole("alert")).toHaveTextContent(
      "Weir couldn’t save that. Try again.",
    );
    expect(
      screen.queryByTestId("network-access-confirm"),
    ).not.toBeInTheDocument();
  });

  it("stops saying Windows may be asking for approval once a minute has passed", () => {
    vi.useFakeTimers();
    setup(status({ state: "blocked", scope: "network", firewall: "blocked" }));
    const asking = "Windows may be asking for approval on MEDIA-PC.";

    render(<NetworkAccessSetting editable />);
    fireEvent.click(screen.getByRole("button", { name: "Try again" }));
    expect(screen.getByText(asking)).toBeInTheDocument();

    act(() => {
      vi.advanceTimersByTime(59_999);
    });
    expect(screen.getByText(asking)).toBeInTheDocument();

    act(() => {
      vi.advanceTimersByTime(1);
    });
    expect(screen.queryByText(asking)).not.toBeInTheDocument();
  });

  it("does not send anything when the choice already holds", () => {
    setup(status());

    render(<NetworkAccessSetting editable />);
    fireEvent.click(option("This PC only"));

    expect(mutate).not.toHaveBeenCalled();
  });

  it("says why a choice could not be saved, in the question that asked for it", () => {
    setup(status(), {
      mutation: {
        isError: true,
        error: new Error("Weir couldn't save that. Try again."),
      },
    });

    render(<NetworkAccessSetting editable />);
    fireEvent.click(option("Devices on my network"));

    expect(screen.getByRole("alert")).toHaveTextContent(
      "Weir couldn't save that. Try again.",
    );
  });

  it("only reports the state to someone who cannot change it", () => {
    setup(status({ state: "blocked", scope: "network", firewall: "blocked" }));

    render(<NetworkAccessSetting editable={false} />);

    expect(screen.queryByTestId("network-access-scope")).toBeNull();
    expect(screen.queryByRole("button", { name: "Try again" })).toBeNull();
    expect(screen.getByTestId("network-access-status")).toHaveTextContent(
      "Blocked by Windows Firewall",
    );
  });

  it("says what decides it, and offers no control, where Weir does not manage it", () => {
    setup(
      status({
        state: "not_applicable",
        summary: "Set by Docker's port mapping.",
        scope: null,
      }),
    );

    render(<NetworkAccessSetting editable />);

    expect(screen.getByTestId("network-access-unmanaged")).toHaveTextContent(
      "Set by Docker's port mapping.",
    );
    expect(screen.queryByTestId("network-access-scope")).toBeNull();
  });

  it("says it is checking before the answer arrives", () => {
    setup(undefined, { isPending: true });

    render(<NetworkAccessSetting editable />);

    expect(screen.getByText("Checking…")).toBeInTheDocument();
  });

  it("says it could not check when the answer never arrives", () => {
    setup(undefined);

    render(<NetworkAccessSetting editable />);

    expect(screen.getByRole("alert")).toHaveTextContent(
      "Weir couldn't load the network setting.",
    );
  });

  it("names its choices as a group for a screen reader", () => {
    setup(status());

    render(<NetworkAccessSetting editable />);

    expect(control()).toHaveAttribute("aria-label", "Who can reach Weir");
  });
});
