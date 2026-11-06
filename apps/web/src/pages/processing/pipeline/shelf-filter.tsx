import { useState } from "react";

import { SegmentedControl } from "../../../components/panels/segmented-control";
import {
  foldedChipIndexes,
  useTitleLineFit,
} from "../../../components/shell/title-line-fit";

/** One choice: a workflow, or null for all of them. */
export type ShelfFilterChoice = { id: number | null; name: string };

const ALL = "all";
const valueOf = (choice: ShelfFilterChoice) => String(choice.id ?? ALL);

/**
 * The workflows to narrow the shelf to, as the boxed switch every filter uses. When the header has no room for every
 * option the last ones fold into the switch's "More" menu: options are whole or not there, and none runs under the link
 * beside them.
 */
export function ShelfFilter({
  choices,
  chosen,
  onChoose,
}: {
  choices: readonly ShelfFilterChoice[];
  /** The workflow the shelf is narrowed to; none when it shows them all. */
  chosen: number | null;
  onChoose: (workflowId: number | null) => void;
}) {
  const [row, setRow] = useState<HTMLDivElement | null>(null);
  const { folded } = useTitleLineFit({
    row,
    stages: 0,
    chips: choices.length,
    refit: `${chosen}|${choices.map((choice) => choice.name).join("\u0000")}`,
  });
  const hidden = new Set(
    [
      ...foldedChipIndexes(
        choices.length,
        folded,
        choices.findIndex((choice) => choice.id === chosen),
      ),
    ].map((index) => valueOf(choices[index])),
  );
  return (
    <div ref={setRow} className="mm-shelf-filter">
      <SegmentedControl
        ariaLabel="Workflows"
        value={String(chosen ?? ALL)}
        options={choices.map((choice) => ({
          value: valueOf(choice),
          label: choice.name,
        }))}
        onChange={(value) => onChoose(value === ALL ? null : Number(value))}
        fold={{ hidden, menuLabel: "More workflows" }}
      />
    </div>
  );
}
