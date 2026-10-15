import { afterEach, beforeEach, describe, expect, it } from "vitest";
import {
  clearSignedIn,
  finishChangingPassword,
  finishSigningOut,
  markSignedIn,
  signedOutPath,
  startChangingPassword,
  startSigningOut,
  wasSignedIn,
} from "./signed-in-before";

describe("wasSignedIn", () => {
  beforeEach(() => {
    clearSignedIn();
  });

  it("is false for a browser that never signed in", () => {
    expect(wasSignedIn()).toBe(false);
  });

  it("is true once the browser has been signed in", () => {
    markSignedIn();
    expect(wasSignedIn()).toBe(true);
  });

  it("is false again after signing out on purpose", () => {
    markSignedIn();
    clearSignedIn();
    expect(wasSignedIn()).toBe(false);
  });
});

describe("signedOutPath", () => {
  beforeEach(() => {
    clearSignedIn();
  });

  afterEach(() => {
    finishChangingPassword();
    finishSigningOut();
  });

  it("is the plain sign-in page for a browser that never signed in", () => {
    expect(signedOutPath()).toBe("/login");
  });

  it("says a session expired for a browser that had one", () => {
    markSignedIn();
    expect(signedOutPath()).toBe("/login?session=expired");
  });

  it("does not say a session expired while signing out on purpose", () => {
    markSignedIn();
    startSigningOut();
    expect(signedOutPath()).toBe("/login");
  });

  it("says the password was changed while a password change is under way, whatever else is known", () => {
    markSignedIn();
    startChangingPassword();
    expect(signedOutPath()).toBe("/login?password=changed");
  });

  it("goes back to saying a session expired once the password change is over", () => {
    markSignedIn();
    startChangingPassword();
    finishChangingPassword();
    expect(signedOutPath()).toBe("/login?session=expired");
  });
});
