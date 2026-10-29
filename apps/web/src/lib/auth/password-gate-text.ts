/**
 * The words for asking the signed-in person for their password before a change that must not be made by whoever is at the keyboard:
 * the dialog, and what is said when the password is missing. What is said when it is wrong comes from the server.
 */
export const PASSWORD_GATE_TEXT = {
  title: "Enter your password",
  label: "Current password",
  confirm: "Confirm",
  cancel: "Cancel",
  missing: "Enter your current password to make this change.",
} as const;
