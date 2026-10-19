// Sends the tray's requests for github.com to this PC, so the tray updates from a feed this run controls while it runs its own, unchanged
// update code. Two things change on the PC, both undone at the end: the hosts file names github.com and api.github.com as this PC, and a
// certificate made here for those two names is trusted, so the tray (which trusts the PC's own store) accepts the answer. Meant for a
// throwaway GitHub-hosted runner; the entry point refuses any other machine before this is reached.
import { randomBytes } from "node:crypto";
import { readFile, writeFile } from "node:fs/promises";
import { join } from "node:path";

import { json, run } from "./windows-machine.mjs";

export const NAMES = ["github.com", "api.github.com"];
const MARK = "# weir-scenario";
const HOSTS = join(process.env.SystemRoot ?? "C:\\Windows", "System32", "drivers", "etc", "hosts");

/** The hosts file's text with each name pointing at this PC, and nothing left of an earlier run's lines. */
export function hostsRedirecting(text, names = NAMES) {
  const kept = hostsRestored(text);
  const lines = names.map((name) => `127.0.0.1 ${name} ${MARK}`);
  return `${kept.replace(/\s*$/, "")}\r\n${lines.join("\r\n")}\r\n`;
}

/** The hosts file's text without the lines this run added. */
export function hostsRestored(text) {
  return text
    .split(/\r?\n/)
    .filter((line) => !line.trimEnd().endsWith(MARK))
    .join("\r\n");
}

export class GithubRedirect {
  #thumbprint = null;
  #redirected = false;

  /** Makes and trusts the certificate. Returns what an HTTPS server needs to answer with it. */
  async trustCertificate(folder) {
    const passphrase = randomBytes(16).toString("hex");
    const pfx = join(folder, "github-stand-in.pfx");
    const made = await json(
      `$cert = New-SelfSignedCertificate -Subject 'CN=github.com' -DnsName ($env:SCENARIO_NAMES -split ',') -CertStoreLocation 'Cert:\\LocalMachine\\My' -KeyExportPolicy Exportable -KeyAlgorithm RSA -KeyLength 2048 -HashAlgorithm SHA256 -NotAfter (Get-Date).AddDays(2)
      $secret = ConvertTo-SecureString -String $env:SCENARIO_PASSPHRASE -AsPlainText -Force
      Export-PfxCertificate -Cert $cert -FilePath $env:SCENARIO_PFX -Password $secret | Out-Null
      $public = [IO.Path]::ChangeExtension($env:SCENARIO_PFX, '.cer')
      Export-Certificate -Cert $cert -FilePath $public | Out-Null
      Import-Certificate -FilePath $public -CertStoreLocation 'Cert:\\LocalMachine\\Root' | Out-Null
      ConvertTo-Json -Compress -InputObject ([pscustomobject]@{ thumbprint = $cert.Thumbprint })`,
      { NAMES: NAMES.join(","), PASSPHRASE: passphrase, PFX: pfx },
    );
    this.#thumbprint = made.thumbprint;
    return { pfx: await readFile(pfx), passphrase };
  }

  /** Points the names at this PC. */
  async redirect() {
    await writeFile(HOSTS, hostsRedirecting(await readFile(HOSTS, "utf8")), "ascii");
    this.#redirected = true;
    await run("ipconfig.exe", ["/flushdns"]);
  }

  /** Asks for an address the way the PC's own programs do (through its certificate store): the status and the text of the answer. */
  fetchAsThePcDoes(url) {
    return json(
      `$answer = Invoke-WebRequest -UseBasicParsing -Uri $env:SCENARIO_URL
      ConvertTo-Json -Compress -InputObject ([pscustomobject]@{ status = [int]$answer.StatusCode; text = [string]$answer.Content })`,
      { URL: url },
    );
  }

  /** Puts the hosts file back and removes the certificate. Safe to call when nothing was done. */
  async undo() {
    if (this.#redirected) {
      await writeFile(HOSTS, hostsRestored(await readFile(HOSTS, "utf8")), "ascii");
      this.#redirected = false;
      await run("ipconfig.exe", ["/flushdns"]);
    }
    if (this.#thumbprint) {
      await json(
        `foreach ($store in 'Root', 'My') { Remove-Item -LiteralPath "Cert:\\LocalMachine\\$store\\$($env:SCENARIO_THUMBPRINT)" -ErrorAction SilentlyContinue }`,
        { THUMBPRINT: this.#thumbprint },
      );
      this.#thumbprint = null;
    }
  }
}
