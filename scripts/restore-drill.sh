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
#   1. starts a throwaway PostgreSQL from the pack's own image, as a compose service named `postgres`
#      — the name the production pack gives its database — so step 3 takes the branch the nightly unit
#      takes (`docker compose exec postgres`) rather than a route only this drill would ever call
#   2. seeds it through the application's own path (`DbAdmin seed`, which migrates first)
#   3. runs the deployment's own backup, `scripts/backup.sh`: the dump, the pg_restore check, the
#      encryption with a passphrase beside its key file and the copy to a destination outside the
#      server are its code, not a second copy of them written here
#   4. runs it twice, so there is a newest copy for the fetch to have to choose, beside a copy already
#      past the retention window, which the same run has to delete
#   5. reads the destination with the wrong passphrase and asserts it cannot be read, deletes the local
#      copies, and fetches the newest object back, decrypted
#   6. restores that copy with `pg_restore --exit-on-error` into a *different* database on the server
#   7. migrates the restored database (`DbAdmin migrate`) and asserts nothing is left pending
#   8. reads it back (`DbAdmin stats`) and compares every count against the source
#
# Usage:
#   scripts/restore-drill.sh
#
# Environment:
#   PG_IMAGE      default pgvector/pgvector:pg16 (the image the production pack runs)
#   SDK_IMAGE     default mcr.microsoft.com/dotnet/sdk:10.0
#   RCLONE_IMAGE  default the rclone image scripts/backup.sh pins; it is what moves the dump off the
#                 server, so the drill exercises the transport rather than describing it
#   KEEP=1        leave the container, the dumps and the working directory for inspection
#
# The dump is not taken here. `scripts/backup.sh` takes it, the way deploy/rma-backup.service does,
# because a drill that dumps its own file and restores it proves the dump format and says nothing
# about the backup path -- the copy that left the host yesterday is the one a disaster needs.
#
# Requirements: docker. Nothing else: there is no host dotnet on the machines this was written on,
# so DbAdmin is published and run in the SDK container, the same way the suite does it.

set -euo pipefail

REPO_ROOT=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
cd "$REPO_ROOT"

PG_IMAGE=${PG_IMAGE:-pgvector/pgvector:pg16}
SDK_IMAGE=${SDK_IMAGE:-mcr.microsoft.com/dotnet/sdk:10.0}
RCLONE_IMAGE=${RCLONE_IMAGE:-rclone/rclone:1.68.1@sha256:e78b765d51301e1934c376e9b5d0b42a156352099502a812b5e3b3bd37ccb33b}
NETWORK=rma-restore-drill
SERVER=rma-restore-drill-pg
SEED_DB=rmadrill_seed
SCRATCH_DB=rmadrill_scratch
DB_USER=rmauser
DB_PASSWORD=restore-drill-not-a-real-password
WORK=$(mktemp -d)
OUT="$WORK/out"
TREE="$WORK/tree"

# The throwaway server is started from a compose file, as a service called `postgres`. A plain
# `docker run` container with a name of its own could only be dumped through an override the pack
# does not have, and the backup path that override skipped is the one the timer runs every night.
COMPOSE="$WORK/drill-compose.yml"
DRILL_ENV="$WORK/drill.env"

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

    # Compose first, so the project's own label bookkeeping goes with the container. The network is
    # external to the project, and is removed here.
    if [ -f "$COMPOSE" ]; then
        docker compose -f "$(winpath "$COMPOSE")" down --remove-orphans >/dev/null 2>&1 || true
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

# The pack's database, described the way the pack describes it: one service, named `postgres`, with
# the credentials in its environment because that is where pg_dump reads them from inside the
# container. scripts/backup.sh is then given this file instead of a container name, so the dump goes
# through `docker compose exec postgres` — the command deploy/rma-backup.service runs.
cat >"$COMPOSE" <<EOF
name: rma-restore-drill

services:
  postgres:
    image: $PG_IMAGE
    container_name: $SERVER
    environment:
      POSTGRES_USER: $DB_USER
      POSTGRES_PASSWORD: $DB_PASSWORD
      POSTGRES_DB: $SEED_DB
    networks: [drill]

networks:
  drill:
    name: $NETWORK
    external: true
EOF

# The env file compose is given, as the unit gives prod.env. The values are already in the service's
# environment above; what matters here is that the file exists, because compose refuses to run with
# a --env-file that does not.
cat >"$DRILL_ENV" <<EOF
POSTGRES_USER=$DB_USER
POSTGRES_DB=$SEED_DB
EOF

if ! docker compose -f "$(winpath "$COMPOSE")" up -d postgres >/dev/null; then
    fail "server starts" "docker compose would not start $PG_IMAGE as the postgres service"
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

# The backup path the deployment runs, not one invented here. A dump made a second before it is
# restored proves the format; the copy that matters is the one that left the host at 01:20 and has
# been sitting in a bucket since. `scripts/backup.sh` is the command the nightly unit runs, so the
# drill drives it: dump, verify, upload, prune -- then delete the local copy and fetch the newest
# object back, so what is restored is what the destination holds.
#
# The server above is a `postgres` service in a compose file, and that is what makes the first half
# of this the deployment's path rather than a second one: the script reaches the database with
# `docker compose exec postgres`, the same command the unit's own run uses.
#
# The destination is a directory beside the drill (`local:`, one of the remotes the script accepts)
# rather than a bucket, so the drill needs no credentials. Under that line it is the same code: the
# dump flags, the pg_restore check, the copy, the retention, and the choice of the newest object.
OFFHOST="$WORK/offhost"
STAGING="$WORK/staging"
KEEP_DAYS=14
DECOY=rma-20200101T000000Z.dump.enc
PASSPHRASE_FILE="$WORK/backup.passphrase"
WRONG_PASSPHRASE_FILE="$WORK/wrong.passphrase"

mkdir -p "$OFFHOST"

# The passphrase lives beside the key file the way the deployment keeps it beside rclone.conf, and
# it is what the destination cannot be read without.
printf '%s\n' 'restore-drill-passphrase-not-a-real-key' >"$PASSPHRASE_FILE"
printf '%s\n' 'wrong-passphrase-also-not-a-real-key' >"$WRONG_PASSPHRASE_FILE"

backup() {
    COMPOSE_FILE="$(winpath "$COMPOSE")" \
    ENV_FILE="$(winpath "$DRILL_ENV")" \
    BACKUP_REMOTE="local:$OFFHOST" \
    BACKUP_STAGING="$STAGING" \
    BACKUP_KEEP_DAYS="$KEEP_DAYS" \
    BACKUP_KEY_FILE="$PASSPHRASE_FILE" \
    RCLONE_IMAGE="$RCLONE_IMAGE" \
        "$REPO_ROOT/scripts/backup.sh" "$@"
}

# A copy already past the window, with a name an oldest-first rule would choose. Retention has to
# delete it, and the fetch below has to come back with one of the two real dumps instead.
printf 'not a dump\n' >"$OFFHOST/$DECOY"
touch -d '30 days ago' "$OFFHOST/$DECOY" 2>/dev/null || touch -t 202001010000 "$OFFHOST/$DECOY"

if ! backup backup >"$WORK/backup-1.txt" 2>&1; then
    fail "backup writes a dump off the host" "scripts/backup.sh backup failed:"
    tail -12 "$WORK/backup-1.txt" | sed 's/^/          /'
    printf '\n%d check(s) failed\n' "$failures"
    exit 1
fi

# The dump has to have gone through the compose service, or this drill is exercising a route the
# nightly unit does not have and proving nothing about 01:20. The script says which route it took.
if grep -q 'dumping into .* through docker compose exec postgres' "$WORK/backup-1.txt"; then
    pass "the dump went through docker compose exec postgres, the path the nightly unit takes"
else
    fail "the backup dumps through the pack's postgres service" \
        "scripts/backup.sh did not report the compose route; it said:"
    sed 's/^/          /' "$WORK/backup-1.txt"
fi

first=$(ls "$STAGING"/*.dump.enc 2>/dev/null | xargs -r -n1 basename | sort | tail -n 1)

# A second dump, so "the newest" has something to be wrong about. The names carry a UTC timestamp
# fixed to the second, which is what makes sorting them chronological rather than alphabetical luck.
sleep 2

if ! backup backup >"$WORK/backup-2.txt" 2>&1; then
    fail "backup runs twice" "the second scripts/backup.sh backup failed:"
    tail -12 "$WORK/backup-2.txt" | sed 's/^/          /'
    printf '\n%d check(s) failed\n' "$failures"
    exit 1
fi

second=$(ls "$STAGING"/*.dump.enc 2>/dev/null | xargs -r -n1 basename | sort | tail -n 1)

if [ -e "$OFFHOST/$DECOY" ]; then
    fail "retention deletes the copy past the window" "$DECOY is still at $OFFHOST after $KEEP_DAYS days of retention"
else
    pass "retention deleted the copy older than $KEEP_DAYS days"
fi

offhost_count=$(ls "$OFFHOST"/*.dump.enc 2>/dev/null | wc -l | tr -d ' ')

if [ "$offhost_count" = 2 ] && [ -n "$first" ] && [ "$first" != "$second" ]; then
    pass "two encrypted dumps are off the host, the newest being $second"
else
    fail "the backups are off the host" "$offhost_count object(s) at $OFFHOST, named '$first' and '$second'"
fi

# And no readable dump is there: an object an operator could open with pg_restore is the exposure
# the encryption exists to end, and a run that uploaded one anyway has to fail here.
readable=$(ls "$OFFHOST"/*.dump 2>/dev/null | wc -l | tr -d ' ')

if [ "$readable" = 0 ]; then
    pass "the destination holds ciphertext only: no *.dump object was written there"
else
    fail "nothing readable is at the destination" "$readable plaintext dump(s) at $OFFHOST"
fi

# The key is what stands between the destination and every return in it, so the drill reads it with
# the wrong one and requires the refusal. A copy that decrypts without the passphrase is not
# encrypted, whatever the file name says.
WRONG_STAGING="$WORK/wrong-staging"
mkdir -p "$WRONG_STAGING"

if BACKUP_KEY_FILE="$WRONG_PASSPHRASE_FILE" \
    COMPOSE_FILE="$(winpath "$COMPOSE")" \
    ENV_FILE="$(winpath "$DRILL_ENV")" \
    BACKUP_REMOTE="local:$OFFHOST" \
    BACKUP_STAGING="$WRONG_STAGING" \
    BACKUP_KEEP_DAYS="$KEEP_DAYS" \
    RCLONE_IMAGE="$RCLONE_IMAGE" \
        "$REPO_ROOT/scripts/backup.sh" fetch >"$WORK/wrong-fetch.txt" 2>&1; then
    fail "the destination is encrypted" \
        "a fetch with the wrong passphrase returned a dump; the objects are readable without the key"
else
    pass "the destination is ciphertext: the wrong passphrase cannot decrypt it"
fi

# The local copies go first, so the restore below cannot quietly use one of them: the only way back
# is `fetch` pulling the newest object from the destination.
rm -f "$STAGING"/*.dump "$STAGING"/*.dump.enc

if ! fetched=$(backup fetch 2>"$WORK/fetch.txt"); then
    fail "fetch the newest off-host copy" "scripts/backup.sh fetch failed:"
    tail -12 "$WORK/fetch.txt" | sed 's/^/          /'
    printf '\n%d check(s) failed\n' "$failures"
    exit 1
fi

dump_bytes=$(wc -c <"$STAGING/$fetched" 2>/dev/null | tr -d ' ')

if [ "$fetched.enc" = "$second" ] && [ "${dump_bytes:-0}" -ge 1024 ]; then
    pass "fetched and decrypted the newest off-host copy ($second -> $fetched, $dump_bytes bytes)"
else
    fail "fetch returns the newest off-host copy" "wanted '$second', got '$fetched.enc' (${dump_bytes:-0} bytes)"
fi

# Into the server the way any restore gets a file: the container side stays a container path and the
# host side goes through winpath.
MSYS_NO_PATHCONV=1 docker cp "$(winpath "$STAGING/$fetched")" "$SERVER:/tmp/restore.dump" >/dev/null \
    || fail "the fetched copy reaches the server" "docker cp failed"

# A different database on the same server: the restore has to recreate the schema rather than
# inherit it from the source, and the source stays intact for comparison.
if ! docker exec "$SERVER" psql -U "$DB_USER" -d postgres -v ON_ERROR_STOP=1 \
    -c "CREATE DATABASE \"$SCRATCH_DB\"" >/dev/null; then
    fail "scratch database" "CREATE DATABASE $SCRATCH_DB failed"
fi

# /tmp/restore.dump is the file fetched from the destination above, not one made here.
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
