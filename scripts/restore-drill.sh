#!/usr/bin/env bash
#
# Proves a dump of this application's database can be restored, migrated and read back.
#
# `pg_dump` succeeding says the source could be read. It says nothing about whether `pg_restore`
# reproduces a database the deployed binary can then migrate and query, and the failures a real
# restore finds — an extension the dump needs and the scratch server lacks, a migration history that
# does not survive the round trip, tables skipped silently by a non-fatal restore error — all look
# exactly like a healthy backup until the day the backup is the only copy.
#
# What this does, end to end:
#   1. starts a throwaway PostgreSQL from the pack's own image
#   2. seeds it through the application's own path (`DbAdmin seed`, which migrates first)
#   3. dumps it with `pg_dump -Fc`, copies the file out of the container and back in, so the exercise
#      includes the file leaving the server rather than staying in its /tmp
#   4. restores it with `pg_restore --exit-on-error` into a *different* database on that server
#   5. migrates the restored database (`DbAdmin migrate`) and asserts nothing is left pending
#   6. reads it back (`DbAdmin stats`) and compares every count against the source
#
# Usage:
#   scripts/restore-drill.sh
#
# Environment:
#   PG_IMAGE   default pgvector/pgvector:pg16 (the image the production pack runs)
#   SDK_IMAGE  default mcr.microsoft.com/dotnet/sdk:10.0
#   KEEP=1     leave the container, the dump and the working directory for inspection
#
# Requirements: docker. Nothing else: there is no host dotnet on the machines this was written on,
# so DbAdmin is published and run in the SDK container, the same way the suite does it.

set -euo pipefail

REPO_ROOT=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
cd "$REPO_ROOT"

PG_IMAGE=${PG_IMAGE:-pgvector/pgvector:pg16}
SDK_IMAGE=${SDK_IMAGE:-mcr.microsoft.com/dotnet/sdk:10.0}
NETWORK=rma-restore-drill
SERVER=rma-restore-drill-pg
SEED_DB=rmadrill_seed
SCRATCH_DB=rmadrill_scratch
DB_USER=rmauser
DB_PASSWORD=restore-drill-not-a-real-password
WORK=$(mktemp -d)
OUT="$WORK/out"
TREE="$WORK/tree"

failures=0
pass() { printf 'ok    %s\n' "$1"; }
fail() {
    printf 'FAIL  %s\n        %s\n' "$1" "${2:-}" >&2
    failures=$((failures + 1))
}

cleanup() {
    if [ "${KEEP:-0}" = 1 ]; then
        printf '\nkept: container %s, working directory %s\n' "$SERVER" "$WORK"
        return
    fi

    docker rm -f "$SERVER" >/dev/null 2>&1 || true
    docker network rm "$NETWORK" >/dev/null 2>&1 || true

    # The publish writes into the working directory as root inside the SDK container, so the user
    # running this does not own those files. A plain rm then fails, and because this runs from an
    # EXIT trap under `set -e`, it reported a drill whose every check had passed as a failure —
    # which is exactly what happened on the first CI run of this gate. Delete the tree from inside
    # a container first, then the empty directory from here, and never let tidying up decide the
    # exit status.
    if [ -d "$WORK" ]; then
        docker run --rm -v "$(winpath "$WORK"):/work" "$SDK_IMAGE" \
            bash -lc 'find /work -mindepth 1 -delete' >/dev/null 2>&1 || true
        rm -rf "$WORK" >/dev/null 2>&1 || true
    fi
}

trap cleanup EXIT

# Docker needs a Windows path for a bind mount under Git Bash; a Linux host has nothing to convert.
winpath() {
    if command -v cygpath >/dev/null 2>&1; then
        cygpath -w "$1"
    else
        printf '%s' "$1"
    fi
}

dbadmin() {
    local database=$1
    shift

    MSYS_NO_PATHCONV=1 docker run --rm --network "$NETWORK" \
        -v "$(winpath "$OUT"):/app" -w /app \
        -e "ConnectionStrings__Rma=Host=$SERVER;Port=5432;Database=$database;Username=$DB_USER;Password=$DB_PASSWORD" \
        -e "ASPNETCORE_ENVIRONMENT=Development" \
        "$SDK_IMAGE" dotnet AIEnabledRma.Tools.DbAdmin.dll "$@"
}

command -v docker >/dev/null 2>&1 || {
    echo "docker is required" >&2
    exit 1
}

printf '\n== restore drill: dump a seeded database, restore it into a scratch one, migrate and read it\n'

docker network create "$NETWORK" >/dev/null 2>&1 || true
docker rm -f "$SERVER" >/dev/null 2>&1 || true

if ! docker run -d --name "$SERVER" --network "$NETWORK" \
    -e POSTGRES_USER="$DB_USER" \
    -e POSTGRES_PASSWORD="$DB_PASSWORD" \
    -e POSTGRES_DB="$SEED_DB" \
    "$PG_IMAGE" >/dev/null; then
    fail "server starts" "docker would not start $PG_IMAGE"
fi

ready=no
for _ in $(seq 1 60); do
    if docker exec "$SERVER" pg_isready -U "$DB_USER" -d "$SEED_DB" >/dev/null 2>&1; then
        ready=yes
        break
    fi

    if [ "$(docker inspect -f '{{.State.Running}}' "$SERVER" 2>/dev/null || echo false)" != true ]; then
        break
    fi

    sleep 1
done

if [ "$ready" != yes ]; then
    fail "server is ready" "PostgreSQL did not accept connections; last lines of its log:"
    docker logs "$SERVER" 2>&1 | tail -8 | sed 's/^/          /'
    printf '\n%d check(s) failed\n' "$failures"
    exit 1
fi

pass "throwaway server is ready"

# The same source tree the gate would check, without the artefacts a Windows build leaves behind: a
# bin/ or obj/ from the host can shadow the container's build and fail the publish for a reason that
# has nothing to do with the drill.
mkdir -p "$TREE" "$OUT"
tar -cf - --exclude=./.git --exclude=./data --exclude='*/bin' --exclude='*/obj' . | tar -xf - -C "$TREE"

if ! MSYS_NO_PATHCONV=1 docker run --rm \
    -v "$(winpath "$TREE"):/src" -w /src \
    -v rma-verify-all-nuget:/root/.nuget/packages \
    -v "$(winpath "$OUT"):/out" \
    "$SDK_IMAGE" bash -lc '
        set -euo pipefail
        dotnet publish tools/DbAdmin -c Release -o /out >/tmp/publish.log 2>&1 || (tail -20 /tmp/publish.log; exit 1)
    '; then
    fail "DbAdmin publishes" "dotnet publish failed"
    printf '\n%d check(s) failed\n' "$failures"
    exit 1
fi

pass "DbAdmin published from this tree"

# Seed through the application's own path: the schema and the data both matter, and a dump of an
# empty database would restore perfectly and prove nothing.
if ! dbadmin "$SEED_DB" seed >"$WORK/seed.txt" 2>&1; then
    fail "seed" "DbAdmin seed failed:"
    tail -12 "$WORK/seed.txt" | sed 's/^/          /'
    printf '\n%d check(s) failed\n' "$failures"
    exit 1
fi

# Only the seven row counts, so the comparison later is about data rather than log lines.
grep -E '^  [a-z_]+:[[:space:]]+[0-9]+$' "$WORK/seed.txt" >"$WORK/seed-counts.txt" || true

if [ ! -s "$WORK/seed-counts.txt" ]; then
    fail "seed reports counts" "no row counts in the seeder's output:"
    tail -12 "$WORK/seed.txt" | sed 's/^/          /'
fi

if ! grep -qE '^  rma_requests:[[:space:]]+[1-9]' "$WORK/seed-counts.txt"; then
    fail "seed writes data" "the dump would restore no returns, so the drill would prove nothing"
fi

pass "seeded: $(tr -s ' ' <"$WORK/seed-counts.txt" | tr '\n' ' ' | sed 's/ $//')"

# -Fc is the format the pack's operator instructions use (custom format: compressed, selective
# restore, pg_restore-able). The file leaves the container and comes back, because a dump that only
# ever existed inside the server it came from is not a backup.
# MSYS_NO_PATHCONV=1 throughout the docker exec calls: the paths below are the container's, and
# Git Bash would otherwise rewrite /tmp/seed.dump into a Windows path that does not exist there.
if ! MSYS_NO_PATHCONV=1 docker exec "$SERVER" pg_dump -U "$DB_USER" -Fc -d "$SEED_DB" -f /tmp/seed.dump; then
    fail "pg_dump" "pg_dump -Fc failed"
fi

# The container side is a container path and must stay one; the host side has to be the Windows
# path under Git Bash, which is why it goes through winpath rather than being left as /tmp/...
MSYS_NO_PATHCONV=1 docker cp "$SERVER:/tmp/seed.dump" "$(winpath "$WORK/seed.dump")" >/dev/null \
    || fail "dump leaves the server" "docker cp failed"
MSYS_NO_PATHCONV=1 docker cp "$(winpath "$WORK/seed.dump")" "$SERVER:/tmp/restore.dump" >/dev/null \
    || fail "dump comes back" "docker cp failed"

dump_bytes=$(wc -c <"$WORK/seed.dump" | tr -d ' ')
if [ "${dump_bytes:-0}" -lt 1024 ]; then
    fail "dump is not empty" "the dump file is ${dump_bytes} bytes"
fi

pass "dumped and moved: $dump_bytes bytes"

# A different database on the same server: the restore has to recreate the schema rather than
# inherit it from the source, and the source stays intact for comparison.
if ! docker exec "$SERVER" psql -U "$DB_USER" -d postgres -v ON_ERROR_STOP=1 \
    -c "CREATE DATABASE \"$SCRATCH_DB\"" >/dev/null; then
    fail "scratch database" "CREATE DATABASE $SCRATCH_DB failed"
fi

# --exit-on-error, because pg_restore's default is to report errors and exit zero: a restore that
# skipped half the tables would otherwise be called a success by the script and by the operator.
if ! MSYS_NO_PATHCONV=1 docker exec "$SERVER" pg_restore -U "$DB_USER" -d "$SCRATCH_DB" --no-owner --exit-on-error /tmp/restore.dump; then
    fail "pg_restore" "pg_restore into $SCRATCH_DB failed (a non-zero exit means it did not silently skip errors)"
fi

pass "restored into $SCRATCH_DB"

# The migration history has to have survived the round trip, or a deployment would try to run every
# migration again against a schema that already has the tables.
if ! dbadmin "$SCRATCH_DB" migrate >"$WORK/migrate.txt" 2>&1; then
    fail "migrate the restored database" "DbAdmin migrate failed:"
    tail -12 "$WORK/migrate.txt" | sed 's/^/          /'
fi

if ! dbadmin "$SCRATCH_DB" migrations >"$WORK/migrations.txt" 2>&1; then
    fail "list migrations" "DbAdmin migrations failed:"
    tail -12 "$WORK/migrations.txt" | sed 's/^/          /'
fi

pending=$(sed -n '/^Pending:/,$p' "$WORK/migrations.txt" | tail -n +2 | sed '/^[[:space:]]*$/d')
if [ "$pending" != "  (none)" ]; then
    fail "nothing pending after restore" "the restored database still wants migrations:"
    printf '%s\n' "$pending" | sed 's/^/          /'
fi

pass "restored database is at the current schema (no pending migrations)"

# Read it back through the application's own context, not psql: the claim is that a host pointed at
# the restored database can query it, and a column the model expects but the dump lost shows up here.
if ! dbadmin "$SCRATCH_DB" stats >"$WORK/scratch.txt" 2>&1; then
    fail "query the restored database" "DbAdmin stats failed:"
    tail -12 "$WORK/scratch.txt" | sed 's/^/          /'
fi

grep -E '^  [a-z_]+:[[:space:]]+[0-9]+$' "$WORK/scratch.txt" >"$WORK/scratch-counts.txt" || true

if ! diff -u "$WORK/seed-counts.txt" "$WORK/scratch-counts.txt" >"$WORK/counts.diff"; then
    fail "row counts match the source" "the restored database does not hold what was dumped:"
    sed 's/^/          /' "$WORK/counts.diff"
else
    pass "every row count matches the source"
fi

if [ "$failures" -ne 0 ]; then
    printf '\n%d check(s) failed\n' "$failures"
    exit 1
fi

printf '\nall restore-drill checks passed\n'
