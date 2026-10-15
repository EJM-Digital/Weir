// node --test scripts/docker-entrypoint.test.mjs
// Runs docker/entrypoint.sh as the ordinary user these tests run as, against a stand-in server that reports what it was started with.
// The branches that need root (remapping the weir user, fixing the owner of WEIR_HOME) run in the image: scripts/docker-compose-smoke.mjs.
import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, statSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { test } from "node:test";

const entrypoint = readFileSync(join(dirname(fileURLToPath(import.meta.url)), "..", "docker", "entrypoint.sh"), "utf8");
const hasShell = spawnSync("sh", ["-c", "umask"]).status === 0;
const skip = hasShell ? false : "no POSIX shell on this machine";
// The start under test is the one that is not root; file modes are only kept on a POSIX file system.
const skipRoot = skip || (process.getuid?.() === 0 ? "running as root" : false);
const skipModes = skipRoot || (process.platform === "win32" ? "this file system keeps no POSIX file modes" : false);

const posix = (path) => path.replaceAll("\\", "/");
// A secret the entrypoint makes: 48 random bytes, base64url without padding.
const GENERATED_SECRET = /^[A-Za-z0-9_-]{64}$/;

// What the entrypoint reads from its environment; none of it may leak in from the machine running the tests.
const entrypointSettings = (name) => name.startsWith("WEIR_") || name === "PUID" || name === "PGID" || name === "PORT";

// Runs the entrypoint with a stand-in server that reports, one per line, the umask, session secret, credentials secret and
// arguments it was started with. A value of undefined in `env` leaves that variable unset.
function runEntrypoint(home, env) {
  const root = mkdtempSync(join(tmpdir(), "weir-entrypoint-"));
  try {
    const app = join(root, "app");
    mkdirSync(app);
    writeFileSync(join(app, "Weir"), '#!/bin/sh\nprintf "%s\\n%s\\n%s\\n%s\\n" "$(umask)" "$WEIR_SESSION_SECRET" "$WEIR_CREDENTIALS_SECRET" "$*"\n', { mode: 0o755 });
    const script = join(root, "entrypoint.sh");
    writeFileSync(script, entrypoint.replaceAll("/opt/weir", posix(app)));
    const environment = Object.fromEntries(Object.entries(process.env).filter(([name]) => !entrypointSettings(name)));
    Object.assign(environment, { WEIR_HOME: posix(home), WEIR_PUID: "1000", WEIR_PGID: "1000" }, env);
    for (const [name, value] of Object.entries(environment)) if (value === undefined) delete environment[name];
    const run = spawnSync("sh", [posix(script)], { env: environment, encoding: "utf8" });
    const [umask, session, credentials, args] = run.stdout.split("\n");
    return { status: run.status, stderr: run.stderr, umask, session, credentials, args, shellUmask: spawnSync("sh", ["-c", "umask"], { encoding: "utf8" }).stdout.trim() };
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
}

function startServer(home, env) {
  const started = runEntrypoint(home, env);
  assert.equal(started.status, 0, started.stderr);
  return started;
}

/** Runs `body` with a data folder that exists only for it. */
function withHome(body) {
  const home = mkdtempSync(join(tmpdir(), "weir-home-"));
  try {
    return body(home);
  } finally {
    rmSync(home, { recursive: true, force: true });
  }
}

function refuses(env, message) {
  withHome((home) => {
    const run = runEntrypoint(home, env);
    assert.equal(run.status, 1, `expected the start to be refused: ${run.stderr}`);
    assert.match(run.stderr, message);
    assert.equal(run.args, undefined, "the server was started although the start was refused");
  });
}

for (const [name, env] of [
  ["a start that makes both secrets", {}],
  ["a start that supplies the session secret", { WEIR_SESSION_SECRET: "s".repeat(40) }],
]) {
  test(`the server keeps the caller's umask on ${name}`, { skip }, () => {
    withHome((home) => {
      const { umask, credentials, shellUmask } = startServer(home, env);
      assert.equal(umask, shellUmask, "owner-only secrets must not leave the server with a private umask");
      assert.ok(credentials.length >= 32, "the credentials secret reaches the server");
    });
  });
}

test("the credentials secret made on the first start is the one used on the next", { skip }, () => {
  withHome((home) => {
    const first = startServer(home, {});
    const second = startServer(home, {});
    assert.equal(second.credentials, first.credentials);
    assert.equal(readFileSync(join(home, "credentials.secret"), "utf8"), `${first.credentials}\n`);
  });
});

test("a credentials secret from the environment wins and no file is made", { skip }, () => {
  withHome((home) => {
    const { credentials } = startServer(home, { WEIR_CREDENTIALS_SECRET: "c".repeat(40) });
    assert.equal(credentials, "c".repeat(40));
    assert.equal(existsSync(join(home, "credentials.secret")), false);
  });
});

test("the session secret made on the first start is 64 URL-safe characters, kept private, and different from the credentials secret", { skip }, () => {
  withHome((home) => {
    const { session, credentials, stderr } = startServer(home, {});
    assert.match(session, GENERATED_SECRET);
    assert.match(credentials, GENERATED_SECRET);
    assert.notEqual(session, credentials);
    assert.equal(readFileSync(join(home, "session.secret"), "utf8"), `${session}\n`);
    assert.match(stderr, /generated WEIR_SESSION_SECRET at .*session\.secret/);
    assert.match(stderr, /generated WEIR_CREDENTIALS_SECRET at .*credentials\.secret/);
  });
});

test("the session secret made on the first start is the one used on the next, and is not made again", { skip }, () => {
  withHome((home) => {
    const first = startServer(home, {});
    const second = startServer(home, {});
    assert.equal(second.session, first.session);
    assert.equal(second.credentials, first.credentials);
    assert.doesNotMatch(second.stderr, /generated WEIR_/);
  });
});

test("both secret files are readable by their owner alone", { skip: skipModes }, () => {
  withHome((home) => {
    startServer(home, {});
    for (const file of ["session.secret", "credentials.secret"]) {
      assert.equal(statSync(join(home, file)).mode & 0o777, 0o600, `${file} must not be readable by anyone else`);
    }
  });
});

test("a session secret from the environment wins and no file is made", { skip }, () => {
  withHome((home) => {
    const { session } = startServer(home, { WEIR_SESSION_SECRET: "s".repeat(40) });
    assert.equal(session, "s".repeat(40));
    assert.equal(existsSync(join(home, "session.secret")), false);
  });
});

test("a session secret shorter than 32 characters refuses the start", { skip }, () => {
  refuses({ WEIR_SESSION_SECRET: "s".repeat(31) }, /WEIR_SESSION_SECRET must be at least 32 characters/);
});

test("a session secret of exactly 32 characters is accepted", { skip }, () => {
  withHome((home) => assert.equal(startServer(home, { WEIR_SESSION_SECRET: "s".repeat(32) }).session, "s".repeat(32)));
});

test("a short session secret left in the data folder refuses the start", { skip }, () => {
  withHome((home) => {
    writeFileSync(join(home, "session.secret"), "too-short\n");
    const run = runEntrypoint(home, {});
    assert.equal(run.status, 1);
    assert.match(run.stderr, /WEIR_SESSION_SECRET must be at least 32 characters/);
  });
});

test("the data folder is made when it does not exist", { skip }, () => {
  withHome((home) => {
    const nested = join(home, "not", "yet");
    startServer(nested, {});
    assert.ok(existsSync(join(nested, "session.secret")));
  });
});

test("the server is started on port 9347, or on the port PORT names", { skip }, () => {
  withHome((home) => {
    assert.equal(startServer(home, {}).args, "--port 9347");
    assert.equal(startServer(home, { PORT: "9000" }).args, "--port 9000");
  });
});

for (const name of ["WEIR_PUID", "WEIR_PGID"]) {
  test(`${name} of 0 refuses the start, as the server must not run as root`, { skip }, () => {
    refuses({ [name]: "0" }, new RegExp(`${name} must not be 0: Weir refuses to run its server as root`));
  });

  for (const value of ["abc", "-5", "1.5", "10x", " 1000"]) {
    test(`${name} of "${value}" refuses the start`, { skip }, () => {
      refuses({ [name]: value }, new RegExp(`${name} must be a non-negative integer`));
    });
  }
}

for (const [legacy, name] of [["PUID", "WEIR_PUID"], ["PGID", "WEIR_PGID"]]) {
  test(`${legacy} is read when ${name} is not set, and is refused at 0 too`, { skip }, () => {
    refuses({ [name]: undefined, [legacy]: "0" }, new RegExp(`${name} must not be 0`));
  });

  test(`${name} wins over ${legacy}`, { skip }, () => {
    withHome((home) => startServer(home, { [name]: "1000", [legacy]: "0" }));
  });
}

test("a uid and gid other than 0 are accepted, and a start that is not root does not apply them", { skip: skipRoot }, () => {
  withHome((home) => {
    const { stderr } = startServer(home, { WEIR_PUID: "2001", WEIR_PGID: "2002" });
    assert.doesNotMatch(stderr, /Updating weir/);
  });
});

for (const name of ["WEIR_CHOWN_WATCHED", "WEIR_CHOWN_TEMP", "WEIR_CHOWN_OUTPUT"]) {
  for (const value of ["maybe", "2", "tru", "y"]) {
    test(`${name} of "${value}" refuses the start`, { skip }, () => {
      refuses({ [name]: value }, new RegExp(`${name} must be one of: true/false, 1/0, yes/no, on/off`));
    });
  }

  for (const value of ["true", "FALSE", "1", "0", "Yes", "no", "ON", "off"]) {
    test(`${name} of "${value}" is accepted`, { skip }, () => {
      withHome((home) => startServer(home, { [name]: value }));
    });
  }
}

for (const name of ["WEIR_DIR_MODE_WATCHED", "WEIR_DIR_MODE_TEMP", "WEIR_DIR_MODE_OUTPUT"]) {
  for (const value of ["999", "77", "77777", "7x5", "rwx", "-775"]) {
    test(`${name} of "${value}" refuses the start`, { skip }, () => {
      refuses({ [name]: value }, new RegExp(`${name} must be an octal directory mode such as 775 or 2775`));
    });
  }

  for (const value of ["775", "2775", "0755", "700"]) {
    test(`${name} of "${value}" is accepted`, { skip }, () => {
      withHome((home) => startServer(home, { [name]: value }));
    });
  }
}

const UNAPPLIED = [
  ["WEIR_CHOWN_WATCHED", "true"],
  ["WEIR_CHOWN_TEMP", "yes"],
  ["WEIR_DIR_MODE_WATCHED", "775"],
  ["WEIR_DIR_MODE_TEMP", "2775"],
];

for (const [name, value] of UNAPPLIED) {
  test(`${name} set to ${value} starts and says it is ignored`, { skip }, () => {
    withHome((home) => {
      const { stderr } = startServer(home, { [name]: value });
      assert.match(stderr, new RegExp(`${name} is set, but this image does not apply it to your watched or work folders\\. Ignoring it`));
      const others = UNAPPLIED.map(([other]) => other).filter((other) => other !== name);
      for (const other of others) assert.doesNotMatch(stderr, new RegExp(`${other} is set`), `${other} was not set but is reported`);
    });
  });
}

test("the folder settings that are off, or not set, are not reported", { skip }, () => {
  withHome((home) => {
    const { stderr } = startServer(home, { WEIR_CHOWN_WATCHED: "false", WEIR_CHOWN_TEMP: "0", WEIR_DIR_MODE_WATCHED: "", WEIR_DIR_MODE_TEMP: undefined });
    assert.doesNotMatch(stderr, /Ignoring/);
  });
});

test("the output settings are applied by the server, so the start does not say they are ignored", { skip }, () => {
  withHome((home) => {
    const { stderr } = startServer(home, { WEIR_CHOWN_OUTPUT: "true", WEIR_DIR_MODE_OUTPUT: "2775", WEIR_FILE_MODE_OUTPUT: "664" });
    assert.doesNotMatch(stderr, /Ignoring/);
  });
});
