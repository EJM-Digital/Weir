import type { CSSProperties, ReactNode } from "react";

import { classNames } from "../../../lib/ui/class-names";
import { useElementSize } from "../../../lib/ui/use-element-size";
import {
  GRID_AREAS,
  GRID_COLUMNS,
  gridRows,
  type PageLayout,
} from "./dashboard-layout";

type LiveGridProps = {
  layout: PageLayout;
  children: ReactNode;
};

/**
 * The grid the Live view's parts sit in. Beside the right column it is as tall as the page, and the band, the
 * Pipeline and the lower row are shares of the height it has, measured again whenever it changes size; below
 * that the parts stack and the page scrolls. The cells name their areas in weir-processing-dashboard.css.
 */
export function LiveGrid({ layout, children }: LiveGridProps) {
  const [gridRef, grid] = useElementSize<HTMLDivElement>();
  const beside = layout.sideBySide;
  const style: CSSProperties | undefined = beside
    ? {
        gridTemplateColumns: GRID_COLUMNS,
        gridTemplateRows: gridRows(grid.height).template,
        gridTemplateAreas: GRID_AREAS,
      }
    : undefined;
  return (
    <div
      ref={gridRef}
      className={classNames("mm-dash__grid", beside && "mm-dash__grid--beside")}
      style={style}
      data-testid="dashboard-live"
    >
      {children}
    </div>
  );
}
