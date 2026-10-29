import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";

import { apiFetch, readJson, requireOk } from "../api/client";
import { sendJson } from "../api/send-json";
import { settingsKeys } from "./query-keys";
import type { HomeNetworkSignIn, HomeNetworkSignInPutBody } from "./types";

export const homeNetworkSignInPath = () => "/api/v1/suite/home-network-sign-in";

/** The id of the session a device is shown with when it skipped sign-in: there is no sign-in to end. */
export const HOME_NETWORK_SESSION_ID = "home-network";

/** Words for skipping sign-in on the home network, the same in Setup, Security and Health. */
export const HOME_NETWORK_WORDS = {
  title: "Skip sign-in for devices on your home network",
  question: "Skip sign-in for devices on your home network?",
  text: "Off, every device signs in, exactly as Weir has always done. On, a device Weir can prove is on your local network (not one behind a reverse proxy from outside) is already signed in -- no password needed on your own network.",
  switch: "Skip sign-in on the local network",
  switchHelp:
    "Counts as local: private network addresses (10.x, 172.16-31.x, 192.168.x), this machine (loopback), link-local addresses, and IPv6 private (ULA) addresses.",
  savedOn: "Devices on your local network now skip sign-in.",
  savedOff:
    "Every device signs in again, including ones on your local network.",
  checkName: "Skip sign-in for local devices",
  checkOn: "On. Devices on your local network skip sign-in.",
  checkOff: "Off. Every device signs in, including ones on your local network.",
  checkFix: "Open Security and turn on Allowed Hosts, or turn this off.",
} as const;

export async function fetchHomeNetworkSignIn(): Promise<HomeNetworkSignIn> {
  const path = homeNetworkSignInPath();
  const r = await apiFetch(path);
  await requireOk(path, r, "Could not load the home network sign-in setting");
  return readJson<HomeNetworkSignIn>(r);
}

/** Saves the choice. The server asks for the current password every time, even of a device that skipped sign-in. */
export async function putHomeNetworkSignIn(
  body: Omit<HomeNetworkSignInPutBody, "csrf_token">,
): Promise<HomeNetworkSignIn> {
  const r = await sendJson(
    homeNetworkSignInPath(),
    "PUT",
    body,
    "Could not save the home network sign-in setting",
  );
  return readJson<HomeNetworkSignIn>(r);
}

export function useHomeNetworkSignInQuery(enabled = true) {
  return useQuery({
    queryKey: settingsKeys.homeNetworkSignIn,
    queryFn: fetchHomeNetworkSignIn,
    enabled,
    staleTime: 30_000,
    retry: false,
  });
}

export function useHomeNetworkSignInMutation() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: putHomeNetworkSignIn,
    onSuccess: (data) => {
      queryClient.setQueryData(settingsKeys.homeNetworkSignIn, data);
    },
  });
}

const ADDRESS_ENTRY = /^[0-9a-fA-F:.]+(?:\/\d{1,3})?$/;
const IPV4 = /^(\d{1,3})\.(\d{1,3})\.(\d{1,3})\.(\d{1,3})$/;
const V4_BITS = 32;
const V6_BITS = 128;
const OCTET_MAX = 255;

/**
 * Whether `entry` looks like an address (192.168.1.20) or a CIDR range (192.168.1.0/28), so an obviously wrong one is refused
 * before it reaches the server, which is the one that decides.
 */
export function isAddressOrSubnet(entry: string): boolean {
  if (!ADDRESS_ENTRY.test(entry)) return false;
  const [address, prefix] = entry.split("/");
  const v4 = IPV4.exec(address);
  if (v4) {
    const octets = v4.slice(1).map(Number);
    return (
      octets.every((octet) => octet <= OCTET_MAX) &&
      (prefix === undefined || Number(prefix) <= V4_BITS)
    );
  }
  return (
    address.includes(":") &&
    !address.includes(".") &&
    (prefix === undefined || Number(prefix) <= V6_BITS)
  );
}

/** What Weir says about an entry that is neither. */
export function notAnAddressWords(entry: string): string {
  return `'${entry}' is not a valid address or subnet. Use an IP address or a CIDR range such as 192.168.1.0/28.`;
}
