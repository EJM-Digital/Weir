import type { RecordedReply } from "./recorded-weir";

/**
 * Replies a running Weir server gave to the requests the recovery-code screens make, kept word for word. Each body is the text
 * the server sent, and each status the one it sent it with. The codes are from a throwaway server and open nothing.
 */

export const csrfToken: RecordedReply = {
  status: 200,
  body: '{"csrf_token":"csrf.anonymous.auMOaA.2XNpaZ88IDzKnV9V3SO4ykv4tlo"}',
};

export const signedInAdmin: RecordedReply = {
  status: 200,
  body: '{"user":{"id":1,"username":"recorded-admin","role":"admin","app_theme":null}}',
};

export const signedInViewer: RecordedReply = {
  status: 200,
  body: '{"user":{"id":2,"username":"recorded-viewer","role":"viewer","app_theme":null}}',
};

export const setupCodes = [
  "GRB2S-U7V42-7H3CH-GSFA5",
  "JRL3H-65ZD7-6UPSZ-AZEB6",
  "S4B52-H2DRR-ZMSGT-QRRH4",
  "2SVJD-E44P2-FBC6J-K4JJH",
  "DBK2U-35A76-9C5ZS-D58D3",
  "G9N37-CLY6P-JWA44-BVRWR",
  "CC4EF-N7CET-JDASR-P335J",
  "UBV8S-5EQ6M-7UFAA-MLRJ4",
  "AT38B-HZANG-BNRDJ-CR4ZB",
  "GN8UA-QX8BW-DPUBP-2S2AJ",
];

export const accountCreatedWithCodes: RecordedReply = {
  status: 200,
  body: '{"message":"Account created. You are signed in.","username":"recorded-admin","user":{"id":1,"username":"recorded-admin","role":"admin","app_theme":null},"recovery_codes":["GRB2S-U7V42-7H3CH-GSFA5","JRL3H-65ZD7-6UPSZ-AZEB6","S4B52-H2DRR-ZMSGT-QRRH4","2SVJD-E44P2-FBC6J-K4JJH","DBK2U-35A76-9C5ZS-D58D3","G9N37-CLY6P-JWA44-BVRWR","CC4EF-N7CET-JDASR-P335J","UBV8S-5EQ6M-7UFAA-MLRJ4","AT38B-HZANG-BNRDJ-CR4ZB","GN8UA-QX8BW-DPUBP-2S2AJ"]}',
};

export const recoveryCodesSaved: RecordedReply = {
  status: 200,
  body: '{"remaining":10,"confirmed":true}',
};

export const recoveryCodesNotSaved: RecordedReply = {
  status: 200,
  body: '{"remaining":10,"confirmed":false}',
};

export const recoveryCodesNone: RecordedReply = {
  status: 200,
  body: '{"remaining":0,"confirmed":false}',
};

export const recoveryCodesOneLeft: RecordedReply = {
  status: 200,
  body: '{"remaining":1,"confirmed":true}',
};

export const recoveryCodesConfirmed: RecordedReply = {
  status: 200,
  body: '{"remaining":10,"confirmed":true,"message":"Thanks. Your recovery codes are marked as saved."}',
};

export const newRecoveryCodes: RecordedReply = {
  status: 200,
  body: '{"message":"New recovery codes made. Save them now: Weir shows them only once, and the old codes no longer work.","codes":["X96AX-3YP6Z-Q72HH-EA988","TJPRT-DZ6EW-HBQHP-NA6G5","A6DYT-39US6-TM777-ECLHF","TCE9G-7ZU52-SK9LM-X8LEV","SK8J4-XFH4H-VHDUZ-4PKRD","8EK6B-9LJ35-S6PUH-Q5MCU","HNMX7-XNQF6-XHYLF-HTGTR","2ABPJ-5LJEV-BPHS8-HSVBY","SB5GB-BM5HT-P238T-3FNDD","SDY9Z-VFM26-4LT23-TD7AC"],"remaining":10,"confirmed":false}',
};

export const newRecoveryCodesList = [
  "X96AX-3YP6Z-Q72HH-EA988",
  "TJPRT-DZ6EW-HBQHP-NA6G5",
  "A6DYT-39US6-TM777-ECLHF",
  "TCE9G-7ZU52-SK9LM-X8LEV",
  "SK8J4-XFH4H-VHDUZ-4PKRD",
  "8EK6B-9LJ35-S6PUH-Q5MCU",
  "HNMX7-XNQF6-XHYLF-HTGTR",
  "2ABPJ-5LJEV-BPHS8-HSVBY",
  "SB5GB-BM5HT-P238T-3FNDD",
  "SDY9Z-VFM26-4LT23-TD7AC",
];

export const currentPasswordWrong: RecordedReply = {
  status: 400,
  body: '{"detail":"Current password is incorrect."}',
};

export const codeAccepted: RecordedReply = {
  status: 200,
  body: '{"username":"recorded-admin","recovery_token":"1:47130fc21f8d0ead.auMObw.SyXAuDukVuAxW9ryLn-uFbBwBTY","codes_left":9}',
};

export const codeRefused: RecordedReply = {
  status: 400,
  body: '{"detail":"That recovery code isn\'t right, or it has been used already. Check it and try again."}',
};

export const codeTriesLimited: RecordedReply = {
  status: 429,
  body: '{"detail":"Too many recovery attempts from this address. Try again later."}',
  headers: { "Retry-After": "60" },
};

export const passwordSet: RecordedReply = {
  status: 200,
  body: '{"message":"Password changed. Sign in with your new password.","username":"recorded-admin"}',
};

export const passwordTooCommon: RecordedReply = {
  status: 400,
  body: '{"detail":"Password is too common. Choose a stronger password."}',
};

export const resetTimedOut: RecordedReply = {
  status: 400,
  body: '{"detail":"This password reset has timed out. Use another recovery code to start again."}',
};
