import { cleanup, fireEvent, screen, within } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

import {
  EMPTY_RULE_SET,
  defaultAudioSortersFor,
  dumpSorters,
} from "./rule-set-model";
import {
  RECORDED_PROFILES,
  STORED,
  expectedSave,
  openProfiles,
  savedBody,
  serveProfiles,
} from "./profiles-test-support";

// The page is taken down before `fetch` is given back, so nothing it still has on its way reaches the network the setup refuses.
afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

function save() {
  fireEvent.click(screen.getByRole("button", { name: "Save profile" }));
}

/** Opens a language picker and picks from its list, not from the plain choices elsewhere on the page. */
function pick(picker: string, ...languages: string[]) {
  fireEvent.click(screen.getByRole("button", { name: picker }));
  const list = within(screen.getByRole("listbox"));
  for (const language of languages) {
    fireEvent.click(list.getByRole("option", { name: language }));
  }
}

/** Opens the tab on a profile like the recorded one, with these rules changed from the start. */
async function openWith(changes: Record<string, unknown> = {}) {
  const stored = { ...STORED, ...changes };
  const api = serveProfiles([stored, ...RECORDED_PROFILES.slice(1)]);
  await openProfiles();
  return { api, stored };
}

function choose(name: string | RegExp, value: string) {
  fireEvent.change(screen.getByRole("combobox", { name }), {
    target: { value },
  });
}

function toggle(name: RegExp) {
  fireEvent.click(screen.getByRole("checkbox", { name }));
}

describe("creating a profile", () => {
  it("sends every rule of a new profile with the name trimmed, then shows it saved and selected", async () => {
    const api = serveProfiles();
    await openProfiles();

    fireEvent.click(screen.getByRole("button", { name: "New profile" }));
    fireEvent.change(screen.getByPlaceholderText("English feature films"), {
      target: { value: "  Anime  " },
    });
    fireEvent.click(screen.getByRole("button", { name: "Create profile" }));

    expect(await savedBody(api, "POST /api/v1/processing/rule-sets")).toEqual({
      ...EMPTY_RULE_SET,
      name: "Anime",
    });
    expect(await screen.findByRole("status")).toHaveTextContent(
      "Anime was saved.",
    );
    expect(screen.getByRole("combobox", { name: "Profile" })).toHaveValue("9");
    expect(screen.getByPlaceholderText("English feature films")).toHaveValue(
      "Anime",
    );
    expect(
      screen.getByRole("button", { name: "Save profile" }),
    ).toBeInTheDocument();
  });

  it("keeps the new profile selected when the list that includes it arrives after the save", async () => {
    const api = serveProfiles(RECORDED_PROFILES, { holdListAfterCreate: true });
    await openProfiles();

    fireEvent.click(screen.getByRole("button", { name: "New profile" }));
    fireEvent.change(screen.getByPlaceholderText("English feature films"), {
      target: { value: "Anime" },
    });
    fireEvent.click(screen.getByRole("button", { name: "Create profile" }));
    expect(await screen.findByRole("status")).toHaveTextContent(
      "Anime was saved.",
    );
    api.letListThrough();

    const profiles = within(screen.getByTestId("rule-set-profile-bar"));
    await profiles.findByRole("option", { name: /Anime/ });
    expect(profiles.getByRole("combobox")).toHaveValue("9");
    expect(screen.getByPlaceholderText("English feature films")).toHaveValue(
      "Anime",
    );
  });

  it("asks for a name and sends nothing while the name is blank", async () => {
    const api = serveProfiles();
    await openProfiles();

    fireEvent.click(screen.getByRole("button", { name: "New profile" }));
    fireEvent.change(screen.getByPlaceholderText("English feature films"), {
      target: { value: "   " },
    });
    fireEvent.click(screen.getByRole("button", { name: "Create profile" }));

    expect(await screen.findByRole("status")).toHaveTextContent(
      "Give this profile a name before saving it.",
    );
    expect(api.to("POST /api/v1/processing/rule-sets")).toHaveLength(0);
  });
});

describe("how audio is chosen", () => {
  it.each(["quality_all_languages", "preferred_langs_strict"])(
    "saves %s with that strategy's own default order in place of the profile's",
    async (mode) => {
      const { api, stored } = await openWith({
        audio_sorters_json: JSON.stringify([
          { field: "bitrate", value: null, reversed: true },
        ]),
      });

      choose("How to choose audio", mode);
      save();

      expect(await savedBody(api)).toEqual(
        expectedSave(
          {
            audio_preference_mode: mode,
            audio_sorters_json: dumpSorters(defaultAudioSortersFor(mode)),
          },
          stored,
        ),
      );
    },
  );

  it("ranks quality across every language without the default flag, and the other strategies with it", () => {
    const fields = (mode: string) =>
      defaultAudioSortersFor(mode).map((row) => row.field);

    expect(fields("quality_all_languages")).not.toContain("default");
    expect(fields("preferred_langs_strict")).toContain("default");
    expect(fields("preferred_langs_quality")).toContain("default");
  });
});

describe("the audio languages", () => {
  it("saves each choice, a regional variant included, and which one is marked default", async () => {
    const { api, stored } = await openWith();

    choose("First choice", "por-BR");
    choose("Second choice", "fre");
    choose("Third choice", "deu");
    choose("Mark as default", "secondary");
    save();

    expect(await savedBody(api)).toEqual(
      expectedSave(
        {
          primary_audio_lang: "por-BR",
          secondary_audio_lang: "fre",
          tertiary_audio_lang: "deu",
          default_audio_slot: "secondary",
        },
        stored,
      ),
    );
  });

  it("clears the second and third choice when None is chosen", async () => {
    const { api, stored } = await openWith({
      secondary_audio_lang: "jpn",
      tertiary_audio_lang: "deu",
    });

    choose("Second choice", "");
    choose("Third choice", "");
    save();

    expect(await savedBody(api)).toEqual(
      expectedSave(
        { secondary_audio_lang: "", tertiary_audio_lang: "" },
        stored,
      ),
    );
  });

  it("keeps a saved language Weir has no name for on the list, selected, and saves it as it was", async () => {
    const { api, stored } = await openWith({
      primary_audio_lang: "tlh",
      tertiary_audio_lang: "xyz",
    });

    const first = screen.getByRole<HTMLSelectElement>("combobox", {
      name: "First choice",
    });
    expect(first).toHaveValue("tlh");
    expect(
      within(first).getByRole("option", { name: "tlh" }),
    ).toBeInTheDocument();
    expect(screen.getByRole("combobox", { name: "Third choice" })).toHaveValue(
      "xyz",
    );
    choose("Mark as default", "tertiary");
    save();

    expect(await savedBody(api)).toEqual(
      expectedSave({ default_audio_slot: "tertiary" }, stored),
    );
  });

  it("groups a language's regional variants under it", async () => {
    await openWith();

    const first = screen.getByRole("combobox", { name: "First choice" });

    const french = within(first).getByRole("group", { name: "French (fre)" });
    expect(
      within(french)
        .getAllByRole("option")
        .map((option) => option.getAttribute("value")),
    ).toEqual(["fre", "fre-CA", "fre-FR", "fre-BE"]);
  });
});

describe("removing commentary tracks", () => {
  it("saves the switch both ways", async () => {
    const { api, stored } = await openWith({ remove_commentary: true });

    toggle(/Remove commentary tracks/);
    save();

    expect(await savedBody(api)).toEqual(
      expectedSave({ remove_commentary: false }, stored),
    );
  });
});

describe("what happens to subtitles", () => {
  it("shows the language list and the flag switches for keep selected languages, only the switches for keep all, and neither for remove all", async () => {
    await openWith({ subtitle_mode: "remove_all" });
    const languages = () =>
      screen.queryByRole("button", { name: "Languages to keep" });
    const forced = () =>
      screen.queryByRole("checkbox", { name: /Keep forced subtitles/ });

    expect(languages()).not.toBeInTheDocument();
    expect(forced()).not.toBeInTheDocument();

    choose("Subtitle handling", "keep_listed");
    expect(languages()).toBeInTheDocument();
    expect(forced()).toBeInTheDocument();

    choose("Subtitle handling", "keep_all");
    expect(languages()).not.toBeInTheDocument();
    expect(forced()).toBeInTheDocument();

    choose("Subtitle handling", "remove_all");
    expect(languages()).not.toBeInTheDocument();
    expect(forced()).not.toBeInTheDocument();
  });

  it.each(["keep_all", "keep_listed", "remove_all"])(
    "saves the handling %s",
    async (mode) => {
      const { api, stored } = await openWith({
        subtitle_mode: mode === "keep_all" ? "remove_all" : "keep_all",
      });

      choose("Subtitle handling", mode);
      save();

      expect(await savedBody(api)).toEqual(
        expectedSave({ subtitle_mode: mode }, stored),
      );
    },
  );

  it("saves the languages picked to keep, in the order they were picked", async () => {
    const { api, stored } = await openWith({
      subtitle_mode: "keep_listed",
      subtitle_langs_csv: "",
    });

    pick("Languages to keep", "French (fre)", "English (eng)");
    save();

    expect(await savedBody(api)).toEqual(
      expectedSave({ subtitle_langs_csv: "fre,eng" }, stored),
    );
  });

  it("keeps a saved language Weir has no name for in the list, where it can be removed", async () => {
    const { api, stored } = await openWith({
      subtitle_mode: "keep_listed",
      subtitle_langs_csv: "eng,zzz",
    });

    expect(
      screen.getByRole("button", { name: "Languages to keep" }),
    ).toHaveTextContent("English (eng), zzz");
    pick("Languages to keep", "zzz");
    save();

    expect(await savedBody(api)).toEqual(
      expectedSave({ subtitle_langs_csv: "eng" }, stored),
    );
  });

  it("saves keeping forced and default subtitles each way", async () => {
    const { api, stored } = await openWith({
      subtitle_mode: "keep_all",
      preserve_forced_subs: true,
      preserve_default_subs: false,
    });

    toggle(/Keep forced subtitles/);
    toggle(/Keep default subtitles/);
    save();

    expect(await savedBody(api)).toEqual(
      expectedSave(
        { preserve_forced_subs: false, preserve_default_subs: true },
        stored,
      ),
    );
  });
});

describe("the original language", () => {
  it("offers its four options only once keeping it is on, and saves each", async () => {
    const { api, stored } = await openWith({
      keep_original_language: false,
      original_language_additional_csv: "",
      original_language_keep_only_first: true,
      original_language_first_if_none: true,
      original_language_treat_empty_as_original: false,
    });
    expect(
      screen.queryByRole("button", { name: "Other languages to keep" }),
    ).not.toBeInTheDocument();

    toggle(/Keep the original language/);
    pick("Other languages to keep", "Japanese (jpn)");
    toggle(/Keep one track per language/);
    toggle(/Use audio preferences if no match is found/);
    toggle(/Treat an untagged track as original/);
    save();

    expect(await savedBody(api)).toEqual(
      expectedSave(
        {
          keep_original_language: true,
          original_language_additional_csv: "jpn",
          original_language_keep_only_first: false,
          original_language_first_if_none: false,
          original_language_treat_empty_as_original: true,
        },
        stored,
      ),
    );
  });
});

describe("the track order", () => {
  const order = (...rows: [string, string | null, boolean][]) =>
    JSON.stringify(
      rows.map(([field, value, reversed]) => ({ field, value, reversed })),
    );

  const rowsOf = (title: string) =>
    within(screen.getByRole("region", { name: title })).getAllByRole(
      "listitem",
    );

  const criteria = (title: string) =>
    rowsOf(title).map(
      (row) => within(row).getByRole<HTMLSelectElement>("combobox").value,
    );

  async function openOrder() {
    const opened = await openWith({
      audio_sorters_json: "",
      subtitle_sorters_json: "",
    });
    fireEvent.click(
      screen.getByRole("button", { name: /Advanced track ordering/ }),
    );
    return opened;
  }

  it("starts from the default order when the profile has none of its own", async () => {
    await openOrder();

    expect(criteria("Audio order")).toEqual([
      "commentary",
      "channels",
      "codec",
      "bitrate",
      "default",
    ]);
    expect(criteria("Subtitle order")).toEqual([
      "forced",
      "default",
      "language",
    ]);
  });

  it("saves an audio order that was added to, changed, reversed, moved and removed from", async () => {
    const { api, stored } = await openOrder();
    const audio = within(screen.getByRole("region", { name: "Audio order" }));

    fireEvent.click(audio.getByRole("button", { name: "Add criterion" }));
    // Changing the criterion gives the row a new key, so the row is looked up again each time.
    const added = () => rowsOf("Audio order")[5]!;
    fireEvent.change(within(added()).getByRole("combobox"), {
      target: { value: "codec" },
    });
    fireEvent.change(within(added()).getByRole("textbox"), {
      target: { value: "dts" },
    });
    fireEvent.click(within(added()).getByRole("checkbox", { name: "Reverse" }));
    fireEvent.click(
      audio.getByRole("button", { name: "Move Audio order criterion 6 up" }),
    );
    fireEvent.click(
      audio.getByRole("button", { name: "Move Audio order criterion 2 down" }),
    );
    fireEvent.click(
      within(rowsOf("Audio order")[0]!).getByRole("button", {
        name: "Remove",
      }),
    );
    save();

    expect(await savedBody(api)).toEqual(
      expectedSave(
        {
          audio_sorters_json: order(
            ["codec", null, false],
            ["channels", null, false],
            ["bitrate", null, false],
            ["codec", "dts", true],
            ["default", null, false],
          ),
        },
        stored,
      ),
    );
  });

  it("saves a subtitle order the same way, apart from the audio order", async () => {
    const { api, stored } = await openOrder();
    const subtitles = within(
      screen.getByRole("region", { name: "Subtitle order" }),
    );

    fireEvent.click(
      within(rowsOf("Subtitle order")[0]!).getByRole("checkbox", {
        name: "Reverse",
      }),
    );
    fireEvent.change(
      within(rowsOf("Subtitle order")[2]!).getByRole("textbox"),
      {
        target: { value: "eng" },
      },
    );
    fireEvent.click(
      within(rowsOf("Subtitle order")[1]!).getByRole("button", {
        name: "Remove",
      }),
    );
    fireEvent.click(subtitles.getByRole("button", { name: "Add criterion" }));
    save();

    expect(await savedBody(api)).toEqual(
      expectedSave(
        {
          subtitle_sorters_json: order(
            ["forced", null, true],
            ["language", "eng", false],
            ["language", null, false],
          ),
        },
        stored,
      ),
    );
  });

  it("keeps focus on the criterion being changed, so the next key press still reaches it", async () => {
    await openOrder();
    const criterion = within(rowsOf("Audio order")[1]!).getByRole("combobox");
    criterion.focus();

    fireEvent.change(criterion, { target: { value: "codec" } });

    const same = within(rowsOf("Audio order")[1]!).getByRole("combobox");
    expect(same).toHaveValue("codec");
    expect(same).toHaveFocus();
  });

  it("cannot move the first criterion up or the last one down", async () => {
    await openOrder();

    for (const title of ["Audio order", "Subtitle order"]) {
      const region = within(screen.getByRole("region", { name: title }));
      const last = rowsOf(title).length;
      expect(
        region.getByRole("button", { name: `Move ${title} criterion 1 up` }),
      ).toBeDisabled();
      expect(
        region.getByRole("button", {
          name: `Move ${title} criterion ${last} down`,
        }),
      ).toBeDisabled();
      expect(
        region.getByRole("button", { name: `Move ${title} criterion 2 up` }),
      ).toBeEnabled();
    }
  });
});
