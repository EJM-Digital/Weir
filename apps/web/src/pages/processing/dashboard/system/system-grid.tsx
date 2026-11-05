import type { CSSProperties, ReactNode } from "react";

import { classNames } from "../../../../lib/ui/class-names";
import { useElementSize } from "../../../../lib/ui/use-element-size";
import { useStillWhileResizing } from "../../pipeline/use-still-while-resizing";
import {
  GRID_AREAS_SYSTEM,
  GRID_COLUMNS,
  gridRows,
  type PageLayout,
} from "../dashboard-layout";

type SystemGridProps = {
  layout: PageLayout;
  children: ReactNode;
};

/**
 * The grid the System view's cards sit in, on Live's columns and rows: beside the right column it is as tall as the
 * page, and its three rows are shares of the height it has, measured again whenever it changes size and divided by
 * the rule Live uses, so the two views' rows are always the same; below that the cards stack and the page scrolls. The cells name their areas in weir-system-cards.css. Nothing moves while the
 * window is being resized.
 */
export function SystemGrid({ layout, children }: SystemGridProps) {
  useStillWhileResizing();
  const [gridRef, grid] = useElementSize<HTMLDivElement>();
  const beside = layout.sideBySide;
  const style: CSSProperties | undefined = beside
    ? {
        gridTemplateColumns: GRID_COLUMNS,
        gridTemplateRows: gridRows(grid.height).template,
        gridTemplateAreas: GRID_AREAS_SYSTEM,
      }
    : undefined;
  return (
    <div
      ref={gridRef}
      className={classNames("mm-sy-grid", beside && "mm-sy-grid--beside")}
      style={style}
      data-testid="dashboard-system"
    >
      {children}
    </div>
  );
}
