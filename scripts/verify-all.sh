#!/usr/bin/env bash
#
# Runs the four gates CI runs, in the order CI runs them, from a checkout.
#
# CI is the authority, but its logs need an authenticated request, and this repository's runs failed
# at the build step with nothing readable behind the annotation. Running the same commands here
# answers the same question without a token, which is also how every failure mode quoted in plan.md
# and docs/mcp.md was measured in the first place.
#
# Usage:
#   scripts/verify-all.sh                # all four, build-and-test first
#   scripts/verify-all.sh suite auth     # a subset, in the order given
#   scripts/verify-all.sh restore        # the backup drill: dump, restore, migrate, read back
#
# `restore` is last in the default set because it is the slowest: it publishes DbAdmin and moves a
# dump through two containers, which is minutes rather than seconds. It belongs in the set anyway.
# Migrations here are forward-only and `reset --force` drops the database, so a dump that cannot be
# restored into a scratch one is the failure with no other way back — and a drill nobody runs on
# every change is a claim about the last time somebody remembered to.
#
# Environment:
#   SDK_IMAGE  container the suite runs in (default mcr.microsoft.com/dotnet/sdk:10.0). There is no
#              host dotnet on the machines this was written on, and a pinned SDK container is also a
#              statement about which SDK the gate is about. CI installs whatever "10.0.x" resolves
#              to, so a difference between the two is a real thing to look at, not noise.
#   PG_IMAGE   default pgvector/pgvector:pg16 (PostgreSQL 16 with pgvector, for the knowledge-store suites)
#
# Requirements: docker; auth and images need what their own scripts need.

set -euo pipefail

REPO_ROOT=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
cd "$REPO_ROOT"

SDK_IMAGE=${SDK_IMAGE:-mcr.microsoft.com/dotnet/sdk:10.0}
PG_IMAGE=${PG_IMAGE:-pgvector/pgvector:pg16}

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

# The CI job's build-and-test, step for step and in its order: restore, build, the EF model check,
# the tests, the skip and "no tests ran" guards, and the dependency audit. The database the live
# fixtures need is here too: the live suites skip silently without RMA_TEST_CONNECTION, so the
# connection string and the skip check travel together — a green run that ran no database test is
# the failure this gate exists to prevent.
suite() {
    step "suite: restore, build, EF model, tests against a throwaway PostgreSQL, dependency audit"

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

            # A migration left uncommitted, or a model changed without one, builds and passes every
            # test in this repository and then fails at `rma db migrate` in a deployment.
            dotnet tool restore
            dotnet ef migrations has-pending-model-changes \
                --project src/AIEnabledRma.Data \
                --no-build \
                --configuration Debug

            dotnet test AIEnabledRma.slnx --no-build --logger "console;verbosity=normal" | tee test.log

            if grep -Eq "Skipped:[[:space:]]+[1-9]" test.log; then
                echo "tests were skipped: the live suites must run against the service database" >&2
                exit 1
            fi

            if ! grep -Eq "Total tests:[[:space:]]+[1-9]" test.log; then
                echo "no tests ran" >&2
                exit 1
            fi

            # The same audit CI runs, last: a restore against the locked file still resolves
            # transitive packages, and one of them can acquire a published advisory the day after a
            # green run. This needs the live advisory feed, so an offline machine fails here rather
            # than reporting a pass CI would not give.
            dotnet list AIEnabledRma.slnx package --vulnerable --include-transitive | tee vulnerable.log
            if grep -q "has the following vulnerable packages" vulnerable.log; then
                echo "a dependency has a known vulnerability" >&2
                exit 1
            fi
        '
}

auth() {
    step "auth: realm import, both flows and the tokens the transport must refuse"
    ./scripts/verify-auth.sh
}

images() {
    step "images: build all three, boot both corpus hosts, start the production web service"
    ./scripts/verify-images.sh

    # The runtime-images job's other step. A pack that resolves and cannot serve is a deployment
    # finding this out, so the local gate checks the same two things the job does.
    step "pack: resolve docker-compose.prod.yml and check every wait it declares can finish"
    ./scripts/verify-compose.sh
}

restore() {
    step "restore: dump a seeded database, restore it into a scratch one, migrate and query it"
    ./scripts/restore-drill.sh
}

if [ "$#" -eq 0 ]; then
    set -- suite auth images restore
fi

for requested in "$@"; do
    case $requested in
        suite) suite ;;
        auth) auth ;;
        images) images ;;
        restore) restore ;;
        *) fail "unknown step" "$requested (expected suite, auth, images or restore)" ;;
    esac
done

printf '\nall requested gates passed\n'
