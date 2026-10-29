import { useCallback, useRef, useState } from "react";
import { Link } from "react-router-dom";

import { useCloseOnOutsideAndEscape } from "../../lib/ui/use-close-on-outside";
import { NavIconChevronDown, NavIconSignOut } from "./nav-icons";

type SidebarUserProps = {
  username: string | undefined;
  version: string | undefined;
  signingOut: boolean;
  /** False for a device that skipped sign-in: there is no sign-in to end. */
  canSignOut: boolean;
  onSignOut: () => void;
  onNavigate: () => void;
};

const AT_MOST_INITIALS = 2;

/** "ann.lee" is AL and "admin" is A: the first letter of each word, up to two. */
export function initialsOf(username: string | undefined): string {
  const words = (username ?? "").split(/[^\p{L}\p{N}]+/u).filter(Boolean);
  const letters = words
    .slice(0, AT_MOST_INITIALS)
    .map((word) => word[0].toUpperCase());
  return letters.length > 0 ? letters.join("") : "W";
}

/**
 * Who is signed in, at the foot of the side menu: initials and name. A click opens a menu with the
 * version, where to change a password, and Sign out.
 */
export function SidebarUser({
  username,
  version,
  signingOut,
  canSignOut,
  onSignOut,
  onNavigate,
}: SidebarUserProps) {
  const [open, setOpen] = useState(false);
  const containerRef = useRef<HTMLDivElement>(null);
  const close = useCallback(() => setOpen(false), []);
  useCloseOnOutsideAndEscape(open, close, containerRef);

  return (
    <div className="mm-sidebar-user" ref={containerRef}>
      <button
        type="button"
        className="mm-sidebar-user__button"
        data-testid="user-menu"
        aria-expanded={open}
        aria-haspopup="true"
        onClick={() => setOpen((value) => !value)}
      >
        <span className="mm-sidebar-user__avatar" aria-hidden="true">
          {initialsOf(username)}
        </span>
        <span className="mm-sidebar-user__who">
          <span className="mm-sidebar-user__name">
            {username ?? "Signed in"}
          </span>
        </span>
        <NavIconChevronDown className="mm-sidebar-user__chevron" />
      </button>
      {open ? (
        <div className="mm-sidebar-user__menu">
          <p
            className="mm-sidebar-user__version"
            title="Installed Weir version reported by the running server"
          >
            {version ? `Version ${version}` : "Checking version…"}
          </p>
          <Link
            to="/system?tab=security"
            className="mm-sidebar-user__item"
            onClick={() => {
              close();
              onNavigate();
            }}
          >
            Change password
          </Link>
          {canSignOut ? (
            <button
              type="button"
              className="mm-sidebar-user__item mm-sidebar-user__item--danger"
              data-testid="sign-out"
              disabled={signingOut}
              onClick={onSignOut}
            >
              <NavIconSignOut />
              {signingOut ? "Signing out…" : "Sign out"}
            </button>
          ) : null}
        </div>
      ) : null}
    </div>
  );
}
