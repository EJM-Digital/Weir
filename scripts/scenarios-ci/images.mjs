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

/** The environment variable that replaces an image for one run: the weekly drift job tries the newest Deluno with it. */
export const overrideVariable = (name) => `SCENARIO_${name}_IMAGE_OVERRIDE`;

/**
 * The pinned images with any the environment replaces for this run. A replacement is held to the same rule as a pin, a reference with a digest,
 * so a run never starts an image that a tag could change under it.
 */
export function withOverrides(images = loadImages(), env = process.env) {
  return Object.fromEntries(
    Object.entries(images).map(([name, image]) => {
      const replacement = env[overrideVariable(name)];
      if (!replacement) return [name, image];
      if (!DIGEST_REFERENCE.test(replacement)) throw new Error(`${overrideVariable(name)} must be a reference with a sha256 digest, not "${replacement}".`);
      return [name, { ...image, label: `${image.label} (replaced for this run)`, reference: replacement }];
    }),
  );
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
