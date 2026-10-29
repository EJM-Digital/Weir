import {
  fetchHomeNetworkSignIn,
  putHomeNetworkSignIn,
} from "../../lib/settings/home-network-sign-in";
import type { RequestCase } from "./request-case";

const HOME_NETWORK = "/api/v1/suite/home-network-sign-in";
const TURN_ON = {
  enabled: true,
  extra_networks: ["100.64.0.0/10"],
  allow_list: [],
  current_password: "the owner's password",
};

/** The requests behind System › Security's card on skipping sign-in, and Setup's question about it. */
export const homeNetworkRequestCases: RequestCase[] = [
  {
    fn: fetchHomeNetworkSignIn,
    asking: "whether devices on the home network skip sign-in",
    call: () => fetchHomeNetworkSignIn(),
    method: "GET",
    path: HOME_NETWORK,
    reply: `GET ${HOME_NETWORK}`,
  },
  {
    fn: putHomeNetworkSignIn,
    asking: "turning it on with the owner's own network and the password",
    call: () => putHomeNetworkSignIn(TURN_ON),
    method: "PUT",
    path: HOME_NETWORK,
    body: TURN_ON,
    reply: `PUT ${HOME_NETWORK}`,
  },
  {
    fn: putHomeNetworkSignIn,
    asking: "a wrong password, which the server refuses in its own words",
    call: () => putHomeNetworkSignIn(TURN_ON),
    method: "PUT",
    path: HOME_NETWORK,
    body: TURN_ON,
    reply: `PUT ${HOME_NETWORK} (wrong password)`,
  },
  {
    fn: putHomeNetworkSignIn,
    asking:
      "an entry that is not an address, which the server refuses in its own words",
    call: () => putHomeNetworkSignIn(TURN_ON),
    method: "PUT",
    path: HOME_NETWORK,
    body: TURN_ON,
    reply: `PUT ${HOME_NETWORK} (bad entry)`,
  },
];
