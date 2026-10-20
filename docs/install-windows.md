# Install Weir on Windows

This takes about five minutes. You need Windows 10 or 11 (64-bit) and a normal user account. You do not
need administrator rights to install.

Weir is in its release-candidate stage, so the installer is on the [Releases page](https://github.com/EJM-Digital/Weir/releases)
marked **Pre-release**. It is not the "Latest" release, so use the list, not the Latest button.

## 1. Download the installer

1. Open the [Releases page](https://github.com/EJM-Digital/Weir/releases).
2. Pick the newest release at the top.
3. Under **Assets**, download **`Weir-win-Setup.exe`**.

## 2. Run it

Double-click `Weir-win-Setup.exe`.

Weir is not code-signed, so Windows may stop you. This is expected, and you can read the source and the
build steps in this repository if you want to check what you are running.

- **"Windows protected your PC"** (SmartScreen): choose **More info**, then **Run anyway**.
- **Your browser may warn that the file "isn't commonly downloaded".** Choose **Keep** (Edge) or
  **Keep anyway** (Chrome).

The installer needs no choices. It puts Weir in `%LocalAppData%\Weir`, adds a Start Menu and desktop
shortcut, and starts Weir.

## 3. Answer two questions

The first time Weir starts, it asks three things, one after the other.

**"Allow Weir on your network?"** This is the Windows Firewall question. It decides who can reach Weir.

| You choose | What happens |
| --- | --- |
| **No** | Weir can only be reached from this PC. Nothing else on your network can even connect. This is the safe default and the right answer if Weir and your browser are on the same PC. |
| **Yes** | Windows asks for administrator approval once, then Weir is reachable from other devices on your home network (a phone, another computer, Deluno or Radarr on another machine). Weir adds one firewall rule named **Weir**, for every network profile (**Domain**, **Private** and **Public**), because Windows often marks a home network as Public. Weir still needs its own sign-in. |

You can change your mind later, in either of two places. In Weir, open **System › About** and choose
**Devices on my network** or **This PC only**. Or right-click the Weir icon in the system tray and choose
**Allow other devices on your network...** or **Only allow this PC**. Weir restarts when you do.

**"Start Weir when you sign in to Windows?"** Nothing starts with Windows unless you say yes. **No** (or a silent install)
leaves it off. You can switch it any time with **Start with Windows** in the tray menu. Versions up to
1.0.0-rc.10 registered it at install without asking. When Weir finds that entry and no answer has been recorded (the answer
is recorded when you answer the question or tick or untick the menu item), it asks the same question once, after its icon is
up, the next time you start Weir yourself (never at sign-in, during a silent start, or with no desktop). Yes keeps the entry,
No removes it.

**"Start Weir" and a port number.** Keep the default, **9347**, unless something else on the PC already uses
it. In that case the window says so and suggests the next free port. Weir remembers your choice.

## 4. Create your account

A notice from the tray icon says Weir is running. Click it, or the icon, and your browser opens Weir at `http://localhost:9347/`. Weir never opens a browser window by itself. It asks for a username and password. This creates the
admin account, and there is no separate sign-up.

Because you are on the same PC, Weir does not ask for a setup code. If you create the account from another
device instead, Weir asks for a code. It is in the file `C:\ProgramData\Weir\setup-code` on the PC running
Weir.

Next, Weir's setup wizard asks how your downloads reach it and which folders to watch. That part is the same
on every install. The [Quickstart](../docs-site/docs/quickstart.md#3-follow-the-setup-wizard) walks through it.

## Where things are

| What | Where |
| --- | --- |
| The program | `%LocalAppData%\Weir` (shortcut: Start Menu, **Weir**) |
| Your data: database, logs, backups, settings | `C:\ProgramData\Weir` |
| The port you chose | `C:\ProgramData\Weir\port.txt` |
| The tray log (why Weir did something at start-up) | `C:\ProgramData\Weir\tray-host.log` |
| What the server printed while it started, and anything it wrote to its error output (the answer when the server never came up) | `C:\ProgramData\Weir\server-host.log` |

The data folder is locked to your Windows account, so other accounts on the PC cannot read it.

Weir runs as you, in your own sign-in session. It is not a Windows service. That is on purpose: a service
cannot see mapped network drives, and a NAS is where most media lives. If you chose to start it with Windows,
it starts when you sign in and does not open a browser window when it does.

## The tray icon

Weir lives in the system tray, next to the clock. If you do not see it, click the small arrow to show
hidden icons. Click the icon to open Weir in your browser. A dot in the icon's corner shows how Weir is
doing: green when it is running and everything it relies on answers, amber when a media manager such as Deluno or one
of its folders does not answer, red when Weir has stopped, and blinking while it starts. Two bars in the opposite
corner mean processing is paused, and a blue arrow means an update is waiting. Hover over the icon to read the state
and, when the dot is amber or red, what is wrong. Right-click it for the menu:

- **Open Weir**, then a greyed line with the state
- **Pause processing** (or **Resume processing**) and **Restart Weir**
- **Copy address**, **Allow other devices on your network...** (or **Only allow this PC**) and **Change port**
- **Open data folder**, **Open logs folder** and **Start with Windows**
- **Check for updates**, the version, and **Report a problem...**
- **Quit Weir**

If Weir stops and cannot start again by itself, the icon turns red and says so; choose **Restart Weir**.
Quitting asks Weir to finish properly first, so running work and the database close in order. The tray follows
the [tray standard](tray-standard.md).

## Open Weir from another computer

Only after you have allowed other devices on your network (step 3). On the other computer, open
`http://<your-pc-name>:9347/`. **System › About** shows the exact address and has a **Copy** button.

Weir is meant for your home network. Do not forward port 9347 on your router. To reach Weir from outside
your home, put it behind a reverse proxy with HTTPS. See the [reverse proxy guide](../docs-site/docs/deployment/reverse-proxy.md).

## Update Weir

A new install does not check for updates. Setup asks whether to, and the answer can be changed at any time under
**System › About**, **Check for updates**. While it is off, Weir does not look on its own and does not download anything on its own. **Check now**
(in System › About or the tray menu) and a download you then ask for still work. An install that was already checking before this
choice existed keeps checking.

When it is on, Weir checks GitHub for new versions and tells you through the tray icon. Under **System › About**,
**Update mode** decides what happens:

- **Auto**: downloads and installs by itself (the default).
- **Download only**: downloads, then waits for you to choose **Restart to update**.
- **Notify only**: tells you, nothing more.

Updates are small, need no administrator rights, and keep your data. After one, Weir starts again quietly,
without opening a browser window.

A release candidate is offered the next release candidate as well as stable releases; a stable install is only
ever offered stable ones. You can also download the new `Weir-win-Setup.exe` from the Releases page and run it.
It installs over the old one. Your data is not touched.

Before the tray applies an update, the Weir that is still running saves a copy of your data in
`C:\ProgramData\Weir\backups\pre-update`: the database, your settings files and the two secrets (the newest five copies
are kept). If it can't (the drive is full, say), Weir does not update: the old version keeps running, a balloon and
**System › About** say why, and the next update check tries again. The new version then saves a copy of its own when it starts, so an update that arrives another way, such as
running `Weir-win-Setup.exe` over the top, is covered too; if that fails Weir stays stopped and the tray's hover text and
balloon say why. A Weir still running when the tray starts with an update waiting is asked for its copy first. **System › About** says where the latest copy is. Before a big upgrade you can also take
a backup under **System › Backups**.

## Uninstall

1. Open **Settings › Apps › Installed apps**.
2. Find **Weir**, open its menu and choose **Uninstall**.

| Removed | Kept |
| --- | --- |
| The program in `%LocalAppData%\Weir` | Your data in `C:\ProgramData\Weir` (database, logs, backups, settings) |
| Start Menu and desktop shortcuts | The files in your own media folders. Weir never deletes those by uninstalling |
| The "start with Windows" entry | |
| The **Weir** firewall rule, only if you ran the uninstaller as administrator. Otherwise it stays and does nothing, because the program it names is gone | |

Weir is stopped for you before it is removed.

To remove everything, delete `C:\ProgramData\Weir` afterwards. To move to a new PC, copy that folder
before you delete it. Reinstalling on the same PC finds your data again.

## Installing from a script

Setting Weir up on many machines, or from another installer? Plain `Weir-win-Setup.exe` assumes a person is at
the screen. Use `Weir-win-Setup.exe --silent`. The details, including how to choose a port and allow your
network without a prompt, are in the [Windows installer reference](../docs-site/docs/deployment/windows.md#installing-weir-from-another-program).

## If something goes wrong

- **The browser does not open.** Click the Weir tray icon. If there is no icon, start **Weir** from the Start Menu.
- **"Port in use".** Pick another port in the first-run window, or later with **Change port**.
- **Other devices cannot connect.** Open **System › About**. Under **This PC** it says whether network access is
  on, and shows "Blocked by Windows Firewall" if Windows is stopping other devices. **Try again** asks Windows once more.
- **"Weir didn't start after 5 tries".** The tray tried to start Weir five times in a short while and stopped trying. Click the notice or
  **Restart Weir** to try again; `tray-host.log` says why each try failed (it hung in a step, or it stopped with a code), and
  `server-host.log` has what the server printed. If Weir starts only on a retry it still works, and System › Logs shows a warning that it needed one.
- **Anything else.** Read `C:\ProgramData\Weir\tray-host.log` and **System › Logs**, then
  [open an issue](https://github.com/EJM-Digital/Weir/issues) and include what they say.
