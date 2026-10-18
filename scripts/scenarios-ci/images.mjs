// The images the scenarios start besides Weir's own build, pinned by digest in images.json and nowhere else.
import { readFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const DIGEST_REFERENCE = /^[a-z0-9][a-z0-9./-]*@sha256:[0-9a-f]{64}$/;

export function loadImages() {
  return JSON.parse(readFileSync(join(dirname(fileURLToPath(import.meta.url)), "images.json"), "utf8")).images;
}

/** The Compose variable that carries an image's reference. */
export const imageVariable = (name) => `SCENARIO_${name}_IMAGE`;

/** The variables Compose reads the pinned references from. */
export function imageEnvironment(images = loadImages()) {
  return Object.fromEntries(Object.entries(images).map(([name, image]) => [imageVariable(name), image.reference]));
}

/** What is wrong with the pins, in words; nothing when every image is named by digest and has a label. */
export function problemsWithImages(images) {
  const problems = [];
  for (const [name, image] of Object.entries(images)) {
    if (!/^[A-Z][A-Z0-9_]*$/.test(name)) problems.push(`${name}: the name must be capital letters, digits and underscores, for the Compose variable.`);
    if (!DIGEST_REFERENCE.test(image.reference ?? "")) problems.push(`${name}: "${image.reference}" is not a reference with a sha256 digest.`);
    if (!image.label || image.label.trim().length < 3) problems.push(`${name}: it needs a label saying what was pinned.`);
  }
  return problems;
}
