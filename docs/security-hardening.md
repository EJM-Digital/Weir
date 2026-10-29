# Security Hardening

This checklist defines the current practical hardening baseline for Weir.

## Authentication and setup

- First-run bootstrap is only available while no account exists at all; a deactivated account still closes it, and
  bootstrap never replaces or removes an account.
- The username and password are the way in and recovery codes are the only backup. Weir has exactly one account, and
  there is no console command, reset file, email reset or other way in.
- First-run bootstrap makes 10 single-use recovery codes and shows them once; setup waits until the owner confirms they
  are saved. System › Security shows how many are left and makes a new set (it needs the current password and voids
  every old code). An account made before recovery codes existed has none, and Weir asks for a set after sign-in.
- Recovery codes are stored only as Argon2id hashes (the same `PasswordHasher` as passwords), never logged, and never
  returned after they are first shown. A wrong code is rate-limited per address like sign-in, and wrong codes from any
  address back off together.
- A recovery code is spent as soon as it is accepted, even if the new password is never chosen. Every recovery deletes
  every session, leaves API keys and webhook secrets working, writes the Activity line "Sign-in recovered with a recovery
  code" with the number of codes left, and alerts every notification channel that is switched on.
- If the password and every recovery code are lost there is no way back in: the only way forward is a fresh install with
  a new data folder.
- Bootstrap from a non-loopback peer needs the one-time setup code Weir logs and writes to
  `WEIR_HOME/setup-code` at start-up; a loopback peer (the tray's `127.0.0.1`) does not.
- Passwords shorter than 8 characters must be blocked by both frontend and backend validation.
- Login and bootstrap routes are rate-limited per IP; login also backs off per account after 5
  failures in 15 minutes, independent of which addresses the guesses came from.
- Authenticated state-changing browser requests require CSRF protection.
- Extra hardening, optional: set `WEIR_CORS_ORIGINS` (or `WEIR_TRUSTED_BROWSER_ORIGINS`) to the address you reach Weir
  at, and a browser post is then also refused unless its `Origin` (or, failing that, `Referer`) is in that list. Without
  it Weir does not check where a post came from; the CSRF token and the `X-Requested-With` header still apply.
- Session cookies are HTTP-only.
- Secure cookies should be enabled when deployed behind HTTPS.
- Existing users must persist across restarts and upgrades.
- Requests are only answered for a `Host` Weir recognises (see
  [Reverse Proxy: Host header allow-list](https://github.com/EJM-Digital/Weir/blob/main/docs-site/docs/deployment/reverse-proxy.md)) — set `WEIR_ALLOWED_HOSTS` for a reverse-proxy domain.
- Skipping sign-in for devices on the home network is off until the owner turns it on; see
  [Skip sign-in on the home network](#skip-sign-in-on-the-home-network).
- Install-level routes (configuration bundle, backups, updates, the directory browser, media
  manager and notification channel credentials, history reset) require the admin role.

## Skip sign-in on the home network

An opt-in choice on System › Security, and a question in first-run setup that starts unanswered. It is **off** until the owner
turns it on, which is how every install behaved before it existed.

- **What counts as local.** `127.0.0.0/8`, `10.0.0.0/8`, `172.16.0.0/12`, `192.168.0.0/16`, `169.254.0.0/16`, `::1/128`,
  `fc00::/7` and `fe80::/10`; an IPv4-mapped IPv6 address counts as the IPv4 address. The owner can add networks of their own
  (a CIDR range or a bare address), and an optional allow list narrows who skips: empty, every local address does; otherwise
  the address must also be on it. A public address is never skipped by the built-in ranges, whatever the allow list says. An
  entry that is not an address or a range is refused with a 400 whose detail begins `'…' is not a valid address or subnet.`
  and goes on to show an address and a range as examples (`HomeNetworkWords.InvalidEntries`).
- **Which address is judged.** The real socket peer, captured before forwarded headers are applied
  (`ResponseMarkers.ConnectionPeer`, with the `X-Forwarded-For` header as it arrived in `ResponseMarkers.ForwardedFor`).
  `X-Forwarded-For` counts only from a peer inside `WEIR_TRUSTED_PROXY_IPS`; the header is then walked from the right, one
  trusted proxy at a time, to the first hop that is not one. A proxy that sends no header, a hop that is not an address, or a
  chain of nothing but proxies names no client, and no client means no skip. `HomeNetwork.ClientOf` is the one place this
  is decided, built on `NetRange` and `ClientRateLimitKey.InNetworks`.
- **What a skipped visitor is.** The account's own identity (its first active admin) for that request only. No session row,
  cookie or token is made (`SignedInSession.ByHomeNetwork`, shown as the session `home-network`), a real session cookie is
  used first when there is one, and there is nobody to be while no account exists. Its CSRF token is the one anyone is given
  before signing in, since there is no session to bind it to.
- **The current password is still asked for**, even of a skipped visitor: changing the password or the username, changing this
  choice or its networks, and (when they exist) creating or revoking API keys and making a new set of recovery codes. The
  one gate for it is `ApiRequest.RequireCurrentPasswordAsync` (used by the password and username forms and by this setting),
  which counts a wrong password against the account like a wrong
  sign-in (`UsernameLoginBackoff`), so it cannot be used to guess without limit. A route that needs it reads
  `current_password` from its body, calls `RequireUserAsync` and then this.
- **Where it is kept.** The `home_network_sign_in` table (one row, migration 0051), read and written by
  `HomeNetworkSignInStore` through `GET` and `PUT /api/v1/suite/home-network-sign-in` (the `PUT` is admin only). It is not part of
  a configuration backup, so restoring a backup cannot turn it on.
- **What it writes down.** One Activity line, "Local network sign-in setting changed" ("Skip sign-in for devices on the local
  network turned on." or "…turned off."), only when the value changes; a change to only the networks writes none. System's
  health checks list "Skip sign-in for local devices", which reads "On. Devices on your local network skip sign-in." or "Off.
  Every device signs in, including ones on your local network.".
- **The Allowed Hosts warning.** With the choice on and Allowed Hosts off, Security and the health check say: "Allowed Hosts
  (above) is off. Weir currently answers on any address, so any device that can reach a local address here skips sign-in too.
  Turn on Allowed Hosts as well for this to only apply on your own network." Weir's Host check has no off switch
  (`AllowedHostPolicy.Enforced`), so the warning is carried by `HomeNetworkWords.Warning` and cannot be raised today.
- **Behind a reverse proxy.** Without `WEIR_TRUSTED_PROXY_IPS`, a proxy on the same machine connects from a local address, so
  everything it forwards looks local. Set `WEIR_TRUSTED_PROXY_IPS` to the proxy before turning this on.

## Secrets and private data

- The data folder that holds the database, backups, logs and secrets is protected against other local
  accounts: on Windows the tray locks it to its owner on every start; in Docker the entrypoint writes secrets
  with `umask 077` and runs the server as a non-root user; a bare source install should restrict `WEIR_HOME`
  itself (for example `chmod 700`).
- The copy taken before an update (`backups/pre-update`) holds the database, with its sessions and saved connection keys,
  and copies of `session.secret` and `credentials.secret` (the keys can only be read with the second). It is owner-only
  wherever it is kept, even when `WEIR_BACKUP_DIR` is outside the data folder: folder `700` and files `600` on Linux and in
  Docker, and on Windows an explicit access list (SYSTEM, Administrators and the account Weir runs as, nothing
  inherited) on the folder and on every file.
- When the server is slow to get to work (the log's "slow to get to work" warning), it saves a triage dump of itself to
  `logs/diagnostics/stall-<time>-pid<id>.dmp`, at most twice a run (the second only 30 minutes or more after the first),
  keeping the newest three files, none larger than 256 MB (a real one is a few hundred KB to a few MB). It holds the stack of every thread and
  a little of what they refer to, so it can contain file names, addresses and the text of a request being read, and
  possibly a credential that was on a stack at that moment. It sits in the data folder, under the same owner-only protection as the logs and
  the database, and is not part of the configuration bundle or any backup. Treat it like the database: share it only with
  whoever supports the install, and delete it after. It is read with `dotnet-dump analyze <file> -c "clrstack -all"`. Saving needs the
  runtime's own diagnostics channel (a named pipe on Windows, a socket in the temp folder on Linux); with
  `DOTNET_EnableDiagnostics=0`, or where the container blocks `ptrace` for its own process, the save fails and the log says
  so in one warning.
- Real `.env` files must never be committed.
- Runtime SQLite databases must never be committed.
- Logs, backups, media paths, API keys, provider tokens, and session secrets must never be committed.
- Public issue logs must redact secrets, private hostnames, and private filesystem paths.
- Docker generates a persistent session secret and credentials secret (`session.secret`, `credentials.secret` in
  `WEIR_HOME`) when none is provided; the Windows tray does the same.
- `WEIR_SESSION_SECRET` signs sessions and CSRF tokens; `WEIR_CREDENTIALS_SECRET` encrypts saved provider
  credentials. Keep them separate.
- `WEIR_METRICS_BEARER_TOKEN` can gate machine access to `/metrics` without requiring an operator browser session.
- To rotate `WEIR_CREDENTIALS_SECRET`, set the new value as `WEIR_CREDENTIALS_SECRET`, add the old value to
  `WEIR_PREVIOUS_CREDENTIALS_SECRETS`, restart Weir, then re-save every media manager
  connection (Setup › Connections › Media managers). After every saved credential has been re-written with the new value, remove the old value from
  `WEIR_PREVIOUS_CREDENTIALS_SECRETS` and restart again. Copies already in `backups/pre-update` keep the credentials secret
  they were made with (their `.credentials.secret` file) and the keys encrypted with it; they do not follow the rotation, so
  delete them once you no longer need them, or keep them as a pair.
- **The backup password.** A configuration backup (System › Backups) holds the connections' secrets only when a backup password
  is set: the media managers' API keys and webhook secrets, the download clients' passwords and API keys, and the alerts'
  addresses. They are in the backup file only as one locked section (`sealed_secrets`), sealed with AES-256-GCM under a key
  that Argon2id makes from the password (the same memory-hard function that protects sign-in passwords); the rest of the file
  stays readable, so a restore can show what is inside. Without a password a backup holds none of them, which is how every
  backup behaved before the password existed, so an install that already backs up keeps working: its next backup is taken
  without the secrets and System › Backups says so until a password is set. Weir never writes a secret into a backup in the
  clear.
  - Setting the password asks for it twice and applies the same strength rule as a sign-in password. Weir keeps the key the
    password makes, locked with `WEIR_CREDENTIALS_SECRET` like every other saved secret, so the schedule can seal a backup
    while nobody is signed in; the password itself is never stored, returned or logged.
  - Restoring a backup with locked secrets asks for the password it was made with. A wrong or missing password is refused
    before anything changes. A restore adds the connections and alerts the install does not have, switched off, and leaves
    the ones it has exactly as they are.
  - Changing the password locks later backups with the new one. Backups already made keep the password they were made with,
    so keep the old one for as long as you keep those backups. Weir cannot recover a lost password.
  - After rotating `WEIR_CREDENTIALS_SECRET`, set the backup password again, together with re-saving the connections. If Weir
    can no longer read the key it saved, it stops taking backups and says so, instead of quietly leaving the secrets out.
  - The server's own session and credentials secrets are not part of a configuration backup. They belong to the install
    (`session.secret` and `credentials.secret`, or the environment variables), and a restore on another install brings the
    connections' secrets across by re-locking them with that install's own credentials secret.

## Repository and dependency controls

- `main` is protected by GitHub rules.
- The required check is `ci-passed` (the `CI` workflow's verdict job; see `docs/local-development.md`).
- Dependabot is security-only: every ecosystem sets `open-pull-requests-limit: 0`, so no routine version pull requests open, while Dependabot security updates still do. It covers NuGet (`apps/server`, `apps/tray`), npm (`apps/web`, `docs-site`), GitHub Actions.
- CodeQL code scanning (C# and JavaScript/TypeScript) runs on `main`, pull requests to `main`, weekly schedule, and manual dispatch.
- Security vulnerabilities are reported privately through `SECURITY.md`.
- Public issues are not used for unpatched vulnerabilities.
- CI runs a NuGet vulnerability scan (`node scripts/check-dotnet-vulnerabilities.mjs apps/server/Weir.slnx`, failing on High or Critical) and `npm audit` in addition to CodeQL and standard test gates.
- An npm advisory with no fixed release can be let through only by an entry in `apps/web/dependency-audit-exceptions.json` or `docs-site/dependency-audit-exceptions.json`: the advisory id, why it cannot reach users (`mitigation`) and an `expires` date at most 30 days out, approved by the owner. An expired entry fails the scan again. Remove the entry as soon as a fixed release can be taken.

## Windows Firewall

- The Windows package's server listens on this PC only (`127.0.0.1` and `[::1]`) until LAN access is allowed, so
  nothing on the network can connect to a fresh or silently installed Weir, whatever the firewall says. LAN access
  is one saved choice (`lan-access` in the data folder). It turns on when the person says yes at the install
  prompt and the rule is created, when the tray's "Allow other devices on your network..." item succeeds, when
  an admin chooses "Devices on my network" on System › About, or when `--allow-lan` runs. The tray's "Only allow
  this PC" item, or "This PC only" on System › About, turns it off again. Any change restarts the server with
  the new bind address. A saved choice that cannot be read or understood counts as off. Docker and a bare source
  install are unchanged: the server binds every interface there, and the operator publishes the port.
- An install that predates the setting keeps its reach: the first start with no saved choice turns LAN access on
  only if Windows already allows Weir's server in (Weir's own rule, or any enabled inbound allow rule for
  `WeirServer.exe` that no enabled block rule cancels), and off otherwise, then saves the answer.
- The Windows package can create exactly one inbound firewall rule, named `Weir`, scoped to the installed
  server's program path (`server\WeirServer.exe` under Velopack's stable `current` folder) — never a port-wide
  rule, and the program path is fixed by the installer, never taken from user input.
- The rule covers every network profile: Domain, Private and Public, at install, from the tray's
  "Allow other devices on your network..." menu item, and from `--allow-lan`. Windows often marks a home network
  Public, and a rule that skipped Public would make "Devices on my network" do nothing there. Weir is still behind
  its own sign-in, and first-run setup from another device needs the one-time setup code. A rule written by an
  earlier version, for Private and Domain only, is replaced by the current one whenever the tray configures the
  rule; and when the PC is on a network that rule does not cover, choosing the network again (or **Try again** on
  System › About) asks Windows for the administrator approval again, which widens it. An install that turned
  network access on under 1.0.0-rc.1 still has such a rule, and on a Private network nothing would ask: so the
  first interactive tray start after the update, with network access on and a rule that leaves a profile out,
  asks Windows once to widen it. The question is recorded (`firewall-rule-widening-asked` in the data folder)
  before the prompt shows, and never repeats whatever the answer; a `--silent` start or a PC with no desktop
  never asks.
- Creating or removing it needs a Windows admin (UAC) elevation; Weir asks for that once, at first run, with a
  plain explanation, and never asks again automatically if declined. Turning LAN access off leaves the rule in
  place: with nothing listening for the network it lets nothing in.
- System › About changes the same saved choice: `PUT /api/v1/suite/network-access` is admin only, carries the
  session's CSRF token like every other settings write, and only writes the `lan-access` file in the data folder
  (the same `on` / `off` text the tray writes). The server never touches the firewall itself. The tray, which
  watches the file, restarts the server for it and, when the choice is for the network and the `Weir` rule is
  missing, raises the same Windows admin prompt as its own menu, on the PC itself, so a person who is signed in to
  Weir from another device cannot approve it for themselves. Declining the prompt still restarts the server for
  the network, because that is the saved choice; Windows Firewall then blocks, System › About says so, and
  choosing the network again asks again. A choice saved while the tray is not running waits for it. Docker and a
  bare install answer the `PUT` with 409 and say what decides there (the port mapping, the `--host` option).
  Every change is written to Activity as "Network access changed", with the admin who made it.
- `--allow-lan` (for a program driving Weir unattended) never elevates itself and never prompts. It always turns
  LAN access on, because that is what the caller asked for; it creates the rule only when already elevated, and
  otherwise logs that the rule was not created and still exits zero. The bind address, not the rule, is what
  keeps a Weir local-only, so an unelevated `--allow-lan` leaves the decision to Windows Firewall.
- The bootstrap setup code and the `Host` allow-list do not depend on any of this: they apply to whichever
  addresses the server listens on. While Weir is local-only, first-run setup from another device is not
  possible, because that device cannot connect at all.
- Reading the current state (for System › About) needs no admin rights and touches nothing; only the Windows
  package does this — Docker and a bare install report `not_applicable` with the reason. It reports "only this
  PC" from the server's own bind address, shows a saved choice the server has not caught up with as pending, and
  consults the firewall only when the server listens, or is about to listen, for the network. The addresses it
  lists are this PC's IPv4 addresses on adapters that have a gateway, never self-assigned ones.
- Weir never modifies a firewall rule it did not create: removing block rules is limited to inbound rules whose
  program path matches Weir's own server exe exactly.

## Release controls

- Releases are built from tagged source.
- Windows and Docker artifacts are produced by GitHub Actions.
- Local Docker Desktop is not required to validate Docker packaging.
- Release artifacts remain under AGPL-3.0-or-later.

## Ongoing checks

Run this list before any major release:

1. Confirm no secrets or runtime files are staged.
2. Confirm dependency audit jobs pass.
3. Confirm CodeQL has no open high-confidence security findings.
4. Confirm auth setup, login, logout, and password validation smoke tests pass.
5. Confirm backup files do not expose secrets in public docs, logs, or screenshots.
6. Confirm Activity and System › Logs do not expose tokens or internal implementation details to normal users.
