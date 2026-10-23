// The folder a repository check reads: the checkout the script sits in, or the one named by --root <folder>, which is how
// the tests show a check a repository that breaks its rule.
import path from "node:path";
import { fileURLToPath } from "node:url";

/** The root for the script at `scriptUrl` (a script in scripts/), from `argv` (the arguments after the script's name); `flag` is the option that names another. */
export function rootOf(scriptUrl, argv = process.argv.slice(2), flag = "--root") {
  const at = argv.indexOf(flag);
  return at >= 0 ? path.resolve(argv[at + 1]) : path.resolve(path.dirname(fileURLToPath(scriptUrl)), "..");
}
