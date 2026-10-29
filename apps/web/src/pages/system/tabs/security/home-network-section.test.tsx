import { cleanup, fireEvent, screen, within } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

import {
  serveRecordedReplies,
  type RecordedRoutes,
  type RecordedWeir,
} from "../../../../test/recorded-weir";
import {
  heldReply,
  renderSecurity,
} from "../../../../test/security-test-support";
import { HomeNetworkSection } from "./home-network-section";
import {
  csrfToken,
  homeNetworkBadEntry,
  homeNetworkOff,
  homeNetworkOn,
  homeNetworkWrongPassword,
} from "./security-replies";

const PATH = "/api/v1/suite/home-network-sign-in";
const CSRF = "csrf.anonymous.atBNYQ.TcHTRYKKC8sTV8BMqob2iGMgIXY";
const WARNING =
  "Allowed Hosts (above) is off. Weir currently answers on any address, so any device that can reach a local address here skips sign-in too. Turn on Allowed Hosts as well for this to only apply on your own network.";

let weir: RecordedWeir | undefined;

function renderCard(routes: RecordedRoutes) {
  weir = serveRecordedReplies({
    "GET /api/v1/auth/csrf": csrfToken,
    ...routes,
  });
  renderSecurity(<HomeNetworkSection />);
}

afterEach(() => {
  cleanup();
  expect(weir?.unrecorded ?? []).toEqual([]);
  weir = undefined;
  vi.unstubAllGlobals();
});

const card = () => screen.getByTestId("suite-security-home-network");
const switchControl = () =>
  within(card()).getByRole("switch", {
    name: /Skip sign-in on the local network/,
  });
const typeInto = (label: string, value: string) =>
  fireEvent.change(
    within(card()).getByLabelText(label, { selector: "input" }),
    {
      target: { value },
    },
  );
const save = () =>
  fireEvent.click(within(card()).getByRole("button", { name: "Save" }));
const sent = () => weir!.sent("PUT", PATH).map((request) => request.body);

describe("Security: skip sign-in for devices on your home network", () => {
  it("says in the owner's words what it does, off until it is turned on", async () => {
    renderCard({ [`GET ${PATH}`]: homeNetworkOff });

    expect(await screen.findByRole("switch")).not.toBeChecked();
    expect(
      within(card()).getByRole("heading", {
        name: "Skip sign-in for devices on your home network",
      }),
    ).toBeInTheDocument();
    expect(screen.getByTestId("home-network-words")).toHaveTextContent(
      "Off, every device signs in, exactly as Weir has always done. On, a device Weir can prove is on your local network (not one behind a reverse proxy from outside) is already signed in -- no password needed on your own network.",
    );
    expect(
      screen.getByText(
        "Counts as local: private network addresses (10.x, 172.16-31.x, 192.168.x), this machine (loopback), link-local addresses, and IPv6 private (ULA) addresses.",
      ),
    ).toBeVisible();
    expect(
      within(card()).queryByLabelText("Current password"),
    ).not.toBeInTheDocument();
    expect(within(card()).getByRole("button", { name: "Save" })).toBeDisabled();
  });

  it("asks for the password only once something changed, and sends it with the choice", async () => {
    renderCard({
      [`GET ${PATH}`]: homeNetworkOff,
      [`PUT ${PATH}`]: homeNetworkOn,
    });
    await screen.findByRole("switch");

    fireEvent.click(switchControl());
    expect(switchControl()).toBeChecked();
    expect(within(card()).getByRole("button", { name: "Save" })).toBeDisabled();
    typeInto("Current password", "the owner's password");
    save();

    expect(
      await screen.findByText(
        "Devices on your local network now skip sign-in.",
      ),
    ).toBeVisible();
    expect(sent()).toStrictEqual([
      {
        enabled: true,
        extra_networks: [],
        allow_list: [],
        current_password: "the owner's password",
        csrf_token: CSRF,
      },
    ]);
    expect(switchControl()).toBeChecked();
    expect(
      within(card()).queryByLabelText("Current password"),
    ).not.toBeInTheDocument();
  });

  it("says every device signs in again after it is turned off", async () => {
    renderCard({
      [`GET ${PATH}`]: homeNetworkOn,
      [`PUT ${PATH}`]: homeNetworkOff,
    });
    await screen.findByRole("switch");

    fireEvent.click(switchControl());
    typeInto("Current password", "the owner's password");
    save();

    expect(
      await screen.findByText(
        "Every device signs in again, including ones on your local network.",
      ),
    ).toBeVisible();
    expect(switchControl()).not.toBeChecked();
  });

  it("keeps the owner's own networks and the allow list, each added and removed one at a time", async () => {
    renderCard({
      [`GET ${PATH}`]: homeNetworkOn,
      [`PUT ${PATH}`]: homeNetworkOn,
    });
    await screen.findByRole("switch");

    expect(within(card()).getByText("100.64.0.0/10")).toBeVisible();
    fireEvent.click(
      within(card()).getByRole("button", {
        name: "Remove 100.64.0.0/10 from Your own networks (optional)",
      }),
    );
    expect(within(card()).queryByText("100.64.0.0/10")).not.toBeInTheDocument();

    fireEvent.change(
      within(card()).getByLabelText(
        "Narrow this to specific addresses (optional)",
      ),
      { target: { value: "192.168.1.0/28" } },
    );
    fireEvent.click(
      within(card()).getByRole("button", {
        name: "Add to Narrow this to specific addresses (optional)",
      }),
    );
    fireEvent.change(
      within(card()).getByLabelText(
        "Narrow this to specific addresses (optional)",
      ),
      { target: { value: "192.168.1.0/28" } },
    );
    fireEvent.keyDown(
      within(card()).getByLabelText(
        "Narrow this to specific addresses (optional)",
      ),
      { key: "Enter" },
    );
    expect(within(card()).getAllByText("192.168.1.0/28")).toHaveLength(1);
    typeInto("Current password", "the owner's password");
    save();

    await within(card()).findByTestId("home-network-saved");
    expect(sent()).toStrictEqual([
      {
        enabled: true,
        extra_networks: [],
        allow_list: ["192.168.1.0/28"],
        current_password: "the owner's password",
        csrf_token: CSRF,
      },
    ]);
  });

  it("refuses a bad entry on the spot, in the exact words, and adds nothing", async () => {
    renderCard({ [`GET ${PATH}`]: homeNetworkOn });
    await screen.findByRole("switch");

    fireEvent.change(
      within(card()).getByLabelText(
        "Narrow this to specific addresses (optional)",
      ),
      { target: { value: "banana" } },
    );
    fireEvent.click(
      within(card()).getByRole("button", {
        name: "Add to Narrow this to specific addresses (optional)",
      }),
    );

    expect(await within(card()).findByRole("alert")).toHaveTextContent(
      "'banana' is not a valid address or subnet. Use an IP address or a CIDR range such as 192.168.1.0/28.",
    );
    expect(
      within(card()).queryByText("banana", { selector: "span" }),
    ).toBeNull();
    expect(weir!.sent("PUT", PATH)).toHaveLength(0);
  });

  it("shows the server's words when the password is wrong, and keeps what was typed", async () => {
    renderCard({
      [`GET ${PATH}`]: homeNetworkOff,
      [`PUT ${PATH}`]: homeNetworkWrongPassword,
    });
    await screen.findByRole("switch");

    fireEvent.click(switchControl());
    typeInto("Current password", "not it");
    save();

    expect(await within(card()).findByRole("alert")).toHaveTextContent(
      "Current password is incorrect.",
    );
    expect(switchControl()).toBeChecked();
  });

  it("shows the server's words when it refuses an entry", async () => {
    renderCard({
      [`GET ${PATH}`]: homeNetworkOn,
      [`PUT ${PATH}`]: homeNetworkBadEntry,
    });
    await screen.findByRole("switch");

    fireEvent.click(switchControl());
    typeInto("Current password", "the owner's password");
    save();

    expect(await within(card()).findByRole("alert")).toHaveTextContent(
      "'banana' is not a valid address or subnet.",
    );
  });

  it("warns when it is on and Allowed Hosts is off, in the owner's words", async () => {
    renderCard({
      [`GET ${PATH}`]: {
        status: 200,
        body: JSON.stringify({
          enabled: true,
          extra_networks: [],
          allow_list: [],
          warning: WARNING,
        }),
      },
    });

    expect(await screen.findByTestId("home-network-warning")).toHaveTextContent(
      WARNING,
    );
  });

  it("shows no warning while it is off", async () => {
    renderCard({ [`GET ${PATH}`]: homeNetworkOff });
    await screen.findByRole("switch");

    expect(screen.queryByTestId("home-network-warning")).toBeNull();
  });

  it("says so when the setting cannot be read", async () => {
    renderCard({
      [`GET ${PATH}`]: {
        status: 500,
        body: '{"detail":"Weir could not finish."}',
      },
    });

    expect(await within(card()).findByRole("alert")).toBeInTheDocument();
    expect(screen.queryByRole("switch")).toBeNull();
  });

  it("says it is loading until the server answers", () => {
    renderCard({ [`GET ${PATH}`]: () => new Promise(() => undefined) });

    expect(within(card()).getByText("Loading…")).toBeInTheDocument();
  });

  it("says Saving… and cannot be pressed again while the save is under way", async () => {
    const held = heldReply(homeNetworkOn);
    renderCard({
      [`GET ${PATH}`]: homeNetworkOff,
      [`PUT ${PATH}`]: held.answer,
    });
    await screen.findByRole("switch");
    fireEvent.click(switchControl());
    typeInto("Current password", "the owner's password");
    save();

    const saving = await within(card()).findByRole("button", {
      name: "Saving…",
    });
    expect(saving).toBeDisabled();
    expect(switchControl()).toBeDisabled();

    held.release();
    expect(
      await within(card()).findByTestId("home-network-saved"),
    ).toBeVisible();
  });

  it("adds nothing for an empty box or a key that is not Enter", async () => {
    renderCard({ [`GET ${PATH}`]: homeNetworkOn });
    await screen.findByRole("switch");
    const box = within(card()).getByLabelText(
      "Narrow this to specific addresses (optional)",
    );

    fireEvent.keyDown(box, { key: "Enter" });
    fireEvent.change(box, { target: { value: "192.168.1.0/28" } });
    fireEvent.keyDown(box, { key: "a" });

    expect(
      within(card()).queryByText("192.168.1.0/28", { selector: "span" }),
    ).toBeNull();
    expect(within(card()).getByRole("button", { name: "Save" })).toBeDisabled();
  });
});
