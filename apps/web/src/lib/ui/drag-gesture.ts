import {
  clamp,
  edgeScrollStep,
  moved,
  sameOrder,
  scrollParentOf,
  scrollPosition,
  sizeOf,
  slotIndex,
  startOf,
  visibleSpan,
  type DragAxis,
} from "./drag-geometry";
import {
  SETTLE_MS,
  clearMotion,
  markHeld,
  motionWanted,
  placeAt,
  slideHome,
  swallowNextClick,
} from "./drag-motion";

/** What the document carries while something is held, so the pointer reads as holding it and no text is selected. */
export const DRAGGING_CLASS = "mm-dragging";

const ESCAPE_KEY = "Escape";

/** How a gesture ended. */
export type DragOutcome<Id> =
  /** The press was released before it moved far enough to be a drag. */
  | { kind: "click" }
  /** Escape, or the browser took the pointer away: everything goes back. */
  | { kind: "cancel" }
  /** Dropped where it started. */
  | { kind: "unchanged" }
  | { kind: "drop"; order: Id[] };

/** What a gesture asks of the list it reorders. */
export type DragHost<Id> = {
  axis: DragAxis;
  /** Pixels the pointer travels before a press becomes a drag; zero picks up at once. */
  threshold: number;
  /** The saved order. */
  ids: () => readonly Id[];
  /** The order on screen, which is the saved one until something is held. */
  order: () => readonly Id[];
  /** The elements that make up one item: a row, or every cell of a column. The first is the one measured. */
  elementsOf: (id: Id) => readonly HTMLElement[];
  /** The held item has passed another: show this order, with the others sliding to their places. */
  reorder: (next: Id[]) => void;
  /** The press became a drag. */
  began: (id: Id) => void;
  ended: (outcome: DragOutcome<Id>) => void;
};

type Slot = { start: number; size: number };

/**
 * One press on a grip, from the pointer going down to it coming up. Once it has moved far enough the held item follows
 * the pointer exactly along the axis, within the list, while the others slide out of its way as it passes their middles;
 * near the edge of the screen the page scrolls. Letting go settles the item into its place before `ended` is called, and
 * Escape puts everything back.
 */
export class DragGesture<Id> {
  private readonly stop = new AbortController();
  private readonly pressedAt: number;
  private pointer: number;
  private active = false;
  private settling = false;
  private scroller: Element | null = null;
  private slots = new Map<Id, Slot>();
  private span: readonly [number, number] = [0, 0];
  private grabOffset = 0;
  private size = 0;
  private translate = 0;
  private frame = 0;
  private timer = 0;

  constructor(
    private readonly host: DragHost<Id>,
    private readonly id: Id,
    pointerPosition: { clientX: number; clientY: number },
  ) {
    this.pointer = this.along(pointerPosition);
    this.pressedAt = this.pointer;
    const { signal } = this.stop;
    window.addEventListener("pointermove", this.onMove, { signal });
    window.addEventListener("pointerup", this.onUp, { signal });
    window.addEventListener("pointercancel", this.cancel, { signal });
    window.addEventListener("keydown", this.onKey, { signal });
    window.addEventListener("scroll", this.follow, { signal, capture: true });
    if (host.threshold <= 0) this.begin();
  }

  /** The held item's elements are measured again after the list has redrawn in its new order. */
  afterLayout(): void {
    this.follow();
  }

  /** Lets go of everything the gesture holds, for a list that goes away mid-drag. */
  dispose(): void {
    this.stop.abort();
    cancelAnimationFrame(this.frame);
    window.clearTimeout(this.timer);
    this.release();
  }

  private along(position: { clientX: number; clientY: number }): number {
    return this.host.axis === "y" ? position.clientY : position.clientX;
  }

  private heldElements(): readonly HTMLElement[] {
    return this.host.elementsOf(this.id);
  }

  private begin(): void {
    const leader = this.heldElements()[0];
    if (!leader) return;
    const { axis } = this.host;
    const scroller = scrollParentOf(leader, axis);
    this.scroller = scroller;
    const position = scrollPosition(scroller, axis);
    const measure = (element: HTMLElement): Slot => {
      const box = element.getBoundingClientRect();
      return { start: startOf(box, axis) + position, size: sizeOf(box, axis) };
    };
    this.slots = new Map();
    for (const id of this.host.order()) {
      const element = this.host.elementsOf(id)[0];
      if (element) this.slots.set(id, measure(element));
    }
    const slots = [...this.slots.values()];
    this.span = [
      Math.min(...slots.map((slot) => slot.start)),
      Math.max(...slots.map((slot) => slot.start + slot.size)),
    ];
    const own = measure(leader);
    this.size = own.size;
    this.grabOffset = this.pointer - (own.start - position);
    this.active = true;
    markHeld(this.heldElements(), true);
    document.documentElement.classList.add(DRAGGING_CLASS);
    this.host.began(this.id);
    this.frame = requestAnimationFrame(() => this.scrollLoop(scroller));
  }

  private onMove = (event: PointerEvent): void => {
    this.pointer = this.along(event);
    if (!this.active) {
      if (Math.abs(this.pointer - this.pressedAt) < this.host.threshold) return;
      this.begin();
    }
    this.follow();
  };

  /** Puts the held item under the pointer, and the list in the order that gives it. */
  private follow = (): void => {
    const leader = this.heldElements()[0];
    if (!this.active || this.settling || !this.scroller || !leader) return;
    const { axis } = this.host;
    const position = scrollPosition(this.scroller, axis);
    const { size } = this;
    const wanted = this.pointer - this.grabOffset + position;
    const heldStart = clamp(wanted, this.span[0], this.span[1] - size);
    const layoutStart =
      startOf(leader.getBoundingClientRect(), axis) + position - this.translate;
    this.translate = heldStart - layoutStart;
    placeAt(this.heldElements(), axis, this.translate);

    const order = this.host.order();
    const centres = order
      .filter((other) => other !== this.id)
      .map((other) => {
        const slot = this.slots.get(other);
        return slot ? slot.start + slot.size / 2 : Infinity;
      });
    // The place is decided by where the pointer wants the item, not where the list lets it be seen, so the first and last
    // places can be reached.
    const index = slotIndex(centres, wanted + size / 2);
    if (index !== order.indexOf(this.id)) {
      this.host.reorder(moved(order, this.id, index));
    }
  };

  /** Each frame while held: scrolls towards the edge the pointer is near. Every way the gesture ends cancels the next frame. */
  private scrollLoop(scroller: Element): void {
    const { axis } = this.host;
    const [viewStart, viewEnd] = visibleSpan(scroller, axis);
    const step = edgeScrollStep(this.pointer, viewStart, viewEnd);
    if (step !== 0) {
      scroller.scrollBy(axis === "y" ? { top: step } : { left: step });
      this.follow();
    }
    this.frame = requestAnimationFrame(() => this.scrollLoop(scroller));
  }

  private onKey = (event: KeyboardEvent): void => {
    if (event.key === ESCAPE_KEY) this.cancel();
  };

  private onUp = (): void => {
    this.stop.abort();
    cancelAnimationFrame(this.frame);
    if (!this.active) {
      this.host.ended({ kind: "click" });
      return;
    }
    swallowNextClick();
    this.settling = true;
    slideHome(this.heldElements(), this.host.axis, this.translate, SETTLE_MS);
    if (motionWanted()) {
      this.timer = window.setTimeout(this.settled, SETTLE_MS);
    } else {
      this.settled();
    }
  };

  private settled = (): void => {
    this.release();
    const order = [...this.host.order()];
    this.host.ended(
      sameOrder(order, this.host.ids())
        ? { kind: "unchanged" }
        : { kind: "drop", order },
    );
  };

  /** Escape, or the browser taking the pointer: the list is told, and slides everything back. */
  private cancel = (): void => {
    this.stop.abort();
    cancelAnimationFrame(this.frame);
    const wasHeld = this.active;
    markHeld(this.heldElements(), false);
    document.documentElement.classList.remove(DRAGGING_CLASS);
    this.active = false;
    this.host.ended(wasHeld ? { kind: "cancel" } : { kind: "click" });
  };

  private release(): void {
    const elements = this.heldElements();
    clearMotion(elements);
    markHeld(elements, false);
    document.documentElement.classList.remove(DRAGGING_CLASS);
    this.active = false;
  }
}
