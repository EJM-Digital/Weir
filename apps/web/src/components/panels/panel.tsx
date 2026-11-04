import { useId, type ReactNode } from "react";
import { Link } from "react-router-dom";

type PanelProps = {
  title: string;
  /** A quiet line after the title. It truncates before anything else when the panel is narrow. */
  count?: ReactNode;
  /** A quiet line under the title, inside the header, which is then taller than the 48px of a header without one. */
  note?: ReactNode;
  /**
   * Controls that take the count's place between the title and the link, such as a row of chips. They scroll
   * sideways rather than wrap when the header is narrow.
   */
  controls?: ReactNode;
  /** What the panel adds up to, for a screen reader only: the panel's accessible description. */
  description?: string;
  /** Where the panel's subject is managed. Without it the header has no link. */
  to?: string;
  /** The link's words, named with the panel's title for a screen reader: "Activity: Just finished". */
  toLabel?: string;
  /** What the link shows when that is not its name, such as "3 more" for a link that leads to the rest. */
  toText?: string;
  /** Shows the link as an arrow alone, so a long count keeps the room. The words are still said. */
  iconOnly?: boolean;
  /** Controls at the header's right edge, after the count: a text action, a switch. */
  aside?: ReactNode;
  /** The title's heading level. A panel inside a page's own section is a level 3. */
  headingLevel?: 2 | 3;
  /** Gives the title an id the page can point at. */
  headingId?: string;
  /**
   * A panel of fields or rows: its body is padded, divided from the header by a hairline, and lets a
   * menu that opens inside it reach past the edge.
   */
  padded?: boolean;
  id?: string;
  /** -1 lets the page move focus to the panel, such as when a link elsewhere points at it. */
  tabIndex?: -1;
  dataTestId?: string;
  className?: string;
  bodyClassName?: string;
  children: ReactNode;
};

/**
 * The card every part of a page sits in: a title, a quiet count that gives way before anything
 * else, an arrow link to where the subject is managed, and a body that takes the rest of the height.
 * The header never wraps onto a second line.
 */
export function Panel({
  title,
  count,
  note,
  controls,
  description,
  to,
  toLabel,
  toText,
  iconOnly = false,
  aside,
  headingLevel = 2,
  headingId,
  padded = false,
  id,
  tabIndex,
  dataTestId,
  className,
  bodyClassName,
  children,
}: PanelProps) {
  const generatedId = useId();
  const titleId = headingId ?? generatedId;
  const linkWords = toLabel ?? "Open";
  const Heading = headingLevel === 2 ? "h2" : "h3";
  const descriptionId = `${titleId}-description`;
  return (
    <section
      id={id}
      tabIndex={tabIndex}
      aria-labelledby={titleId}
      aria-describedby={description ? descriptionId : undefined}
      data-testid={dataTestId}
      className={["mm-panel", padded ? "mm-panel--padded" : "", className]
        .filter(Boolean)
        .join(" ")}
    >
      <header
        className={["mm-panel__head", note ? "mm-panel__head--note" : ""]
          .filter(Boolean)
          .join(" ")}
      >
        {note ? (
          <div className="mm-panel__titles">
            <Heading id={titleId} className="mm-panel__title">
              {title}
            </Heading>
            <p className="mm-panel__note">{note}</p>
          </div>
        ) : (
          <Heading id={titleId} className="mm-panel__title">
            {title}
          </Heading>
        )}
        {controls ? (
          <div className="mm-panel__controls">{controls}</div>
        ) : (
          <span
            className="mm-panel__count"
            title={typeof count === "string" ? count : undefined}
          >
            {count}
          </span>
        )}
        {aside ? <div className="mm-panel__aside">{aside}</div> : null}
        {to ? (
          <Link
            to={to}
            className={
              iconOnly
                ? "mm-panel__link mm-panel__link--icon"
                : "mm-panel__link"
            }
            aria-label={`${linkWords}: ${title}`}
          >
            {iconOnly ? null : (toText ?? linkWords)}
            <svg
              viewBox="0 0 24 24"
              width="14"
              height="14"
              fill="none"
              stroke="currentColor"
              strokeWidth="2"
              strokeLinecap="round"
              strokeLinejoin="round"
              aria-hidden="true"
            >
              <path d="M5 12h14" />
              <path d="m12 5 7 7-7 7" />
            </svg>
          </Link>
        ) : null}
      </header>
      {description ? (
        <p id={descriptionId} className="sr-only">
          {description}
        </p>
      ) : null}
      <div
        className={["mm-panel__body", bodyClassName].filter(Boolean).join(" ")}
      >
        {children}
      </div>
    </section>
  );
}
