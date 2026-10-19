// The Windows PC under test, as the Windows scenarios ask about it: Setup and the uninstaller run for real, the tray and the server started
// and stopped, the files, access lists and registry entries read. Every change it makes (installing, uninstalling, stopping Weir, running
// the recovery command against the machine's own data folder) is meant for a clean GitHub-hosted runner and is refused anywhere else.
import { execFile, spawn } from "node:child_process";
import { createHash } from "node:crypto";
import { createReadStream } from "node:fs";
import { mkdir, readdir, readFile, rm, stat, writeFile } from "node:fs/promises";
import { join, relative } from "node:path";

import { pollUntil } from "../docker-smoke/poll.mjs";

const POWERSHELL = "powershell.exe";
// Hang guards: each ends the moment the thing it waits for has happened.
const SETUP_CEILING_MS = 600_000;
const RECOVER_CEILING_MS = 120_000;
const STOP_CEILING_MS = 60_000;
const RUN_CEILING_MS = 120_000;

/** Why this is not a machine to install and uninstall Weir on, or null when it is a GitHub-hosted Windows runner. */
export function whyNotAThrowawayMachine(env, platform) {
  if (platform !== "win32") return `this is ${platform}, not Windows`;
  if (env.GITHUB_ACTIONS !== "true" || env.RUNNER_ENVIRONMENT !== "github-hosted") {
    return "this is not a GitHub-hosted runner, so a clean machine cannot be assumed; installing and uninstalling Weir here would change your own Weir";
  }
  return null;
}

/** The environment a person's console has: nothing Weir's tray gives the server, and no data folder named. */
export function consoleEnvironment(env) {
  return Object.fromEntries(Object.entries(env).filter(([name]) => !name.toUpperCase().startsWith("WEIR_")));
}

export function run(program, args, { env = process.env, ceilingMs = 120_000 } = {}) {
  return new Promise((resolve, reject) => {
    execFile(program, args, { env, windowsHide: true, timeout: ceilingMs, maxBuffer: 32 * 1024 * 1024 }, (error, stdout, stderr) => {
      if (error) reject(new Error(`${program} ${args.join(" ")} failed: ${error.message}\n${stderr}`.trim(), { cause: error }));
      else resolve(stdout);
    });
  });
}

/**
 * Runs a PowerShell script that writes JSON; what it needs to know arrives in the environment, never spliced into the script. A script that
 * finds nothing and says nothing succeeds (a failed lookup under -ErrorAction SilentlyContinue would otherwise set the exit code); an error that
 * stops the script still fails it.
 */
export async function json(script, values = {}) {
  const env = { ...process.env, ...Object.fromEntries(Object.entries(values).map(([name, value]) => [`SCENARIO_${name}`, String(value)])) };
  const text = await run(POWERSHELL, ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", `$ErrorActionPreference = 'Stop'; ${script}
exit 0`], { env });
  return text.trim() ? JSON.parse(text) : null;
}

const sha256 = (path) =>
  new Promise((resolve, reject) => {
    const hash = createHash("sha256");
    createReadStream(path).on("data", (chunk) => hash.update(chunk)).on("error", reject).on("end", () => resolve(hash.digest("hex")));
  });

async function walk(folder) {
  const found = [];
  for (const entry of await readdir(folder, { withFileTypes: true })) {
    const path = join(folder, entry.name);
    if (entry.isDirectory()) found.push(...(await walk(path)));
    else found.push(path);
  }
  return found;
}

export class WindowsMachine {
  #env;

  /** @param {NodeJS.ProcessEnv} env */
  constructor(env = process.env) {
    if (env.WEIR_HOME) throw new Error("WEIR_HOME is set, so the tray would keep its data somewhere other than the default folder this scenario reads.");
    this.#env = env;
    const installRoot = join(env.LOCALAPPDATA, "Weir");
    const runtimeHome = join(env.ProgramData ?? "C:\\ProgramData", "Weir");
    this.paths = {
      installRoot,
      updater: join(installRoot, "Update.exe"),
      packagesFolder: join(installRoot, "packages"),
      trayExe: join(installRoot, "current", "Weir.exe"),
      serverExe: join(installRoot, "current", "server", "WeirServer.exe"),
      runtimeHome,
      database: join(runtimeHome, "data", "weir.sqlite3"),
      trayLog: join(runtimeHome, "tray-host.log"),
      logsFolder: join(runtimeHome, "logs"),
    };
  }

  /** Runs Setup the way another program installing Weir does, and waits for Setup's own exit. */
  async install(setupPath) {
    const began = Date.now();
    const exitCode = await this.#exitOf(setupPath, ["--silent"], SETUP_CEILING_MS, "Setup");
    return { exitCode, seconds: Math.round((Date.now() - began) / 1000) };
  }

  /** Writes update-settings.json, the file the tray reads its update choices from, as the tray or the Settings page would. */
  async writeUpdateSettings(settings) {
    await mkdir(this.paths.runtimeHome, { recursive: true });
    await writeFile(join(this.paths.runtimeHome, "update-settings.json"), JSON.stringify(settings));
  }

  /** Chooses "only say that an update exists" in update-settings.json, so no start of Weir installs a release over the build under test. With `onlyWhenAsked` it also checks for updates only when asked, so the menu's update item and the balloons are those of the scenario alone. */
  keepUpdatesFromInstalling({ onlyWhenAsked = false } = {}) {
    return this.writeUpdateSettings(onlyWhenAsked ? { mode: "NotifyOnly", checkOnStartup: false, checkIntervalMinutes: 0 } : { mode: "NotifyOnly" });
  }

  /** Leaves the machine as a clean runner is: Weir stopped and uninstalled, and its install and data folders gone. */
  async removeEverything() {
    await this.stopWeir();
    if (await this.exists(this.paths.updater)) {
      const { exitCode } = await this.uninstall();
      if (exitCode !== 0) throw new Error(`The uninstaller exited ${exitCode}, not 0.`);
      await pollUntil(async () => ((await this.exists(this.paths.trayExe)) ? undefined : true), {
        what: "the uninstaller to remove Weir.exe",
        ceilingMs: STOP_CEILING_MS * 2,
        intervalMs: 1000,
      });
      await this.#waitForUninstallHelpers();
    }
    for (const folder of [this.paths.runtimeHome, this.paths.installRoot]) await rm(folder, { recursive: true, force: true, maxRetries: 10, retryDelay: 500 });
  }

  /** The uninstaller hands the last of its clean-up to processes that outlive it; an install made while they run would be cleaned away with the old one. */
  async #waitForUninstallHelpers() {
    const running = () =>
      json(
        `ConvertTo-Json -Compress -InputObject @(Get-CimInstance Win32_Process | Where-Object { $_.ProcessId -ne $PID -and $_.CommandLine -and $_.CommandLine.IndexOf($env:SCENARIO_ROOT, [StringComparison]::OrdinalIgnoreCase) -ge 0 } | ForEach-Object { $_.Name })`,
        { ROOT: this.paths.installRoot },
      );
    await pollUntil(async () => ((await running()).length === 0 ? true : undefined), {
      what: "the uninstaller's helper processes to end",
      ceilingMs: STOP_CEILING_MS,
      intervalMs: 500,
    });
  }

  /** What is missing from an install that has just been made, in words, or undefined when it is whole. */
  async missingFromInstall() {
    for (const [what, path] of [["Update.exe", this.paths.updater], ["Weir.exe", this.paths.trayExe], ["WeirServer.exe", this.paths.serverExe]]) {
      if (!(await this.exists(path))) return `${what} (${path}) is not there`;
    }
    return undefined;
  }

  /** Runs Velopack's own uninstaller, silently. */
  async uninstall() {
    return { exitCode: await this.#exitOf(this.paths.updater, ["uninstall", "--silent"], SETUP_CEILING_MS, "The uninstaller") };
  }

  #exitOf(program, args, ceilingMs, what) {
    return new Promise((resolve, reject) => {
      const child = spawn(program, args, { stdio: ["ignore", "pipe", "pipe"], windowsHide: true, env: this.#env });
      child.stdout.resume();
      child.stderr.resume();
      const timer = setTimeout(() => {
        child.kill();
        reject(new Error(`${what} was still running after ${ceilingMs / 60_000} minutes.`));
      }, ceilingMs);
      child.on("error", (error) => {
        clearTimeout(timer);
        reject(error);
      });
      child.on("exit", (code) => {
        clearTimeout(timer);
        resolve(code);
      });
    });
  }

  /** Starts the installed tray the way an unattended caller does: no window, a port given. Returns its process id. */
  startTray(port) {
    const child = spawn(this.paths.trayExe, ["--port", String(port), "--silent"], {
      detached: true,
      stdio: "ignore",
      windowsHide: true,
      env: consoleEnvironment(this.#env),
    });
    child.unref();
    return child.pid;
  }

  /**
   * Starts the installed tray with these arguments, as the person who owns the desktop would (its windows are shown). `exit` settles with the
   * tray's exit code when it ends. The variables of Weir's are not passed on unless given. `executable` runs a Weir.exe other than the installed one.
   */
  startTrayWith({ args, env = {}, executable = this.paths.trayExe }) {
    const child = spawn(executable, args, { detached: true, stdio: "ignore", windowsHide: false, env: { ...consoleEnvironment(this.#env), ...env } });
    const exit = new Promise((resolve) => child.on("exit", (code) => resolve(code)));
    child.unref();
    return { pid: child.pid, exit };
  }

  /** Runs the installed Weir.exe to the end with these arguments (a second launch, `--version`, the firewall commands) and returns what it said. */
  runTray(args, { env = {} } = {}) {
    return new Promise((resolve, reject) => {
      const child = spawn(this.paths.trayExe, args, { stdio: ["ignore", "pipe", "pipe"], windowsHide: false, env: { ...consoleEnvironment(this.#env), ...env } });
      let stdout = "";
      let stderr = "";
      child.stdout.on("data", (chunk) => (stdout += chunk));
      child.stderr.on("data", (chunk) => (stderr += chunk));
      const timer = setTimeout(() => {
        child.kill();
        reject(new Error(`Weir.exe ${args.join(" ")} did not end within ${RUN_CEILING_MS / 1000} s.`));
      }, RUN_CEILING_MS);
      child.on("error", (error) => {
        clearTimeout(timer);
        reject(error);
      });
      child.on("close", (exitCode) => {
        clearTimeout(timer);
        resolve({ exitCode, stdout, stderr });
      });
    });
  }

  /** Ends one process, by its id and nothing else; a process that is already gone is not an error. */
  async killProcess(pid) {
    await run("taskkill.exe", ["/PID", String(pid), "/F"]).catch(() => undefined);
  }

  /** Whether the process is still running. */
  async isRunning(pid) {
    return (await json(`ConvertTo-Json -Compress -InputObject ([bool](Get-Process -Id ([int]$env:SCENARIO_PID) -ErrorAction SilentlyContinue))`, { PID: pid })) === true;
  }

  /** Browser processes whose command line names the address: { pid, name, commandLine }. A browser Weir opened is found here. */
  browsersShowing(addressPart) {
    return json(
      `$part = $env:SCENARIO_PART
      ConvertTo-Json -Compress -InputObject @(Get-CimInstance Win32_Process -Filter "Name = 'msedge.exe' OR Name = 'chrome.exe' OR Name = 'firefox.exe' OR Name = 'iexplore.exe'" |
        Where-Object { $_.CommandLine -and $_.CommandLine.Contains($part) } |
        ForEach-Object { [pscustomobject]@{ pid = [int]$_.ProcessId; name = $_.Name; commandLine = $_.CommandLine } })`,
      { PART: addressPart },
    );
  }

  /**
   * The environment a running process of this account was started with, as { NAME: value } (the names in upper case): what the tray gave the
   * server. It is read from the process's own memory, the way a debugger reads it.
   */
  processEnvironment(pid) {
    return json(
      `Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class ProcessEnvironment {
    [StructLayout(LayoutKind.Sequential)] struct Basic { public IntPtr Exit; public IntPtr Peb; public IntPtr Affinity; public IntPtr Priority; public IntPtr Id; public IntPtr Parent; }
    [DllImport("ntdll.dll")] static extern int NtQueryInformationProcess(IntPtr process, int kind, ref Basic basic, int size, out int returned);
    [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(int access, bool inherit, int pid);
    [DllImport("kernel32.dll")] static extern bool ReadProcessMemory(IntPtr process, IntPtr address, byte[] buffer, IntPtr size, out IntPtr read);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    static IntPtr Pointer(IntPtr process, IntPtr address) {
        var buffer = new byte[IntPtr.Size]; IntPtr read;
        if (!ReadProcessMemory(process, address, buffer, (IntPtr)buffer.Length, out read)) throw new InvalidOperationException("The process memory could not be read.");
        return new IntPtr(BitConverter.ToInt64(buffer, 0));
    }
    public static string[] Read(int pid) {
        var process = OpenProcess(0x0410, false, pid);
        if (process == IntPtr.Zero) throw new InvalidOperationException("The process could not be opened.");
        try {
            var basic = new Basic(); int returned;
            if (NtQueryInformationProcess(process, 0, ref basic, Marshal.SizeOf(basic), out returned) != 0) throw new InvalidOperationException("The process could not be queried.");
            var parameters = Pointer(process, IntPtr.Add(basic.Peb, 0x20));
            var environment = Pointer(process, IntPtr.Add(parameters, 0x80));
            var size = Pointer(process, IntPtr.Add(parameters, 0x3F0)).ToInt64();
            var block = new byte[size]; IntPtr read;
            if (!ReadProcessMemory(process, environment, block, (IntPtr)size, out read)) throw new InvalidOperationException("The environment could not be read.");
            return Encoding.Unicode.GetString(block).Split(new[] { (char)0 }, StringSplitOptions.RemoveEmptyEntries);
        } finally { CloseHandle(process); }
    }
}
'@
      $pairs = @{}
      foreach ($entry in [ProcessEnvironment]::Read([int]$env:SCENARIO_PID)) { $at = $entry.IndexOf('=', 1); if ($at -gt 0) { $pairs[$entry.Substring(0, $at).ToUpperInvariant()] = $entry.Substring($at + 1) } }
      ConvertTo-Json -Compress -InputObject $pairs`,
      { PID: pid },
    );
  }

  /** Whether Windows Firewall has Weir's own rule. */
  async firewallRuleExists() {
    return (await json(`ConvertTo-Json -Compress -InputObject ([bool](Get-NetFirewallRule -DisplayName 'Weir' -ErrorAction SilentlyContinue))`)) === true;
  }

  /** The folders File Explorer has open: their names. */
  explorerFolders() {
    return json(
      `ConvertTo-Json -Compress -InputObject @((New-Object -ComObject Shell.Application).Windows() | ForEach-Object { try { $_.Document.Folder.Self.Path } catch { } } | Where-Object { $_ })`,
    );
  }

  /** Closes the File Explorer windows. */
  closeExplorerFolders() {
    return json(`(New-Object -ComObject Shell.Application).Windows() | ForEach-Object { try { $_.Quit() } catch { } }; ConvertTo-Json -Compress -InputObject $true`);
  }

  /** Takes away the update Velopack has downloaded and not installed, so a quit or a start cannot install it. */
  async discardDownloadedUpdate() {
    const packages = join(this.paths.installRoot, "packages");
    // Velopack keeps its lock file open for as long as the updater runs; everything else is the downloaded release.
    for (const entry of await readdir(packages).catch(() => [])) {
      if (entry !== ".velopack_lock") await rm(join(packages, entry), { recursive: true, force: true });
    }
  }

  /** What a file of the data folder holds, without the line end. */
  async dataFile(name) {
    return (await readFile(join(this.paths.runtimeHome, name), "utf8")).trim();
  }

  /** The files of the data folder named, deleted if they are there. */
  async removeFromDataFolder(...names) {
    for (const name of names) await rm(join(this.paths.runtimeHome, name), { force: true });
  }

  async writeToDataFolder(name, text) {
    await mkdir(this.paths.runtimeHome, { recursive: true });
    await writeFile(join(this.paths.runtimeHome, name), text);
  }

  /** The tray and server processes running from the install: { name, pid, parentPid, hasWindow }. */
  processes() {
    return json(
      `$root = $env:SCENARIO_ROOT + '\\'
      ConvertTo-Json -Compress -InputObject @(Get-CimInstance Win32_Process -Filter "Name = 'Weir.exe' OR Name = 'WeirServer.exe'" |
        Where-Object { $_.ExecutablePath -and $_.ExecutablePath.StartsWith($root, [StringComparison]::OrdinalIgnoreCase) } |
        ForEach-Object {
          $process = Get-Process -Id $_.ProcessId -ErrorAction SilentlyContinue
          [pscustomobject]@{ name = $_.Name; pid = [int]$_.ProcessId; parentPid = [int]$_.ParentProcessId; hasWindow = [bool]($process -and $process.MainWindowHandle -ne [IntPtr]::Zero) }
        })`,
      { ROOT: this.paths.installRoot },
    );
  }

  /** Stops the install's own tray, then its server, by process id, and waits until neither runs. Nothing else is touched. */
  async stopWeir() {
    for (const process of (await this.processes()).toSorted((a, b) => Number(b.name === "Weir.exe") - Number(a.name === "Weir.exe"))) {
      await run("taskkill.exe", ["/PID", String(process.pid), "/F"]).catch(() => undefined);
    }
    await pollUntil(async () => ((await this.processes()).length === 0 ? true : undefined), {
      what: "the installed Weir to stop",
      ceilingMs: STOP_CEILING_MS,
      intervalMs: 500,
    });
  }

  /** The addresses something listens on at the port. */
  listeningAddresses(port) {
    return json(
      `ConvertTo-Json -Compress -InputObject @(Get-NetTCPConnection -LocalPort ([int]$env:SCENARIO_PORT) -State Listen -ErrorAction SilentlyContinue | ForEach-Object { $_.LocalAddress } | Sort-Object -Unique)`,
      { PORT: port },
    );
  }

  /** The Windows account the tray runs as. */
  account() {
    return json(
      `$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
      ConvertTo-Json -Compress -InputObject ([pscustomobject]@{ sid = $identity.User.Value; name = $identity.Name })`,
    );
  }

  /** Who a file lets in: whether it inherits nothing, and every rule with the account's security identifier. */
  accessList(path) {
    return json(
      `$acl = Get-Acl -LiteralPath $env:SCENARIO_PATH
      ConvertTo-Json -Compress -Depth 4 -InputObject ([pscustomobject]@{
        protected = [bool]$acl.AreAccessRulesProtected
        rules = @($acl.Access | ForEach-Object {
          [pscustomobject]@{
            sid = $_.IdentityReference.Translate([Security.Principal.SecurityIdentifier]).Value
            name = $_.IdentityReference.Value
            allow = ($_.AccessControlType -eq 'Allow')
            rights = $_.FileSystemRights.ToString()
          }
        })
      })`,
      { PATH: path },
    );
  }

  /** The shortcuts to Weir on this account's Start Menu and desktop (and the shared ones), as paths. */
  async shortcuts() {
    const found = await json(
      `$places = 'Programs', 'CommonPrograms', 'Desktop', 'CommonDesktopDirectory', 'Startup', 'CommonStartup' | ForEach-Object { [Environment]::GetFolderPath($_) } | Where-Object { $_ -and (Test-Path -LiteralPath $_) }
      ConvertTo-Json -Compress -InputObject @($places | ForEach-Object { Get-ChildItem -LiteralPath $_ -Filter 'Weir*.lnk' -Recurse -File -ErrorAction SilentlyContinue } | ForEach-Object { $_.FullName })`,
    );
    return found ?? [];
  }

  /** The command line behind the Weir entry of the current account's Run key, or null when there is none. */
  runEntry() {
    return json(
      `$entry = Get-ItemProperty -Path 'HKCU:\\Software\\Microsoft\\Windows\\CurrentVersion\\Run' -Name 'Weir' -ErrorAction SilentlyContinue
      if ($entry) { ConvertTo-Json -Compress -InputObject ([string]$entry.Weir) }`,
    );
  }

  /** The mark a running tray leaves in the current account's registry, or null when there is none. */
  trayMark() {
    return json(
      `$mark = Get-ItemProperty -Path 'HKCU:\\SOFTWARE\\WeirTrayRunning' -ErrorAction SilentlyContinue
      if ($mark) { ConvertTo-Json -Compress -InputObject ([pscustomobject]@{ processId = [int]$mark.ProcessId; executable = [string]$mark.Executable }) }`,
    );
  }

  /** Runs `WeirServer.exe --version` and returns what it prints. */
  async serverVersion() {
    return (await run(this.paths.serverExe, ["--version"], { env: consoleEnvironment(this.#env) })).trim();
  }

  /**
   * Runs the installed `WeirServer.exe recover` as a person's console does: stdin closed so nothing can be prompted, none of the variables
   * the tray gives the server, and no data folder named, so the command must find the one the installed Weir uses. Never throws on the exit code.
   */
  recover(args) {
    return new Promise((resolve, reject) => {
      const child = spawn(this.paths.serverExe, ["recover", ...args], { stdio: ["pipe", "pipe", "pipe"], windowsHide: true, env: consoleEnvironment(this.#env) });
      child.stdin.end();
      let stdout = "";
      let stderr = "";
      child.stdout.on("data", (chunk) => (stdout += chunk));
      child.stderr.on("data", (chunk) => (stderr += chunk));
      const timer = setTimeout(() => {
        child.kill();
        reject(new Error(`WeirServer.exe recover did not finish within ${RECOVER_CEILING_MS / 1000} s.`));
      }, RECOVER_CEILING_MS);
      child.on("error", (error) => {
        clearTimeout(timer);
        reject(error);
      });
      child.on("close", (exitCode) => {
        clearTimeout(timer);
        resolve({ exitCode, stdout, stderr });
      });
    });
  }

  /** Where the tray keeps a secret of the runtime home, by file name. */
  secretPath(name) {
    return join(this.paths.runtimeHome, name);
  }

  async ensureFolders(...folders) {
    for (const folder of folders) await mkdir(folder, { recursive: true });
  }

  exists(path) {
    return stat(path).then(
      () => true,
      () => false,
    );
  }

  readText(path) {
    return readFile(path, "utf8");
  }

  /** Every file under the folder: path relative to it and size, with the SHA-256 of the bytes when asked. Empty when the folder is not there. */
  async files(folder, { hash = false } = {}) {
    if (!(await this.exists(folder))) return [];
    const listed = await Promise.all(
      (await walk(folder)).map(async (path) => ({
        path: relative(folder, path),
        bytes: (await stat(path)).size,
        ...(hash ? { sha256: await sha256(path) } : {}),
      })),
    );
    return listed.toSorted((a, b) => a.path.localeCompare(b.path));
  }

  /** Everything the server has logged, oldest file first. */
  async serverLogs() {
    const files = (await this.files(this.paths.logsFolder)).map((file) => join(this.paths.logsFolder, file.path));
    return (await Promise.all(files.map((file) => readFile(file, "utf8")))).join("\n");
  }
}
