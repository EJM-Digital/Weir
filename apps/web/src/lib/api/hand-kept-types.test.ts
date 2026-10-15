import { describe, expectTypeOf, it } from "vitest";

import type { MediaManagerConnection } from "../media-managers/media-managers-api";
import type {
  ProcessingLibrary,
  ProcessingLibraryWrite,
} from "../processing/libraries-api";
import type { ProcessingFile } from "../processing/files-api";
import type {
  ProcessingRuleSet,
  ProcessingRuleSetWrite,
} from "../processing/rule-sets-api";
import type { RequestBody, Schema } from "./types";

// Types written by hand beside the generated ones are tied to the server's schema here: a field the server adds, drops
// or renames makes `npm run build` (which compiles every test) fail until the hand-written type follows.
describe("types kept by hand follow the server's schema", () => {
  it("a workflow has the fields the server reports", () => {
    expectTypeOf<keyof ProcessingLibrary>().toEqualTypeOf<
      keyof Schema<"ProcessingLibraryOut">
    >();
  });

  it("a workflow is saved with the fields the server accepts", () => {
    expectTypeOf<keyof ProcessingLibraryWrite>().toEqualTypeOf<
      keyof RequestBody<"ProcessingLibraryUpdateIn">
    >();
  });

  it("a profile has the fields the server reports and accepts", () => {
    expectTypeOf<keyof ProcessingRuleSet>().toEqualTypeOf<
      keyof Schema<"ProcessingRuleSetOut">
    >();
    expectTypeOf<keyof ProcessingRuleSetWrite>().toEqualTypeOf<
      keyof RequestBody<"ProcessingRuleSetIn">
    >();
  });

  it("a media manager has the fields the server reports", () => {
    expectTypeOf<keyof MediaManagerConnection>().toEqualTypeOf<
      keyof Schema<"MediaManagerConnectionOut">
    >();
  });

  it("a file in the list has the fields the server reports", () => {
    expectTypeOf<keyof ProcessingFile>().toEqualTypeOf<
      keyof Schema<"ProcessingFileOut">
    >();
  });
});
