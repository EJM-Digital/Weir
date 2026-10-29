import { describe, expect, it } from "vitest";

import { isAddressOrSubnet, notAnAddressWords } from "./home-network-sign-in";

describe("isAddressOrSubnet", () => {
  it.each([
    "192.168.1.20",
    "192.168.1.0/28",
    "10.0.0.0/8",
    "0.0.0.0/0",
    "255.255.255.255/32",
    "::1",
    "fd00::/8",
    "fe80::1234:abcd/10",
    "2001:db8::/128",
  ])("accepts %s", (entry) => {
    expect(isAddressOrSubnet(entry)).toBe(true);
  });

  it.each([
    "banana",
    "",
    "192.168.1",
    "192.168.1.256",
    "192.168.1.0/33",
    "192.168.1.0/",
    "192.168.1.0/x",
    "fd00::/129",
    "::ffff:192.168.1.20",
    "192.168.1.20:8080",
    "192.168.1.20 ",
    "1234",
  ])("refuses %j", (entry) => {
    expect(isAddressOrSubnet(entry)).toBe(false);
  });

  it("says what is wrong in the owner's words", () => {
    expect(notAnAddressWords("banana")).toBe(
      "'banana' is not a valid address or subnet. Use an IP address or a CIDR range such as 192.168.1.0/28.",
    );
  });
});
