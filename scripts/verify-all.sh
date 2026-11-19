#!/usr/bin/env bash
#
# Runs the three gates CI runs, in the order CI runs them, from a checkout.
#
# CI is the authority, but its logs need an authenticated request, and this repository's runs failed
# at the build step with nothing readable behind the annotation. Running the same commands here
# answers the same question without a token, which is also how every failure mode quoted in plan.md
# and docs/mcp.md was measured in the first place.
#
# Usage:
#   scripts/verify-all.sh                # all three, build-and-test first
#   scripts/verify-all.sh suite auth     # a subset, in the order given
#
# Environment:
#   SDK_IMAGE  container the suite runs in (default mcr.microsoft.com/dotnet/sdk:10.0). There is no
#              host dotnet on the machines this was written on, and a pinned SDK container is also a
#              statement about which SDK the gate is about. CI installs whatever "10.0.x" resolves
#              to, so a difference between the two is a real thing to look at, not noise.
#   PG_IMAGE   default postgres:16-alpine
#
# Requirements: docker; auth and images need what their own scripts need.

set -euo pipefail

REPO_ROOT=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
cd "$REPO_ROOT"

SDK_IMAGE=${SDK_IMAGE:-mcr.microsoft.com/dotnet/sdk:10.0}
PG_IMAGE=${PG_IMAGE:-postgres:16-alpine}

SUITE_NETWORK=rma-verify-all
SUITE_DB=rma-verify-all-pg
SUITE_DIR=""

step() { printf '\n== %s\n' "$1"; }

fail() { printf 'FAIL  %s\n        %s\n' "$1" "${2:-}" >&2; exit 1; }

cleanup() {
    docker rm -f "$SUITE_DB" >/dev/null 2>&1 || true
    docker network rm "$SUITE_NETWORK" >/dev/null 2>&1 || true

    if [ -n "$SUITE_DIR" ]; then
        rm -rf "$SUITE_DIR"
    fi
}

trap cleanup EXIT

# The CI job's build-and-test, including the database the live fixtures need. The live suites skip
# silently without RMA_TEST_CONNECTION, so the connection string and the skip check travel together:
# a green run that ran no database test is the failure this gate exists to prevent.
suite() {
    step "suite: restore --locked-mode, build -warnaserror, test against a throwaway PostgreSQL"

    command -v docker >/dev/null 2>&1 || fail "suite" "docker is required"

    docker network create "$SUITE_NETWORK" >/dev/null 2>&1 || true
    docker rm -f "$SUITE_DB" >/dev/null 2>&1 || true

    docker run -d --name "$SUITE_DB" --network "$SUITE_NETWORK" \
        -e POSTGRES_USER=rmauser \
        -e POSTGRES_PASSWORD=rmapassword \
        -e POSTGRES_DB=rmadev \
        "$PG_IMAGE" >/dev/null

    # A copy of the working tree, uncommitted changes included: this is the checkout being checked,
    # not the last commit. bin/ and obj/ are left out so a Windows build artefact cannot shadow the
    # container's build, and data/ is the pg volume on this machine, not source.
    SUITE_DIR=$(mktemp -d)
    tar -cf - --exclude=./.git --exclude=./data --exclude='*/bin' --exclude='*/obj' . \
        | tar -xf - -C "$SUITE_DIR"

    local mount
    if command -v cygpath >/dev/null 2>&1; then
        mount=$(cygpath -w "$SUITE_DIR")
    else
        mount=$SUITE_DIR
    fi

    MSYS_NO_PATHCONV=1 docker run --rm --network "$SUITE_NETWORK" \
        -v rma-verify-all-nuget:/root/.nuget/packages \
        -v "$mount:/build" -w /build \
        -e RMA_TEST_CONNECTION="Host=$SUITE_DB;Port=5432;Database=rmadev;Username=rmauser;Password=rmapassword" \
        "$SDK_IMAGE" bash -lc '
            set -euo pipefail
            dotnet restore AIEnabledRma.slnx --locked-mode
            dotnet build AIEnabledRma.slnx --no-restore -warnaserror
            dotnet test AIEnabledRma.slnx --no-build --logger "console;verbosity=normal" | tee test.log

            if grep -Eq "Skipped:[[:space:]]+[1-9]" test.log; then
                echo "tests were skipped: the live suites must run against the service database" >&2
                exit 1
            fi

            if ! grep -Eq "Total tests:[[:space:]]+[1-9]" test.log; then
                echo "no tests ran" >&2
                exit 1
            fi
        '
}

auth() {
    step "auth: realm import, both flows and the tokens the transport must refuse"
    ./scripts/verify-auth.sh
}

images() {
    step "images: build both, check what each host reads and the commit label"
    ./scripts/verify-images.sh
}

if [ "$#" -eq 0 ]; then
    set -- suite auth images
fi

for requested in "$@"; do
    case $requested in
        suite) suite ;;
        auth) auth ;;
        images) images ;;
        *) fail "unknown step" "$requested (expected suite, auth or images)" ;;
    esac
done

printf '\nall requested gates passed\n'
