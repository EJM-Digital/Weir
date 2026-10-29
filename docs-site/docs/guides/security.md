---
sidebar_position: 2
title: Security
---

# Security

Weir's security posture and hardening baseline.

## Authentication

- First-run bootstrap is only available while no account exists at all
- The username and password are the way in; ten single-use recovery codes are the only backup (see [Locked out](#locked-out))
- Passwords must be at least 8 characters (enforced frontend and backend)
- Login and bootstrap routes are rate-limited
- Session cookies are HTTP-only
- CSRF protection on all authenticated state-changing requests
- Optional extra hardening: set `WEIR_CORS_ORIGINS` to the address you reach Weir at, and browser posts from any other origin are refused
- Secure cookies enabled when deployed behind HTTPS

## Skip sign-in for devices on your home network

By default every device signs in. If you would rather not type your password on your own network, turn on **Skip sign-in for
devices on your home network** in System › Security (or answer Yes when setup asks). It is off until you do.

With it on, a device Weir can prove is on your local network is already signed in, with no password. Weir counts these as
local: private network addresses (`10.x`, `172.16`-`172.31.x`, `192.168.x`), this machine (loopback), link-local addresses and
IPv6 private (ULA) addresses. You can add networks of your own, and an optional allow list narrows it to chosen addresses, still
only if they are also local. A device on a public address is never let in this way.

- **Not behind a reverse proxy from outside.** Weir judges the address the connection really came from. It reads
  `X-Forwarded-For` only from a proxy listed in `WEIR_TRUSTED_PROXY_IPS`. If you reach Weir through a reverse proxy on the same
  machine, set that first, or every visitor the proxy forwards will look local.
- **Nothing is left behind.** A device that skips sign-in is treated as signed in for that one request: no session is made, no
  cookie is set, and there is no Sign out.
- **The password is still asked for** when you change your password or username, change this choice or its networks, and for the
  other changes that must not be made by anyone who happens to be at the keyboard.
- **Activity** records "Local network sign-in setting changed" when you turn it on or off, and System's health checks show its state.

It is a choice about signing in, not a way to recover an account.

## Secrets management

| Secret | Purpose |
|--------|---------|
| `WEIR_SESSION_SECRET` | Signs sessions and CSRF tokens |
| `WEIR_CREDENTIALS_SECRET` | Encrypts saved provider credentials (Sonarr, Radarr, etc.) |
| `WEIR_METRICS_BEARER_TOKEN` | Gates machine access to `/metrics` |

**Keep these separate.** Never commit `.env` files, SQLite databases, or logs.

### Rotating credentials secret

1. Set the new value as `WEIR_CREDENTIALS_SECRET`
2. Add the old value to `WEIR_PREVIOUS_CREDENTIALS_SECRETS`
3. Restart Weir
4. Re-save all provider credentials (Sonarr, Radarr)
5. Remove the old value from `WEIR_PREVIOUS_CREDENTIALS_SECRETS`
6. Restart again

## CI security checks

| Tool | What it checks |
|------|---------------|
| CodeQL | Static analysis of the C# server and the JavaScript/TypeScript web app |
| NuGet vulnerability scan | .NET package vulnerabilities (`node scripts/check-dotnet-vulnerabilities.mjs apps/server/Weir.slnx`; fails on High or Critical) |
| npm audit | JavaScript dependency vulnerabilities |
| Dependabot | Automated dependency update PRs for NuGet, npm, GitHub Actions and Docker base images |

The docs build runs an image-format preflight and rejects ICNS, JXL, HEIC, and HEIF before Docusaurus parses repository assets. The current `image-size` advisories are tracked in `dependency-audit-exceptions.json` because the registry does not yet publish a fixed version; the exception has an expiry date and a documented mitigation. When a fixed release is available, update the `image-size` override, remove the exception entries, and keep the preflight as defense in depth.

## Repository controls

- `main` is protected by GitHub branch rules
- Required check: `ci-passed` (the CI workflow's verdict job)
- Security vulnerabilities are reported privately through `SECURITY.md`

## Pre-release checklist

1. Confirm no secrets or runtime files are staged
2. Confirm dependency audit jobs pass
3. Confirm CodeQL has no open high-confidence findings
4. Confirm auth smoke tests pass
5. Confirm backup files don't expose secrets
6. Confirm Activity and the logs don't expose tokens

## Locked out

Weir has one account, the admin made at first-run setup. The username and password are the way in, and recovery
codes are the only backup. There is no email reset (the app stores no email address), no console command, no reset
file and no other way in.

### Recovery codes

- **Setup shows ten codes, once.** Each works once. Setup does not finish until you tick **I've saved these codes**;
  **Copy** and **Download as text** are there to help.
- **System › Security › Recovery codes** shows how many are left. **Make a new set** asks for your current password,
  voids every code you had and shows the new ones once.
- **An account made before recovery codes existed has none.** After you update, signing in asks you to make a set, and
  System › Security keeps saying so until there is one.
- Weir stores only a hash of each code, as it does for your password, and never writes a code to its log.

Keep the codes somewhere safe and private, away from the computer that runs Weir.

### If you forgot your username or password

1. On the sign-in page, choose **Forgot your username or password?**.
2. Enter one of your recovery codes. Weir shows your username.
3. Choose a new password, typed twice. It must meet the same rules as any password.
4. Sign in with it.

The code is used up as soon as Weir accepts it, even if you stop before choosing a password. Each recovery:

- signs out every browser that was signed in;
- leaves API keys working, so a media manager such as Deluno keeps sending files;
- writes "Sign-in recovered with a recovery code" to Activity, with the number of codes left;
- sends an alert to every notification channel you have switched on (Setup › Connections › Alerts).

Wrong codes are limited like sign-in attempts: per address, and for all addresses together.

### If the password and every recovery code are lost

There is no way back in. Weir has no reset command, no reset file and no email link, by design. The only way forward is a
fresh install with a new data folder.

### If you signed in but are sent straight back to the login page

Your password is not the problem — the browser is discarding the session cookie. This happens
when Weir is reached over plain HTTP while the cookie is forced to HTTPS-only.

The default is `auto`, which marks the cookie HTTPS-only only when a request actually arrives
over HTTPS, so this should not occur unless the setting has been forced. Check for
`WEIR_SESSION_COOKIE_SECURE=true` in your environment and remove it, or set it to `auto`,
then restart.
