// The files in the container under test, as the scenarios ask about them: made, dropped into a watched folder, probed, hashed and dated by
// the container's own tools, as the account the server runs as.
import { shellQuote } from "../docker-smoke/shell.mjs";
import { filmCommand, summarizeProbe } from "./film.mjs";

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

  /** A release folder in a watched folder: a copy of the film named for the release, and an .nfo beside it. */
  async dropRelease({ film, folder, name }) {
    const [source, target, nfo] = [film, `${folder}/${name}.mkv`, `${folder}/${name}.nfo`].map((path) => shellQuote(plain(path)));
    this.#stack.exec(`mkdir -p ${shellQuote(plain(folder))} && cp ${source} ${target} && printf 'Scenario release ${name}\\n' > ${nfo}`);
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
