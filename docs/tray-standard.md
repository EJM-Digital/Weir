# The tray standard (Deluno and Weir)

Both apps keep an icon in the Windows notification area. This page is the one standard for what that icon shows, what its menu holds and how it behaves. Deluno and Weir each keep a word-for-word copy, and each app builds and tests its own tray. Nothing is shared in code, so neither app depends on the other.

Decided by the owner on 9 Oct 2026, from the side-by-side audit of both trays. The icon and hover text were revised the same day: they show the platform's own health only.

## What the icon shows

Each app keeps its own brand icon, so you always know which is which. A coloured dot in the corner always shows the health of the platform itself: the app, and the things it is set up to talk to.

| Dot | Meaning |
|---|---|
| green | The app is running, and everything it relies on answers |
| amber | The app is running, but something it relies on does not answer |
| red | The app itself is stopped, has crashed, or keeps failing to start |
| blinking | Starting: the dot blinks until the app can say green, amber or red |

- **What counts as "something it relies on":**
  - Deluno: a download client, an indexer, its processor (Weir), the metadata service, or a library folder or disk.
  - Weir: Deluno, or its watched, work or output folders.
- **The dot never reflects downloads, files or things waiting on you.** Those stay in each app's own screens and bell. A failed download or a file on hold is not a platform problem.
- **Two extra marks**, in the opposite corner, on top of the colour:
  - **pause bars** while automation (Deluno) or processing (Weir) is paused;
  - **a blue arrow** when an update is ready to install.
  - When both apply, the pause bars show.
- **No mark for being busy.** Downloading and processing are normal.
- **How it is drawn:** at the shell's small-icon size, with a thin outline so it reads on light and dark taskbars. The icon file carries 16, 20, 24, 32, 40, 48, 64, 128 and 256 px frames.
- **When it appears:** at once, blinking while it starts. Never only after the server is up.

## What the hover text says

`<App> - <status>`: the platform's status, and when it is amber or red, what is wrong, so the reason for the colour is never hidden. No counts of downloads or items waiting. Examples:

- `Deluno - Running at http://localhost:7879`
- `Deluno - SABnzbd isn't answering`
- `Deluno - Paused`
- `Weir - Starting...`
- `Weir - Update ready (1.0.0-rc.11)`
- `Weir - Stopped - choose Restart Weir`

It is kept short but never cut mid-word. It changes as the state changes, with no timer.
## The menu

```
Open <App>                                   (bold; a left click does the same)
<status line, same words as the hover text>  (greyed out)
---
Pause automation / Resume automation         (Weir: Pause processing / Resume processing)
Restart <App>
---
Copy address
Allow other devices on your network...
Only allow this PC
Change port (<port>)...
---
Open data folder
Open logs folder
[x] Start with Windows
---
Check for updates                            (changes with the update, below)
<App> v<version>                             (greyed out)
Report a problem...
---
Quit <App>                                   (hover text says what it stops)
```

- **The update item:** `Check for updates`, then `Checking for updates...` (greyed out), then `Downloading update...` (greyed out), then `Download update (v<version>)`, then `Restart to update (v<version>)`. While update checking is off (the default for a new install) the item reads `Update checking is off` and opens System › About, where it is switched on; nothing is looked for or downloaded until then, though an update already downloaded can still be restarted into.
- **Report a problem...** opens the logs folder while the repositories are private. Once they are public, it opens a new GitHub issue page with the version filled in. No paths or addresses ever go in the link.
- **Start with Windows** is asked once, at install or first run: "Start <App> when you sign in to Windows?", Yes or No. Nothing is on by default. The tick in the menu shows the current choice and flips it at any time, without closing the app.
- **Quit <App>** stops the app. Its hover text says so:
  - Deluno: "Stops Deluno and closes this icon. Download clients Deluno started keep running."
  - Weir: "Stops Weir after running jobs finish, then closes this icon. A downloaded update is installed."
- **There is no Stop or Start item.** Restart covers "it's misbehaving", and Quit covers "turn it off".

**Deluno-only lines:**
- `Add Transmission's torrents back...` and `Try again: install apps...` show at the top only while something is pending.
- `Run as a Windows service...` and `Let any address in...` sit in an `Advanced` submenu above Quit.

## Behaviour

- **Clicks:** a left click opens the app once, and so does a double-click. A middle click does nothing. If the app can't open yet, the click says why, in a balloon.
- **The address** the app opens is always the live one, not a saved setting.
- **A browser opens only when you click** something: the icon, a menu item or a balloon. Start-up, sign-in start, an update restart, a port change and a second launch never open one.
- **A second launch** shows a balloon from the running copy, "<App> is already running", with the address.
- **Balloons:**
  - a click does what the balloon offers: open Updates, Restart, open the logs folder, Try again;
  - Windows decides how long a notice stays: it ignores the time an app asks for. So a notice is never the only place a problem shows. A platform problem also shows in the dot's colour, the hover text and the menu's status line, until it is dealt with.
