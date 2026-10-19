# Smoke Checklists

These checklists define the minimum user-level validation before calling a release ready. Automated CI checks are necessary but not enough; these are the click-through paths that catch packaging and first-run regressions.

## Windows installer smoke

Use the Velopack setup exe from the release being validated.

1. Run the setup exe on a clean Windows user profile or a reset test profile.
2. Confirm application files install under `%LocalAppData%\Weir`.
3. Confirm runtime data is created under `C:\ProgramData\Weir`.
4. Launch Weir from the Start Menu shortcut.
5. Confirm the tray icon appears at once with a blinking dot in its corner while Weir starts, and that the dot stops blinking and goes green once Weir is ready and everything it relies on answers (amber, with the hover text naming it, if Deluno or a workflow folder does not). Confirm the first run asked "Start Weir when you sign in to Windows?" and that `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` has a `Weir` entry only if you said yes. Confirm a notice from the tray icon says Weir is running, that no browser window opened by itself, and that clicking the notice opens the app on `http://localhost:9347/` (or the port shown in the tray's `Change port` item if 9347 was taken).
6. Confirm the **Create admin** screen appears when no user exists.
7. Attempt a password shorter than 8 characters and confirm it is blocked.
8. Create the first user with a valid password.
9. Confirm the setup wizard (**Set up Weir**) opens after the first user is created.
10. Confirm `Skip for now` leaves the wizard and lands on Processing.
11. Confirm the wizard can be reopened from System › About › Setup wizard › `Open setup wizard`.
12. Confirm the wizard first asks how downloads reach Weir (Deluno, Sonarr / Radarr, a download client, or Neither), and that Neither shows the typed watched and output folders for Movies and TV.
    - Choose Deluno, Sonarr / Radarr or a download client, connect it, and confirm it tests as connected and the Movies and TV workflows are offered with folders filled in, each with its kind badge.
    - Confirm an unreachable address says so and leaves Neither available.
    - Confirm `Finish setup` saves the time zone, the ticked workflows (linked to the media manager when there is one) and the automatic backup schedule, and that What's next no longer offers to connect a media manager.
13. Confirm the side menu shows Processing and Activity under Live, Library under Your library, Workflows, Rules, Media managers, Performance, Schedule, Cleanup and Alerts under Setup, and System under Weir, and that Processing is the first screen.
14. Confirm each Setup item opens its own Settings section, with the section name as the page title and no row of tabs on the page.
15. Confirm System shows the tabs About, Backups, Security and Logs.
16. In System › Backups, use **Export or restore now** to download a configuration backup.
17. Restore that backup and confirm the app remains usable.
18. Confirm System › About › Updates shows a meaningful status, even when no update is available.
19. Right-click the tray icon and confirm the menu matches `docs/tray-standard.md` (including `Restart Weir`, `Copy address`, `Open logs folder`, `Start with Windows` and the version line), that the hover text says the state, and that `Check for updates` reaches the current release status. Choose `Pause processing` and confirm the two-bars mark and `Resume processing` appear; choose it again and confirm they go. Choose `Restart Weir` and confirm the ring shows and then goes.
20. In Setup › Workflows, open a workflow's editor and use `Browse` on a folder field to pick a local folder. Confirm each row shows its kind (Weir only, or Linked to a media manager), its watched folder and the folder it cleans into in separate columns, and that `Add workflow` asks which kind first. Drag a row by its grip (or press Alt with the up or down arrow on the grip) and confirm the Priority numbers change and stay changed after a reload.
21. Enter a UNC path (`\\<nas>\<share>\folder`) manually and confirm it saves; a missing folder is a warning, not a save blocker.
22. Open Library, pick the library from the title and confirm `Check again` runs.
23. Quit Weir from the tray icon, and confirm `tray-host.log` in `C:\ProgramData\Weir` says the server host `stopped cleanly` within a few seconds, not `killing it`. Do the same after switching LAN access on or off from the tray menu.
24. Relaunch Weir and confirm the existing user, settings, and wizard completion state persist.
25. Install the next version over the current version and confirm Velopack applies a delta update cleanly.
    - With **Auto** update mode and Weir left running, let it download the update and confirm `tray-host.log` says `Update downloaded successfully.` and then `Update vX downloaded; installing once Weir has been idle for 5 minutes.`, and that no `Scheduling update` or Velopack `Running: Update.exe` line follows before the idle period ends. `work-state.json` in the data folder now refreshes about every 15 seconds.
    - While a file is processing (or queued in a workflow whose schedule window is open), confirm `work-state.json` says `"busy":true`, the log says `Weir has work to do.`, and after 6 minutes the version and the server process id have not changed. Then let the work finish and confirm the log says `Weir is idle.`.
    - Leave Weir idle. After 5 minutes confirm `tray-host.log` says `Weir has been idle for 5 minutes; installing update vX now.`, then the server `stopped cleanly`, then `Applying update ... and restarting`, and that Weir comes back on the new version with no browser window, on the same port, and `velopack_Weir.log` has no `Killing process`.
    - Before any of those applies the update, confirm `tray-host.log` says `Asking the server to save a copy of Weir's data` and then `The server saved a copy of Weir's data.`, that `backups\pre-update` holds `weir-NNNN-to-<new version>-<time>.db` with its settings, secret and `.backup.json` files (newest five only), that, when the new version changes the database, its log says it saved a second copy of its own (the first stays beside it), and that System › About says `Before updating from <old> to <new> on <date>, Weir saved a copy of its data at …`. To see a failure, fill the drive (or put a file where `backups\pre-update` belongs), let the update apply, and confirm the old version keeps running, an amber balloon and System › About say `Weir didn't update because it couldn't save a copy of its data: …`, and the next update check tries again.
    - Repeat the download with **Download only** and with **Notify only**, wait 10 minutes idle, and confirm the version and the server process id have not changed and `work-state.json` is not written for **Notify only**.
    - With a newer release published, press **Check now**, **Download update** and **Restart and apply** on System › About without waiting for the tray's schedule, in **Notify only** mode first. Confirm the buttons read `Checking…` and `Downloading update…` while they work, that only the button that applies is enabled at each step, that `tray-host.log` shows the flag being taken (`Check-now flag detected`, `Download-now flag detected`, `Apply-now flag detected`), and that the page reloads by itself on the new version.
    - Quit from the tray icon and confirm the log shows the server `stopped cleanly`, then `Applying update ... and exiting`, and that Weir is on the new version at its next start.
    - Repeat with **Restart to update** (tray menu, then System › About › **Restart and apply**) and confirm the server `stopped cleanly` before `Applying update ... and restarting`, and that Weir comes back on the new version with no browser window.
    - Download an update, then end the tray with `taskkill /PID <tray pid> /F` (the same as a Windows sign-out that does not run Quit). Start Weir and confirm `tray-host.log` shows `Update vX was left waiting to install. Installing it before the server starts.` before the `Velopack: before update` line, that the data folder now holds `update-start-attempt`, and that the new version is running. Velopack's own start-up install is off, so that line is the only way a waiting update installs at start; `velopack_Weir.log` must not say `Auto apply is true`.
    - Run the next version's `Weir-win-Setup.exe --silent` over a **running** Weir, as Deluno's update does. Confirm Setup exits 0 within seconds, `tray-host.log` says `Weir was running before this install. It starts again once Setup`, and without starting it by hand Weir answers on `/ready` on the same port within a minute, on the new version, with no browser window and no balloon. Then Quit Weir and run `--silent` again over the stopped Weir, and confirm `tray-host.log` says `Weir was not running from this install before Setup, so it is left stopped.` and Weir stays stopped.
    - Run the same Setup without `--silent` over a running Weir and confirm Velopack starts Weir once: one tray icon, `tray-host.log` showing no second start (a `Start after Setup: Weir is already running` line is the expected outcome of the extra start), and no `Weir is already running` notice from the tray.
26. Uninstall and reinstall only when intentionally testing clean-install behavior.
27. Confirm the automated packaged smoke reports that Processing placed a byte-identical
    pass-through file in the processed tree before removing its watched source.

## Docker smoke

Use the published release image, not a locally built image. The Git tag is `vX.Y.Z`; the image tag
drops the `v`, so `ghcr.io/ejm-digital/weir:X.Y.Z` is the image for tag `vX.Y.Z`. Run the checks on
both `linux/amd64` and `linux/arm64` when hardware for both is available.

1. Pull the versioned image:

   ```bash
   docker pull ghcr.io/ejm-digital/weir:X.Y.Z
   ```

2. Start with a fresh named volume:

   ```bash
   docker run --rm -p 9347:9347 -v weir-smoke:/data/weir ghcr.io/ejm-digital/weir:X.Y.Z
   ```

3. Open `http://localhost:9347/`.
4. Confirm the **Create admin** screen appears.
5. Confirm the release-candidate audit artifact includes `pass-through-proof.json`
   showing completed, byte-identical delivery and watched-source cleanup through a
   mounted Docker path.
6. Attempt a password shorter than 8 characters and confirm it is blocked.
7. Create the first user with a valid password.
8. Confirm the setup wizard opens.
9. Complete or skip the setup wizard and confirm System › About can reopen it.
10. Confirm `/health` returns healthy while the container is running.
11. Drop a file into a watched folder and confirm Processing shows it without a manual page reload, and that it then appears in Activity.
12. Confirm System › Logs shows application and runtime events, not developer build noise.
13. Confirm System › Backups can export and restore a configuration backup against the mounted volume.
14. Stop and restart the container with the same volume.
15. Confirm the user, settings, and runtime state persist.
16. Pull and run `latest` and confirm it resolves to the expected release digest.
17. Upgrade from the previous release tag to the new release tag using the same volume.
18. Confirm Docker path wording is clear: paths inside the container may differ from host/NAS paths.
19. Remove the smoke volume only after validation is complete.

## Failure handling

- Any failed smoke step gets a GitHub issue with the install type, version, exact step, expected result, actual result, and screenshots/logs with secrets removed.
- Release-blocking failures get `priority: critical`.
- Core workflow failures without data loss get `priority: high`.
