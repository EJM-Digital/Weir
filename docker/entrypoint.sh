#!/bin/sh
set -e

# Entrypoint for the Weir image (Dockerfile). Validates the runtime settings, remaps the weir
# user to WEIR_PUID/WEIR_PGID, makes sure a session secret exists, then runs the .NET server as
# the unprivileged weir user. There is no separate migration step: the server creates or migrates
# its own SQLite database on start (apps/server/src/Weir.Host/WeirServer.cs, OpenDatabase).

export WEIR_HOME="${WEIR_HOME:-/data/weir}"
WEIR_PUID="${WEIR_PUID:-${PUID:-1000}}"
WEIR_PGID="${WEIR_PGID:-${PGID:-1000}}"
WEIR_CHOWN_WATCHED="${WEIR_CHOWN_WATCHED:-false}"
WEIR_CHOWN_TEMP="${WEIR_CHOWN_TEMP:-false}"
WEIR_CHOWN_OUTPUT="${WEIR_CHOWN_OUTPUT:-false}"
WEIR_DIR_MODE_WATCHED="${WEIR_DIR_MODE_WATCHED:-}"
WEIR_DIR_MODE_TEMP="${WEIR_DIR_MODE_TEMP:-}"
WEIR_DIR_MODE_OUTPUT="${WEIR_DIR_MODE_OUTPUT:-}"

log_info() {
  echo "info: $*" >&2
}

fail() {
  echo "error: $*" >&2
  exit 1
}

validate_uint() {
  value="$1"
  name="$2"
  case "$value" in
    ""|*[!0-9]*)
      fail "$name must be a non-negative integer."
      ;;
  esac
}

# Running the server as root defeats the point of gosu-dropping to `weir` in the first place: a
# compromised request handler would then be able to touch anything on the host that the container
# can reach, not just WEIR_HOME. There is no supported reason to opt into that, so it is refused
# rather than warned about.
validate_not_root_id() {
  value="$1"
  name="$2"
  if [ "$value" = "0" ]; then
    fail "$name must not be 0: Weir refuses to run its server as root. Pick a non-root uid/gid, or leave $name unset to use the image default (1000)."
  fi
}

validate_boolish() {
  value="$(printf '%s' "$1" | tr '[:upper:]' '[:lower:]')"
  name="$2"
  case "$value" in
    ""|0|1|true|false|yes|no|on|off)
      ;;
    *)
      fail "$name must be one of: true/false, 1/0, yes/no, on/off."
      ;;
  esac
}

validate_dir_mode() {
  value="$1"
  name="$2"
  if [ -z "$value" ]; then
    return
  fi
  case "$value" in
    *[!0-7]*)
      fail "$name must be an octal directory mode such as 775 or 2775."
      ;;
  esac
  case "${#value}" in
    3|4)
      ;;
    *)
      fail "$name must be an octal directory mode such as 775 or 2775."
      ;;
  esac
}

bool_enabled() {
  value="$(printf '%s' "$1" | tr '[:upper:]' '[:lower:]')"
  case "$value" in
    1|true|yes|on)
      return 0
      ;;
    *)
      return 1
      ;;
  esac
}

update_runtime_identity() {
  current_gid="$(getent group weir | cut -d: -f3)"
  current_uid="$(id -u weir)"
  if [ "$current_gid" != "$WEIR_PGID" ]; then
    log_info "Updating weir group id to $WEIR_PGID"
    groupmod -o -g "$WEIR_PGID" weir
  fi
  if [ "$current_uid" != "$WEIR_PUID" ]; then
    log_info "Updating weir user id to $WEIR_PUID"
    usermod -o -u "$WEIR_PUID" -g "$WEIR_PGID" weir
  fi
}

# /opt/weir is owned by root and read-only for weir (see Dockerfile); it is never chowned here.
# Checking only WEIR_HOME's top-level owner, with a single stat, keeps every normal start fast
# even when WEIR_HOME holds a large database or years of logs. A recursive chown runs only when
# that one check says ownership is actually wrong: a bind mount created by the host, or a
# WEIR_PUID/WEIR_PGID remap since the last start, either of which would otherwise leave the
# server unable to read its own database and logs.
ensure_runtime_home_ownership() {
  mkdir -p "$WEIR_HOME"
  current_owner="$(stat -c '%u:%g' "$WEIR_HOME")"
  target_owner="$WEIR_PUID:$WEIR_PGID"
  if [ "$current_owner" != "$target_owner" ]; then
    log_info "WEIR_HOME is owned by $current_owner, not $target_owner; fixing ownership"
    chown -R weir:weir "$WEIR_HOME"
  fi
}

# Weir applies WEIR_CHOWN_OUTPUT, WEIR_FILE_MODE_OUTPUT and WEIR_DIR_MODE_OUTPUT itself, to each file or folder it
# publishes (apps/server/README.md, "Output ownership"), so none of this script's job. The four settings for the watched
# and work folders have nothing to apply to (docker/README.md): they are validated above, and said to be ignored here.
log_ignored_folder_setting() {
  log_info "$1 is set, but this image does not apply it to your watched or work folders. Ignoring it (see docker/README.md)."
}

warn_ignored_folder_settings() {
  if bool_enabled "$WEIR_CHOWN_WATCHED"; then log_ignored_folder_setting WEIR_CHOWN_WATCHED; fi
  if bool_enabled "$WEIR_CHOWN_TEMP"; then log_ignored_folder_setting WEIR_CHOWN_TEMP; fi
  if [ -n "$WEIR_DIR_MODE_WATCHED" ]; then log_ignored_folder_setting WEIR_DIR_MODE_WATCHED; fi
  if [ -n "$WEIR_DIR_MODE_TEMP" ]; then log_ignored_folder_setting WEIR_DIR_MODE_TEMP; fi
}

run_app() {
  cd /opt/weir
  exec ./Weir --port "${PORT:-9347}"
}

validate_uint "$WEIR_PUID" "WEIR_PUID"
validate_uint "$WEIR_PGID" "WEIR_PGID"
validate_not_root_id "$WEIR_PUID" "WEIR_PUID"
validate_not_root_id "$WEIR_PGID" "WEIR_PGID"
validate_boolish "$WEIR_CHOWN_WATCHED" "WEIR_CHOWN_WATCHED"
validate_boolish "$WEIR_CHOWN_TEMP" "WEIR_CHOWN_TEMP"
validate_boolish "$WEIR_CHOWN_OUTPUT" "WEIR_CHOWN_OUTPUT"
validate_dir_mode "$WEIR_DIR_MODE_WATCHED" "WEIR_DIR_MODE_WATCHED"
validate_dir_mode "$WEIR_DIR_MODE_TEMP" "WEIR_DIR_MODE_TEMP"
validate_dir_mode "$WEIR_DIR_MODE_OUTPUT" "WEIR_DIR_MODE_OUTPUT"
mkdir -p "$WEIR_HOME"
warn_ignored_folder_settings

generate_secret() {
  # No Python interpreter in this image. 48 random bytes, base64url-encoded without padding —
  # the same entropy as docker/entrypoint.sh's secrets.token_urlsafe(48), produced with coreutils
  # (already present in the runtime-deps base) instead.
  head -c 48 /dev/urandom | base64 | tr '+/' '-_' | tr -d '=\n'
}

# The owner-only mode belongs to the secret file alone. The umask is set in a subshell because the server inherits
# this script's umask, and 077 would make every folder and file it creates private to the weir user.
write_secret_file() {
  ( umask 077; printf '%s\n' "$2" > "$1" )
}

if [ -z "${WEIR_SESSION_SECRET:-}" ]; then
  secret_file="$WEIR_HOME/session.secret"
  if [ -f "$secret_file" ]; then
    WEIR_SESSION_SECRET="$(cat "$secret_file")"
  else
    WEIR_SESSION_SECRET="$(generate_secret)"
    write_secret_file "$secret_file" "$WEIR_SESSION_SECRET"
    log_info "generated WEIR_SESSION_SECRET at $secret_file"
  fi
  export WEIR_SESSION_SECRET
fi

# Fernet-backed features and session signing expect adequate entropy.
if [ "${#WEIR_SESSION_SECRET}" -lt 32 ]; then
  fail "WEIR_SESSION_SECRET must be at least 32 characters (try: openssl rand -hex 32)"
fi

# Saved provider keys are encrypted with a secret of their own. A WEIR_CREDENTIALS_SECRET from the environment wins;
# otherwise one is made on first start and kept next to the session secret. Keys saved before this file existed were
# encrypted with the session secret, which Weir still reads them with.
if [ -z "${WEIR_CREDENTIALS_SECRET:-}" ]; then
  credentials_file="$WEIR_HOME/credentials.secret"
  if [ -f "$credentials_file" ]; then
    WEIR_CREDENTIALS_SECRET="$(cat "$credentials_file")"
  else
    WEIR_CREDENTIALS_SECRET="$(generate_secret)"
    write_secret_file "$credentials_file" "$WEIR_CREDENTIALS_SECRET"
    log_info "generated WEIR_CREDENTIALS_SECRET at $credentials_file"
  fi
  export WEIR_CREDENTIALS_SECRET
fi

if [ "$(id -u)" -eq 0 ]; then
  update_runtime_identity
  ensure_runtime_home_ownership
  if [ -f "$WEIR_HOME/session.secret" ]; then
    chown weir:weir "$WEIR_HOME/session.secret"
  fi
  if [ -f "$WEIR_HOME/credentials.secret" ]; then
    chown weir:weir "$WEIR_HOME/credentials.secret"
  fi
  cd /opt/weir
  exec gosu weir ./Weir --port "${PORT:-9347}"
fi

run_app
