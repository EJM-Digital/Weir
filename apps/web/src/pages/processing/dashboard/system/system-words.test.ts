import { describe, expect, it } from "vitest";

import {
  fullInWords,
  gigabytes,
  machineUpWords,
  sizeWords,
  splitAddress,
  uptimeWords,
} from "./system-words";

describe("memory", () => {
  it("turns bytes into gigabytes", () => {
    expect(gigabytes(8 * 1024 ** 3)).toBe(8);
  });

  it("says 0 B for nothing", () => {
    expect(sizeWords(0)).toBe("0 B");
    expect(sizeWords(2048)).toBe("2.0 KB");
  });
});

describe("uptime", () => {
  it("counts minutes and seconds in the first hour", () => {
    expect(uptimeWords(42 * 60 + 7)).toBe("42:07");
  });

  it("counts hours and minutes in the first day", () => {
    expect(uptimeWords(3 * 3600 + 5 * 60)).toBe("3h 05m");
  });

  it("counts days and hours after that", () => {
    expect(uptimeWords(2 * 86_400 + 4 * 3600 + 59)).toBe("2d 4h");
  });

  it("says how long the computer has been up to the unit that matters", () => {
    expect(machineUpWords(30)).toBe("up 1 min");
    expect(machineUpWords(7 * 3600)).toBe("up 7 h");
    expect(machineUpWords(3 * 86_400 + 100)).toBe("up 3 days");
  });
});

describe("when a drive is full", () => {
  it("gives days for a drive filling quickly", () => {
    expect(fullInWords(12.4)).toBe("~12 days");
    expect(fullInWords(0.2)).toBe("~1 day");
  });

  it("gives months past three months", () => {
    expect(fullInWords(150)).toBe("~5 months");
  });

  it("says nothing for a drive that is not filling or will last over a year", () => {
    expect(fullInWords(null)).toBe("");
    expect(fullInWords(0)).toBe("");
    expect(fullInWords(800)).toBe("");
  });
});

describe("addresses", () => {
  it("splits the host from the port", () => {
    expect(splitAddress("http://192.168.1.5:9347/")).toEqual({
      host: "192.168.1.5",
      port: "9347",
    });
  });

  it("has no port when the address names none", () => {
    expect(splitAddress("https://weir.home")).toEqual({
      host: "weir.home",
      port: null,
    });
  });
});
