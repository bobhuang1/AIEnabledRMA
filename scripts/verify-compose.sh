#!/usr/bin/env bash
#
# Resolves the production pack with the example env, then checks what resolving cannot: that every
# service waited on as healthy has a probe to become healthy with.
#
# `docker compose config` already fails on a renamed variable, a broken anchor and a dependency on a
# service that does not exist, and CI ran that command by itself. It does not fail on
# `condition: service_healthy` naming a service with no healthcheck: the project resolves, and
# `up --no-start` accepts it too. The wait then happens at deployment, against a container that will
# never report a health state, and the proxy behind it never starts — a stack that looks built and
# never serves. That is the case this script exists for; everything else it runs is the resolve CI
# already had, kept beside it so one command answers the whole question.
#
# Usage:
#   scripts/verify-compose.sh
#   PACK=copy-of-the-pack.yml scripts/verify-compose.sh    # check a mutated copy by hand
#
# Environment:
#   PACK       compose file to resolve (default docker-compose.prod.yml)
#   ENV_FILE   env file to interpolate from (default deploy/prod.env.example), which is the one CI and
#              the image gate use: every required value there is a placeholder, so nothing real is
#              needed to answer whether the file parses.
#
# Requirements: docker with the compose plugin, and a python that runs. Starts nothing, and reads no
# container.

set -euo pipefail

REPO_ROOT=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
cd "$REPO_ROOT"

PACK=${PACK:-docker-compose.prod.yml}
ENV_FILE=${ENV_FILE:-deploy/prod.env.example}

command -v docker >/dev/null 2>&1 || { echo "verify-compose: docker is required" >&2; exit 2; }
docker compose version >/dev/null 2>&1 || { echo "verify-compose: the compose plugin is required" >&2; exit 2; }

# `command -v` is not enough on Windows, where python3 can be an App Execution Alias that exists and
# refuses to run. Ask the interpreter to prove it works before trusting it.
PY=""
for candidate in python3 python; do
    if command -v "$candidate" >/dev/null 2>&1 && "$candidate" -c 'import json' >/dev/null 2>&1; then
        PY=$candidate
        break
    fi
done

if [ -z "$PY" ]; then
    echo "verify-compose: python is required to read the resolved project" >&2
    exit 2
fi

failures=0
pass() { printf 'ok    %s\n' "$1"; }
fail() {
    printf 'FAIL  %s\n        %s\n' "$1" "${2:-}" >&2
    failures=$((failures + 1))
}

WORK=$(mktemp -d)
trap 'rm -rf "$WORK"' EXIT

compose() { docker compose -f "$PACK" --env-file "$ENV_FILE" "$@"; }

# The resolve, and the one check that already existed. A renamed variable, a broken anchor and a
# dependency on a service that does not exist all fail here, before anything is read.
if ! compose config --format json > "$WORK/project.json" 2> "$WORK/resolve.err"; then
    fail "$PACK resolves with $ENV_FILE" "$(cat "$WORK/resolve.err")"
    printf '\n%d problem(s).\n' "$failures" >&2
    exit 1
fi

pass "$PACK resolves with $ENV_FILE"

# Every wait can finish. Read from the resolved project rather than the source text, because that is
# the only form where an anchor has been expanded and every default has been applied: a service that
# inherits its healthcheck through `<<: *hardening` looks, in the source, like one that has none.
status=0
problems=$("$PY" - "$WORK/project.json" <<'PY'
import json
import sys

project = json.load(open(sys.argv[1], encoding="utf-8"))
services = project.get("services") or {}
problems = []
waits = 0

for name in sorted(services):
    service = services[name] or {}

    # A healthcheck whose test resolved away is a probe that reports nothing, which a dependency
    # waiting on it reads as ill health forever.
    probe = service.get("healthcheck") or {}
    if probe and not probe.get("test"):
        problems.append(f"{name} declares a healthcheck with no test")

    for target, dependency in sorted((service.get("depends_on") or {}).items()):
        if (dependency or {}).get("condition") != "service_healthy":
            continue

        waits += 1
        target_probe = (services.get(target) or {}).get("healthcheck") or {}

        if not target_probe.get("test"):
            problems.append(
                f"{name} waits for {target} to be healthy, but {target} has no healthcheck")

print("\n".join(problems))

if problems:
    sys.exit(1)

# No wait found is a failed check: a loop over an empty set passes without looking at anything, and
# the pack losing its `depends_on` is exactly the edit this is supposed to notice.
if not services or waits == 0:
    print("no service_healthy dependency was found, so nothing was checked")
    sys.exit(3)
PY
) || status=$?

case $status in
    0) pass "every service_healthy dependency names a service with a healthcheck" ;;
    3) fail "the pack declares a service_healthy dependency" "$problems" ;;
    *) fail "every service_healthy dependency names a service with a healthcheck" "$problems" ;;
esac

if [ "$failures" -gt 0 ]; then
    printf '\n%d problem(s).\n' "$failures" >&2
    exit 1
fi

printf '\nThe production pack resolves, and every wait it declares can finish.\n'
