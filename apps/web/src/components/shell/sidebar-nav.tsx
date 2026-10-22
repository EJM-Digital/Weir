import { forwardRef, type Ref } from "react";
import { Link, useLocation } from "react-router-dom";

import { activityGroupPath } from "../../pages/activity/activity-links";
import { NavGlyph } from "./nav-icons";
import {
  NAV_BADGE_MEANING,
  NAV_GROUPS,
  type NavBadgeKind,
  type NavItem,
} from "./nav-model";

/** What rides on each badge: the number, and the words a screen reader says for it. */
type Badges = Readonly<Record<NavBadgeKind, number>>;

const BADGE_WORDS: Record<NavBadgeKind, string> = {
  working: "working",
  "needs-you": "need you",
};

/**
 * Where an item goes while its badge shows, where that is not the item's own page: the files that need you are
 * listed in Activity's Needs you view, and the badge counts exactly those.
 */
const BADGE_TARGETS: Partial<Record<NavBadgeKind, string>> = {
  "needs-you": activityGroupPath("attention"),
};

const BADGE_TEST_IDS: Record<NavBadgeKind, string> = {
  working: "nav-processing-working",
  "needs-you": "nav-activity-needs-you",
};

type SidebarLinkProps = {
  item: NavItem;
  current: boolean;
  count: number;
  onNavigate: () => void;
};

const SidebarLink = forwardRef<HTMLAnchorElement, SidebarLinkProps>(
  function SidebarLink({ item, current, count, onNavigate }, ref) {
    const badge =
      item.badge && count > 0
        ? { kind: item.badge, words: `${count} ${BADGE_WORDS[item.badge]}` }
        : null;
    const target = (badge && BADGE_TARGETS[badge.kind]) || item.to;
    return (
      <Link
        ref={ref}
        to={target}
        className="mm-sidebar-link"
        aria-current={current ? "page" : undefined}
        aria-label={badge ? `${item.label}, ${badge.words}` : undefined}
        title={item.label}
        onClick={onNavigate}
      >
        <span className="mm-sidebar-link-icon" aria-hidden="true">
          <NavGlyph name={item.icon} />
        </span>
        <span className="mm-sidebar-link-label">{item.label}</span>
        {badge ? (
          <span
            className="mm-sidebar-link-badge"
            data-status={NAV_BADGE_MEANING[badge.kind]}
            data-testid={BADGE_TEST_IDS[badge.kind]}
            title={badge.words}
          >
            {count}
          </span>
        ) : null}
      </Link>
    );
  },
);

type SidebarNavProps = {
  badges: Badges;
  /** Takes focus when a phone's menu opens. */
  firstLinkRef: Ref<HTMLAnchorElement>;
  onNavigate: () => void;
};

/**
 * The side menu: groups under quiet headings, each item a link. The current page is named for a
 * screen reader with `aria-current`, which the address's path decides for a setup area and whichever of its tabs is open.
 */
export function SidebarNav({
  badges,
  firstLinkRef,
  onNavigate,
}: SidebarNavProps) {
  const { pathname, search } = useLocation();
  const place = { pathname, search };
  return (
    <nav className="mm-sidebar-nav" aria-label="Primary">
      {NAV_GROUPS.map((group, groupIndex) => {
        const headingId = `mm-nav-group-${group.id}`;
        return (
          <div
            key={group.id}
            className="mm-sidebar-group"
            role="group"
            aria-labelledby={headingId}
          >
            <p id={headingId} className="mm-sidebar-group__label">
              {group.label}
            </p>
            {group.items.map((item, itemIndex) => (
              <SidebarLink
                key={item.id}
                ref={
                  groupIndex === 0 && itemIndex === 0 ? firstLinkRef : undefined
                }
                item={item}
                current={item.isCurrent(place)}
                count={item.badge ? badges[item.badge] : 0}
                onNavigate={onNavigate}
              />
            ))}
          </div>
        );
      })}
    </nav>
  );
}
