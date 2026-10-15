#!/bin/sh
# Scratch (removed before the pull request is opened): shows each check red against an entrypoint missing it.
#   mutants.sh node                  runs scripts/docker-entrypoint.test.mjs against each broken copy of docker/entrypoint.sh
#   mutants.sh container <image>     builds an image with each broken copy and runs the scenario that must catch it
set -u
mode="$1"
original="$(mktemp)"
cp docker/entrypoint.sh "$original"
results=""

# name | scenario the container run must fail | sed expression applied to docker/entrypoint.sh
MUTANTS='
no-root-refusal|refusals|/^validate_not_root_id "/d
no-id-validation|refusals|/^validate_uint "/d
no-boolean-validation|refusals|/^validate_boolish "/d
no-mode-validation|refusals|/^validate_dir_mode "/d
no-secret-length-check|refusals|s/-lt 32/-lt 0/
no-remap|identity|s/usermod -o -u "\$WEIR_PUID" -g "\$WEIR_PGID" weir/true/
no-home-ownership-fix|data-folder|s/chown -R weir:weir "\$WEIR_HOME"/true/
secrets-regenerated|identity|s/if \[ -f "\$secret_file" \]/if false/
secrets-not-private|identity|s/umask 077/umask 022/
secret-too-short|identity|s/head -c 48 /head -c 24 /
port-ignored|none|s/\${PORT:-9347}/9347/
legacy-puid-ignored|none|s/\${WEIR_PUID:-\${PUID:-1000}}/${WEIR_PUID:-1000}/
never-says-ignored|media|s/^  log_info "$1 is set.*/  true/
says-output-ignored|media|s/if \[ -n "\$WEIR_DIR_MODE_TEMP" \]; then/if [ -n "$WEIR_DIR_MODE_OUTPUT" ]; then log_ignored_folder_setting WEIR_DIR_MODE_OUTPUT; fi; if [ -n "$WEIR_DIR_MODE_TEMP" ]; then/
'

echo "$MUTANTS" | while IFS='|' read -r name scenario expression; do
  [ -n "$name" ] || continue
  [ -z "${ONLY:-}" ] || [ "$name" = "$ONLY" ] || continue
  cp "$original" docker/entrypoint.sh
  sed -i -e "$expression" docker/entrypoint.sh
  if cmp -s "$original" docker/entrypoint.sh; then
    echo "MUTANT-NOT-APPLIED $name"
    continue
  fi
  if [ "$mode" = node ]; then
    if node --test scripts/docker-entrypoint.test.mjs > "/tmp/mutant-$name.log" 2>&1; then
      echo "MUTANT-SURVIVED $name (the tests stayed green)"
    else
      echo "MUTANT-KILLED $name: $(grep -c '^not ok\|^    not ok\|✖' "/tmp/mutant-$name.log") failing lines"
    fi
  else
    [ "$scenario" = none ] && continue
    printf 'FROM %s\nCOPY entrypoint.sh /entrypoint.sh\nRUN chmod +x /entrypoint.sh\n' "$2" > /tmp/Dockerfile.mutant
    mkdir -p "/tmp/mutant-$name" && cp docker/entrypoint.sh "/tmp/mutant-$name/entrypoint.sh" && cp /tmp/Dockerfile.mutant "/tmp/mutant-$name/Dockerfile"
    docker build -q -t "weir-mutant-$name" "/tmp/mutant-$name" > /dev/null
    if node scripts/docker-compose-smoke.mjs "$scenario" --candidate "weir-mutant-$name" --logs "artifacts/mutants/$name" > "/tmp/mutant-$name.log" 2>&1; then
      echo "MUTANT-SURVIVED $name (scenario $scenario stayed green)"
    else
      echo "MUTANT-KILLED $name by scenario $scenario: $(grep -m1 '::error::' "/tmp/mutant-$name.log" | cut -c1-300)"
    fi
  fi
done | tee /tmp/mutant-summary.txt

cp "$original" docker/entrypoint.sh
