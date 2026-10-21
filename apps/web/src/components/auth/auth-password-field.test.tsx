import { fireEvent, render, screen } from "@testing-library/react";
import { useState } from "react";
import { describe, expect, it } from "vitest";

import { AuthPasswordField } from "./auth-password-field";

function Field(props: { initial?: string; required?: boolean }) {
  const [value, setValue] = useState(props.initial ?? "");
  return (
    <AuthPasswordField
      id="pw"
      testId="pw"
      name="password"
      label="Password"
      revealLabel="password"
      value={value}
      onChange={setValue}
      autoComplete="current-password"
      required={props.required}
      minLength={8}
      maxLength={64}
    />
  );
}

describe("the password field", () => {
  it("hides what is typed until the person asks to see it", () => {
    render(<Field initial="hunter2hunter2" />);
    const input = screen.getByLabelText("Password");

    expect(input).toHaveAttribute("type", "password");
    fireEvent.click(screen.getByRole("button", { name: "Show password" }));
    expect(input).toHaveAttribute("type", "text");
    fireEvent.click(screen.getByRole("button", { name: "Hide password" }));
    expect(input).toHaveAttribute("type", "password");
  });

  it("hides the password again once it has been cleared", () => {
    render(<Field initial="abc" />);
    const input = screen.getByLabelText("Password");
    fireEvent.click(screen.getByRole("button", { name: "Show password" }));

    fireEvent.change(input, { target: { value: "abcd" } });
    expect(input).toHaveAttribute("type", "text");
    fireEvent.change(input, { target: { value: "" } });

    expect(input).toHaveAttribute("type", "password");
    expect(screen.getByRole("button", { name: "Show password" })).toBeVisible();
  });

  it.each([
    ["asks for a value", true],
    ["leaves the value optional", undefined],
  ])("%s when it is told to", (_name, required) => {
    render(<Field required={required} />);

    expect(screen.getByLabelText("Password").hasAttribute("required")).toBe(
      required === true,
    );
    expect(screen.getByLabelText("Password")).toHaveAttribute("minlength", "8");
    expect(screen.getByLabelText("Password")).toHaveAttribute(
      "maxlength",
      "64",
    );
  });
});
