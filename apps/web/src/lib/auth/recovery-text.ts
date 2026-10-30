/** How many codes a set holds. */
const SET_SIZE = 10;

/**
 * Every sentence the recovery-code screens say: first-run setup, the prompt after sign-in, System › Security and
 * the recovery page. They live here, in one place, so they can be kept in step with Deluno's wording.
 */
export const RECOVERY_TEXT = {
  noWayBack:
    "If you lose your password and every recovery code, there is no way back in. The only way forward is a fresh install with a new data folder.",
  shownOnce:
    "Weir shows these codes only once. Each one works once. Keep them somewhere safe and private, away from this computer.",
  codesListLabel: "Recovery codes",
  copyLabel: "the recovery codes",
  download: "Download as text",
  downloadFileName: "weir-recovery-codes.txt",
  downloadHeader: "Weir recovery codes",
  downloadNote:
    "Each code works once. Keep this file somewhere safe and private.",
  savedCheck: "I have saved these codes",
  continue: "Continue",
  done: "Done",
  continueBusy: "Saving…",

  setupHeading: "Save your recovery codes",
  setupLead:
    "Your account is ready. These ten recovery codes are the only way back in if you forget your password.",

  promptHeading: "Make your recovery codes",
  promptLead:
    "This account has no recovery codes yet. Without them, a forgotten password cannot be fixed.",
  promptUnsavedLead:
    "The recovery codes made for this account were never marked as saved. Make a new set so you have them.",
  promptMake: "Make recovery codes",
  promptLater: "Not now",

  settingsTitle: "Recovery codes",
  settingsHelp:
    "Your recovery codes are the only way back in if you forget your password.",
  settingsNone: "No recovery codes yet.",
  settingsNotSaved: "Codes were made but never marked as saved.",
  settingsNudge:
    "Make a set now, so a forgotten password can be fixed. Weir keeps asking until there is one.",
  settingsMakeHeading: "Make new codes",
  settingsMakeHelp:
    "A new set voids every code you have now. It needs your current password, and shows the new codes once.",
  settingsMake: "Make recovery codes",
  settingsMakeNew: "Make new codes",
  loadThing: "your recovery codes",
  makeFailed: "Could not make recovery codes.",
  confirmFailed: "Could not save that the codes were saved. Try again.",

  forgotLink: "Forgot your username or password?",
  recoverTitle: "Get back into Weir",
  recoverLead:
    "Enter one of your recovery codes. Weir shows your username, then lets you choose a new password.",
  recoverCodeLabel: "Recovery code",
  recoverCodeHint:
    "Each code works once, and it is used up as soon as Weir accepts it, even if you stop before choosing a password.",
  recoverCodeSubmit: "Check code",
  recoverCodeBusy: "Checking…",
  recoverCodeFailed: "That did not work. Check it and try again.",
  recoverTooManyTries: "Too many tries. Wait 10 minutes and try again.",
  recoverCodeMissing: "Enter a recovery code.",
  recoverUsernameLead: "Your username is",
  recoverDone: "Your password is changed.",
  recoverFooter:
    "If you have lost both your password and your recovery codes, the only way forward is a fresh install.",
  recoverEndedSessions:
    "Everyone who was signed in has been signed out. Choose a new password to sign in again.",
  recoverPasswordLabel: "New password",
  recoverConfirmLabel: "Confirm new password",
  recoverMismatch: "Passwords do not match.",
  recoverTooShort: "Password must be at least 8 characters.",
  recoverPasswordSubmit: "Set new password",
  recoverPasswordBusy: "Saving…",
  recoverPasswordFailed: "Could not set the new password.",
  recoverStartAgain: "Use another recovery code",
  recoverBack: "Go to sign in",

  /** "9 of 10 recovery codes left." */
  codesLeft: (count: number) => `${count} of ${SET_SIZE} recovery codes left.`,
} as const;
