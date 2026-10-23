// The part of the redirect that changes the PC: the certificate store, the hosts file and the DNS cache. It runs on a hosted Windows runner only,
// so it is listed in docs/coverage/exclusions.json with the windows-* scenario files; the text it writes is made in github-redirect.mjs.
import { randomBytes } from "node:crypto";
import { readFile, writeFile } from "node:fs/promises";
import { join } from "node:path";

import { NAMES, hostsRedirecting, hostsRestored } from "./github-redirect.mjs";
import { json, run } from "./windows-machine.mjs";

const HOSTS = join(process.env.SystemRoot ?? "C:\\Windows", "System32", "drivers", "etc", "hosts");

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
