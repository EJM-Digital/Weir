// node --test scripts/sweep-repository-owner.test.mjs
import assert from "node:assert/strict";
import { test } from "node:test";

import { addFormerOwner, sweepPlace, sweepText } from "./sweep-repository-owner.mjs";

const FROM = "old-owner/Weir";
const TO = "New-Org/Weir";

test("an address, an image, a docs site and the sibling project move; the image and the site go to lower case", () => {
  const text = [
    "Releases: https://github.com/old-owner/Weir/releases",
    "docker pull ghcr.io/old-owner/weir:1.0.0",
    "Docs: https://old-owner.github.io/Weir/docs/quickstart",
    "gh run list --repo Old-Owner/Weir",
    "Deluno lives at https://github.com/old-owner/Deluno",
  ].join("\n");

  assert.equal(
    sweepText(text, FROM, TO),
    [
      "Releases: https://github.com/New-Org/Weir/releases",
      "docker pull ghcr.io/new-org/weir:1.0.0",
      "Docs: https://new-org.github.io/Weir/docs/quickstart",
      "gh run list --repo New-Org/Weir",
      "Deluno lives at https://github.com/New-Org/Deluno",
    ].join("\n"),
  );
});

test("the docs site's configuration moves", () => {
  assert.equal(
    sweepText("  url: 'https://old-owner.github.io',\n  organizationName: 'old-owner',", FROM, TO),
    "  url: 'https://new-org.github.io',\n  organizationName: 'New-Org',",
  );
});

test("the copyright holder and a sponsorship link stay with the person", () => {
  const person = "Copyright (C) 2026 old-owner and the Weir contributors\n# https://github.com/sponsors/old-owner";
  assert.equal(sweepText(person, FROM, TO), person);
});

test("a repository renamed within its owner changes only the name", () => {
  assert.equal(sweepText("https://github.com/old-owner/Weir/issues", FROM, "old-owner/Weir2"), "https://github.com/old-owner/Weir2/issues");
});

test("each kind of place is rewritten", () => {
  assert.equal(
    sweepPlace('public const string Owner = "old-owner";\npublic const string Repo = "Weir";', FROM, TO),
    'public const string Owner = "New-Org";\npublic const string Repo = "Weir";',
  );
  assert.equal(sweepPlace('export const REPOSITORY = "old-owner/Weir";', FROM, TO), 'export const REPOSITORY = "New-Org/Weir";');
  assert.equal(sweepPlace("$WeirRepository = 'old-owner/Weir'", FROM, TO), "$WeirRepository = 'New-Org/Weir'");
  assert.equal(sweepPlace("$RepositoryUrl = 'https://github.com/old-owner/Weir'", FROM, TO), "$RepositoryUrl = 'https://github.com/New-Org/Weir'");
});

test("the old owner is added to the former owners once, and not when it is still the owner", () => {
  const empty = "export const FORMER_OWNERS = [];";
  const once = addFormerOwner(empty, "old-owner", "New-Org");
  assert.equal(once, 'export const FORMER_OWNERS = ["old-owner"];');
  assert.equal(addFormerOwner(once, "old-owner", "New-Org"), once);
  assert.equal(addFormerOwner(once, "second-owner", "Third-Org"), 'export const FORMER_OWNERS = ["old-owner", "second-owner"];');
  assert.equal(addFormerOwner(empty, "old-owner", "Old-Owner"), empty);
});
