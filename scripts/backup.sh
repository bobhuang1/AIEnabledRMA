#!/usr/bin/env bash
#
# One database backup, written to a destination outside this host, with retention.
#
# `pg_dump` succeeding says the source could be read. It says nothing about where the file went: a
# dump written into the database container, or into a directory on the host that holds the
# database's own volume, survives exactly the failures a backup exists for -- the disk, the host,
# the account. This script is the copy that leaves: it dumps through the pack's own PostgreSQL
# image, checks the file with `pg_restore --list`, copies it to BACKUP_REMOTE through rclone, and
# deletes what is past the window on both sides.
#
# Modes:
#   backup   dump, verify, upload, prune (default)
#   fetch    copy the newest dump from BACKUP_REMOTE into BACKUP_STAGING and print its name
#   list     print the names at BACKUP_REMOTE, oldest first
#
# Environment:
#   BACKUP_REMOTE       required. An rclone destination outside this host:
#                       s3:bucket/prefix, b2:bucket/prefix, sftp:host:/path, local:/mnt/offsite/rma
#   BACKUP_KEEP_DAYS    days of dumps to keep, default 14
#   BACKUP_STAGING      local working copy, default /var/backups/aienabledrma
#   BACKUP_DB_CONTAINER run pg_dump in this container instead of the pack's `postgres` service
#                       (the restore drill points it at its throwaway server)
#   COMPOSE_FILE        default docker-compose.prod.yml
#   ENV_FILE            default prod.env
#   RCLONE_CONFIG_DIR   directory holding rclone.conf, default /etc/aienabledrma
#   RCLONE_IMAGE        default the pinned rclone image below
#
# Requirements: docker. PostgreSQL and rclone are run as containers, so the host needs neither a
# client tool nor rclone of its own -- the same reason the pack's other scheduled job calls compose
# instead of a database client.

set -euo pipefail

# Git Bash rewrites an argument that starts with a slash into a Windows path. Every path below is
# the container's (/dumps, /offhost, /config), so the rewriting has to be off for the whole script.
export MSYS_NO_PATHCONV=1

REPO_ROOT=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
cd "$REPO_ROOT"

MODE=${1:-backup}

BACKUP_REMOTE=${BACKUP_REMOTE:-}
BACKUP_KEEP_DAYS=${BACKUP_KEEP_DAYS:-14}
BACKUP_STAGING=${BACKUP_STAGING:-/var/backups/aienabledrma}
BACKUP_DB_CONTAINER=${BACKUP_DB_CONTAINER:-}
COMPOSE_FILE=${COMPOSE_FILE:-docker-compose.prod.yml}
ENV_FILE=${ENV_FILE:-prod.env}
RCLONE_CONFIG_DIR=${RCLONE_CONFIG_DIR:-/etc/aienabledrma}
# Pinned by digest like every other image the pack runs: a tag is whatever was published last.
RCLONE_IMAGE=${RCLONE_IMAGE:-rclone/rclone:1.68.1@sha256:e78b765d51301e1934c376e9b5d0b42a156352099502a812b5e3b3bd37ccb33b}

say() { printf '%s\n' "$*" >&2; }

usage() {
    cat >&2 <<'EOF'
usage: scripts/backup.sh [backup|fetch|list]

  backup  dump the database, verify the file, copy it to BACKUP_REMOTE and prune the window
  fetch   copy the newest dump back from BACKUP_REMOTE, and print its name
  list    print the dump names at BACKUP_REMOTE, oldest first

BACKUP_REMOTE must name an rclone destination outside this host, for example
s3:rma-prod-backups/postgres, sftp:backup.example.com:/srv/rma, or local:/mnt/offsite/rma.
EOF
}

case "$MODE" in
    backup|fetch|list) ;;
    -h|--help|help) usage; exit 0 ;;
    *) say "backup: unknown mode '$MODE'"; usage; exit 2 ;;
esac

case "$BACKUP_KEEP_DAYS" in
    ''|*[!0-9]*)
        say "backup: BACKUP_KEEP_DAYS must be a whole number of days, got '$BACKUP_KEEP_DAYS'"
        exit 2
        ;;
esac

if [ "$BACKUP_KEEP_DAYS" -lt 1 ]; then
    # Zero is not a short window, it is a backup kept until the next run.
    say "backup: BACKUP_KEEP_DAYS must be at least 1, got '$BACKUP_KEEP_DAYS'"
    exit 2
fi

if [ -z "$BACKUP_REMOTE" ]; then
    say "backup: BACKUP_REMOTE is not set. A dump that stays on the host holding the database is" \
        "not a backup; name the destination, e.g. s3:rma-prod-backups/postgres or" \
        "sftp:backup.example.com:/srv/rma."
    exit 2
fi

command -v docker >/dev/null 2>&1 || { say "backup: docker is required"; exit 1; }

# Docker needs a Windows path for a bind mount under Git Bash; a Linux host has nothing to convert.
winpath() {
    if command -v cygpath >/dev/null 2>&1; then
        cygpath -w "$1"
    else
        printf '%s' "$1"
    fi
}

# The remote as rclone sees it inside its container. `local:` is mounted under a fixed path, because
# the container cannot see this host's filesystem otherwise -- that is the mounted-disk and NFS case,
# and the one the restore drill uses to run this script without credentials.
RCLONE_MOUNTS=()
REMOTE=$BACKUP_REMOTE

case "$BACKUP_REMOTE" in
    local:*)
        offsite_host_path=${BACKUP_REMOTE#local:}

        if [ -z "$offsite_host_path" ]; then
            say "backup: local: needs a directory, e.g. local:/mnt/offsite/rma"
            exit 2
        fi

        mkdir -p "$offsite_host_path"
        RCLONE_MOUNTS+=(-v "$(winpath "$offsite_host_path"):/offhost")
        # The on-the-fly form. A named `local:` remote needs a [local] section in rclone.conf, and a
        # mounted disk whose only requirement is that the host wrote a config file for it is a
        # dependency that fails on the first night -- with "didn't find section in config file".
        REMOTE=:local:/offhost
        ;;
    *:*)
        ;;
    *)
        say "backup: BACKUP_REMOTE must name an rclone remote (s3:bucket/prefix," \
            "sftp:host:/path, local:/path), got '$BACKUP_REMOTE'. A bare path would be read as a" \
            "remote named after its first component."
        exit 2
        ;;
esac

RCLONE_CONFIG_MOUNTS=()
RCLONE_CONFIG_FILE=/dev/null

if [ -f "$RCLONE_CONFIG_DIR/rclone.conf" ]; then
    RCLONE_CONFIG_MOUNTS+=(-v "$(winpath "$RCLONE_CONFIG_DIR/rclone.conf"):/config/rclone.conf:ro")
    RCLONE_CONFIG_FILE=/config/rclone.conf
fi

# One place that knows how rclone is invoked, so `backup`, `fetch` and `list` cannot disagree about
# which config or which mount they used.
rclone() {
    docker run --rm \
        -v "$(winpath "$BACKUP_STAGING"):/dumps" \
        ${RCLONE_CONFIG_MOUNTS[@]+"${RCLONE_CONFIG_MOUNTS[@]}"} \
        ${RCLONE_MOUNTS[@]+"${RCLONE_MOUNTS[@]}"} \
        -e "RCLONE_CONFIG=$RCLONE_CONFIG_FILE" \
        "$RCLONE_IMAGE" "$@"
}

# The database, reached the way the pack reaches it: inside its own container, where the credentials
# are already the environment and the server has no published port. BACKUP_DB_CONTAINER is the same
# question asked of a container the pack does not manage.
db() {
    if [ -n "$BACKUP_DB_CONTAINER" ]; then
        docker exec -i "$BACKUP_DB_CONTAINER" "$@"
    else
        docker compose -f "$COMPOSE_FILE" --env-file "$ENV_FILE" exec -T postgres "$@"
    fi
}

mkdir -p "$BACKUP_STAGING"

case "$MODE" in
    list)
        rclone lsf "$REMOTE" --files-only --include "*.dump" | sort
        ;;

    backup)
        name="rma-$(date -u +%Y%m%dT%H%M%SZ).dump"
        file="$BACKUP_STAGING/$name"

        say "backup: dumping into $name"

        # -Fc is the format the operator instructions use (compressed, selective restore,
        # pg_restore-able), and the same format the staged copy is checked in.
        if ! db bash -c 'pg_dump -U "$POSTGRES_USER" -d "$POSTGRES_DB" -Fc' >"$file"; then
            rm -f "$file"
            say "backup: pg_dump failed; nothing was uploaded"
            exit 1
        fi

        bytes=$(wc -c <"$file" | tr -d ' ')

        if [ "${bytes:-0}" -lt 1024 ]; then
            # Uploading this would report a successful backup every night while holding nothing.
            rm -f "$file"
            say "backup: $name is ${bytes:-0} bytes; refusing to publish it"
            exit 1
        fi

        # A file that `pg_restore` cannot even list is not a dump. Checking here costs a second;
        # finding out during a restore costs the restore.
        if ! db pg_restore --list <"$file" >/dev/null; then
            rm -f "$file"
            say "backup: pg_restore cannot read $name; refusing to publish it"
            exit 1
        fi

        rclone copyto "/dumps/$name" "$REMOTE/$name"

        # Retention on both sides, in the same run: the remote is the copy that matters, and the
        # local one is a working copy that would otherwise fill the host's disk unnoticed.
        rclone delete "$REMOTE" --include "*.dump" --min-age "${BACKUP_KEEP_DAYS}d"
        find "$BACKUP_STAGING" -maxdepth 1 -name '*.dump' -mtime "+$BACKUP_KEEP_DAYS" -delete

        say "backup: $name ($bytes bytes) is at $BACKUP_REMOTE, keeping $BACKUP_KEEP_DAYS days"
        ;;

    fetch)
        # Names sort by time because the timestamp is fixed-width and UTC: the newest object is the
        # last one by name, not the first line an object store happens to return.
        latest=$(rclone lsf "$REMOTE" --files-only --include "*.dump" | sort | tail -n 1)

        if [ -z "$latest" ]; then
            say "backup: no *.dump at $BACKUP_REMOTE; there is nothing to restore"
            exit 1
        fi

        rclone copyto "$REMOTE/$latest" "/dumps/$latest"

        if [ ! -s "$BACKUP_STAGING/$latest" ]; then
            say "backup: $latest was listed at $BACKUP_REMOTE but did not arrive"
            exit 1
        fi

        # The only thing on stdout: a caller that captures the output gets the name it must restore.
        printf '%s\n' "$latest"
        ;;
esac
