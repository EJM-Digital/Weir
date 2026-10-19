// The files in the container under test, as the scenarios ask about them: made, dropped into a watched folder, probed, hashed and dated by
// the container's own tools, as the account the server runs as.
import { shellQuote } from "../docker-smoke/shell.mjs";
import { audioOnlyCommand, filmCommand, summarizeProbe } from "./film.mjs";

const PLAIN_PATH = /^[A-Za-z0-9_./-]+$/;

function plain(path) {
  if (!PLAIN_PATH.test(path)) throw new Error(`${JSON.stringify(path)} is not a plain path (letters, digits, dots, dashes, underscores and slashes).`);
  return path;
}

export class ContainerBox {
  #stack;

  /** @param {{exec(command: string): string, execResult(command: string): {status: number|null, stdout: string, output: string}}} stack */
  constructor(stack) {
    this.#stack = stack;
  }

  async ensureFolders(...paths) {
    this.#stack.exec(`mkdir -p ${paths.map((path) => shellQuote(plain(path))).join(" ")}`);
  }

  async makeFilm(options) {
    plain(options.path);
    this.#stack.exec(filmCommand(options));
  }

  /** A file with sound and no picture, large enough to pass a size minimum. */
  async makeAudioOnly(options) {
    plain(options.path);
    this.#stack.exec(audioOnlyCommand(options));
  }

  /** A release folder in a watched folder: a copy of the film named for the release, and an .nfo beside it. */
  async dropRelease({ film, folder, name }) {
    const [source, target, nfo] = [film, `${folder}/${name}.mkv`, `${folder}/${name}.nfo`].map((path) => shellQuote(plain(path)));
    this.#stack.exec(`mkdir -p ${shellQuote(plain(folder))} && cp ${source} ${target} && printf 'Scenario release ${name}\\n' > ${nfo}`);
  }

  /** A file written whole or not at all, as a program that hands a file to a watcher writes it: to a neighbour, then renamed into place. */
  async writeFileAtomically(path, text) {
    const [target, partial] = [path, `${path}.partial`].map((name) => shellQuote(plain(name)));
    this.#stack.exec(`printf %s ${shellQuote(text)} > ${partial} && mv ${partial} ${target}`);
  }

  /** A file holding exactly these bytes. */
  async writeBytes(path, bytes) {
    const octal = bytes.map((byte) => `\\${byte.toString(8).padStart(3, "0")}`).join("");
    this.#stack.exec(`printf '${octal}' > ${shellQuote(plain(path))}`);
  }

  async remove(path) {
    this.#stack.exec(`rm -f ${shellQuote(plain(path))}`);
  }

  /** A folder and everything in it, as a person deletes a download from its folder. */
  async removeTree(path) {
    this.#stack.exec(`rm -rf ${shellQuote(plain(path))}`);
  }

  /** How many bytes the file holds. */
  async sizeOf(path) {
    const result = this.#stack.execResult(`stat -c %s ${plain(path)}`);
    if (result.status !== 0) throw new Error(`stat could not read ${path}: ${result.output.trim()}`);
    return Number(result.stdout.trim());
  }

  /** The paths below a folder that have this file name, or none when the folder is not there. */
  async findNamed(folder, name) {
    const result = this.#stack.execResult(`find ${plain(folder)} -type f -name ${shellQuote(name)}`);
    return result.stdout.split("\n").filter(Boolean);
  }

  async probe(path) {
    const result = this.#stack.execResult(`ffprobe -v error -print_format json -show_streams -show_format ${plain(path)}`);
    if (result.status !== 0) throw new Error(`ffprobe could not read ${path}: ${result.output.trim()}`);
    return summarizeProbe(JSON.parse(result.stdout));
  }

  async exists(path) {
    return this.#stack.execResult(`test -f ${plain(path)}`).status === 0;
  }

  async sha256(path) {
    const result = this.#stack.execResult(`sha256sum ${plain(path)}`);
    if (result.status !== 0) throw new Error(`sha256sum could not read ${path}: ${result.output.trim()}`);
    return result.stdout.split(/\s/)[0];
  }

  /** When the file was last written, to the nanosecond, as text to compare. */
  async modified(path) {
    const result = this.#stack.execResult(`stat -c %y ${plain(path)}`);
    if (result.status !== 0) throw new Error(`stat could not read ${path}: ${result.output.trim()}`);
    return result.stdout.trim();
  }
}
