#!/usr/bin/env bash
#
# One database backup, written to a destination outside this host, with retention.
#
# `pg_dump` succeeding says the source could be read. It says nothing about where the file went: a
# dump written into the database container, or into a directory on the host that holds the
# database's own volume, survives exactly the failures a backup exists for -- the disk, the host,
# the account. This script is the copy that leaves: it dumps through the pack's own PostgreSQL
# image, checks the file with `pg_restore --list`, encrypts it with the passphrase beside
# rclone.conf, copies it to BACKUP_REMOTE through rclone, and deletes what is past the window on both
# sides.
#
# The encryption is not optional. A dump is every return, address and payment reference in the
# database, and a destination -- a bucket, a backup host, a mounted disk -- is read by everyone its
# own access control admits, which is a different set of people from the ones allowed to read this
# database. The object that leaves is ciphertext and nothing else.
#
# Modes:
#   backup   dump, verify, encrypt, upload, prune (default)
#   fetch    copy the newest dump back from BACKUP_REMOTE, decrypt it, print its name
#   list     print the object names at BACKUP_REMOTE, oldest first
#
# Environment:
#   BACKUP_REMOTE       required. An rclone destination outside this host:
#                       s3:bucket/prefix, b2:bucket/prefix, sftp:host:/path, local:/mnt/offsite/rma
#   BACKUP_KEEP_DAYS    days of dumps to keep, default 14
#   BACKUP_STAGING      local working copy, default /var/backups/aienabledrma
#   COMPOSE_FILE        the pack that holds the database service, default docker-compose.prod.yml
#   ENV_FILE            its env file, default prod.env
#   RCLONE_CONFIG_DIR   directory holding rclone.conf, default /etc/aienabledrma
#   BACKUP_KEY_FILE     the passphrase that encrypts every dump, default
#                       $RCLONE_CONFIG_DIR/backup.passphrase -- beside rclone.conf, because the two
#                       are the credentials that open the destination and neither belongs in prod.env
#   RCLONE_IMAGE        default the pinned rclone image below
#
# Requirements: docker, and openssl for the cipher. PostgreSQL and rclone are run as containers, so
# the host needs neither a database client nor rclone of its own -- the same reason the pack's other
# scheduled job calls compose instead of a database client. openssl is the exception: it is on every
# Debian and Ubuntu base image, while the pinned images the pack runs carry no cipher of their own,
# and a further image pulled onto a production host for one command is a dependency with its own
# upgrade schedule.

set -euo pipefail

# Git Bash rewrites an argument that starts with a slash into a Windows path. Every path below is
# the container's (/dumps, /offhost, /config), so the rewriting has to be off for the whole script.
export MSYS_NO_PATHCONV=1

REPO_ROOT=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
cd "$REPO_ROOT"

# The staged copy is the plaintext dump for the seconds it exists and the ciphertext afterwards.
# 077 keeps both from being readable by every account on the host.
umask 077

MODE=${1:-backup}

BACKUP_REMOTE=${BACKUP_REMOTE:-}
BACKUP_KEEP_DAYS=${BACKUP_KEEP_DAYS:-14}
BACKUP_STAGING=${BACKUP_STAGING:-/var/backups/aienabledrma}
COMPOSE_FILE=${COMPOSE_FILE:-docker-compose.prod.yml}
ENV_FILE=${ENV_FILE:-prod.env}
RCLONE_CONFIG_DIR=${RCLONE_CONFIG_DIR:-/etc/aienabledrma}
# Pinned by digest like every other image the pack runs: a tag is whatever was published last.
RCLONE_IMAGE=${RCLONE_IMAGE:-rclone/rclone:1.68.1@sha256:e78b765d51301e1934c376e9b5d0b42a156352099502a812b5e3b3bd37ccb33b}
# Beside rclone.conf, not in prod.env: prod.env is the file an operator copies around to say what a
# deployment is configured with, and it is handed to every container. The key must not travel with
# the objects it opens.
BACKUP_KEY_FILE=${BACKUP_KEY_FILE:-$RCLONE_CONFIG_DIR/backup.passphrase}

say() { printf '%s\n' "$*" >&2; }

usage() {
    cat >&2 <<'EOF'
usage: scripts/backup.sh [backup|fetch|list]

  backup  dump the database, encrypt the dump, copy it to BACKUP_REMOTE and prune the window
  fetch   copy the newest object back from BACKUP_REMOTE, decrypt it, and print its name
  list    print the object names at BACKUP_REMOTE, oldest first

BACKUP_REMOTE must name an rclone destination outside this host, for example
s3:rma-prod-backups/postgres, sftp:backup.example.com:/srv/rma, or local:/mnt/offsite/rma.

BACKUP_KEY_FILE must name the passphrase file, by default beside rclone.conf. Without it nothing is
uploaded: the destination holds ciphertext, and a dump with no key is a bucket of noise.
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

if [ ! -s "$BACKUP_KEY_FILE" ]; then
    # Uploading unencrypted instead would be the failure nobody notices: the schedule stays green
    # and the bucket holds every return in the database. There is no fallback.
    say "backup: BACKUP_KEY_FILE ($BACKUP_KEY_FILE) is missing or empty. Dumps are customer data and"
    say "        are encrypted before they leave; create the passphrase beside rclone.conf:"
    say "        openssl rand -base64 48 > $BACKUP_KEY_FILE"
    exit 2
fi

backup_passphrase=$(head -n 1 "$BACKUP_KEY_FILE")

if [ "${#backup_passphrase}" -lt 16 ]; then
    # The object is readable by anyone the destination admits, so a guessable key is no encryption.
    say "backup: the passphrase in $BACKUP_KEY_FILE is shorter than 16 characters; refusing to use it"
    exit 2
fi

command -v docker >/dev/null 2>&1 || { say "backup: docker is required"; exit 1; }
command -v openssl >/dev/null 2>&1 || { say "backup: openssl is required to encrypt the dump"; exit 1; }

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
# are already the environment and the server has no published port. `postgres` is the service name
# docker-compose.prod.yml gives the database, and a test reads both files so a rename in either one
# fails the gate rather than the 01:20 backup with "no such service".
#
# This is the only route to the database, deliberately: an alternative for a container the pack does
# not manage would be a second branch that only the drill ever called, which is how the branch the
# nightly unit actually takes went unexercised in the first place.
db() {
    docker compose -f "$COMPOSE_FILE" --env-file "$ENV_FILE" exec -T postgres "$@"
}

# The cipher. AES-256-CBC with PBKDF2 key derivation (200k iterations) and a random salt per run is
# what openssl gives without adding an image; the pinned rclone image carries no cipher, and the
# pack's PostgreSQL image is there to dump, not to be a toolbox.
#
# The passphrase reaches openssl in an environment variable rather than on its command line, where
# `ps` would show it to every account on the host.
encrypt() {
    BACKUP_PASSPHRASE=$backup_passphrase openssl enc -aes-256-cbc -pbkdf2 -iter 200000 -salt \
        -pass env:BACKUP_PASSPHRASE
}

decrypt() {
    BACKUP_PASSPHRASE=$backup_passphrase openssl enc -d -aes-256-cbc -pbkdf2 -iter 200000 \
        -pass env:BACKUP_PASSPHRASE
}

mkdir -p "$BACKUP_STAGING"

case "$MODE" in
    list)
        rclone lsf "$REMOTE" --files-only --include "*.dump.enc" | sort
        ;;

    backup)
        name="rma-$(date -u +%Y%m%dT%H%M%SZ).dump.enc"
        file="$BACKUP_STAGING/$name"
        # The plaintext exists only between the dump and the encryption, and every way out of here,
        # including the failures, deletes it.
        plain="$BACKUP_STAGING/${name%.enc}"

        say "backup: dumping into ${name%.enc} through docker compose exec postgres in $COMPOSE_FILE"

        # -Fc is the format the operator instructions use (compressed, selective restore,
        # pg_restore-able), and the same format the staged copy is checked in.
        if ! db bash -c 'pg_dump -U "$POSTGRES_USER" -d "$POSTGRES_DB" -Fc' >"$plain"; then
            rm -f "$plain"
            say "backup: pg_dump failed; nothing was uploaded"
            exit 1
        fi

        bytes=$(wc -c <"$plain" | tr -d ' ')

        if [ "${bytes:-0}" -lt 1024 ]; then
            # Uploading this would report a successful backup every night while holding nothing.
            rm -f "$plain"
            say "backup: ${name%.enc} is ${bytes:-0} bytes; refusing to publish it"
            exit 1
        fi

        # A file that `pg_restore` cannot even list is not a dump. Checking here costs a second;
        # finding out during a restore costs the restore.
        if ! db pg_restore --list <"$plain" >/dev/null; then
            rm -f "$plain"
            say "backup: pg_restore cannot read ${name%.enc}; refusing to publish it"
            exit 1
        fi

        if ! encrypt <"$plain" >"$file"; then
            rm -f "$plain" "$file"
            say "backup: could not encrypt $name with $BACKUP_KEY_FILE; nothing was uploaded"
            exit 1
        fi

        rm -f "$plain"

        # What leaves has to be what the key opens, and it has to still be a dump once opened. A
        # ciphertext that stopped being restorable is found here, at 01:20, rather than on the night
        # somebody needs it.
        if ! decrypt <"$file" | db pg_restore --list >/dev/null 2>&1; then
            rm -f "$file"
            say "backup: $name does not decrypt back to a readable dump; refusing to publish it"
            exit 1
        fi

        rclone copyto "/dumps/$name" "$REMOTE/$name"

        # Retention on both sides, in the same run: the remote is the copy that matters, and the
        # local one is a working copy that would otherwise fill the host's disk unnoticed. Plain
        # `*.dump` is in both patterns so the unencrypted objects an earlier version wrote are aged
        # out of the destination instead of staying readable there for ever.
        rclone delete "$REMOTE" --include "*.dump.enc" --include "*.dump" --min-age "${BACKUP_KEEP_DAYS}d"
        find "$BACKUP_STAGING" -maxdepth 1 \( -name '*.dump.enc' -o -name '*.dump' \) \
            -mtime "+$BACKUP_KEEP_DAYS" -delete

        uploaded=$(wc -c <"$file" | tr -d ' ')
        say "backup: $name ($uploaded bytes, encrypted) is at $BACKUP_REMOTE, keeping $BACKUP_KEEP_DAYS days"
        ;;

    fetch)
        # Names sort by time because the timestamp is fixed-width and UTC: the newest object is the
        # last one by name, not the first line an object store happens to return.
        latest=$(rclone lsf "$REMOTE" --files-only --include "*.dump.enc" | sort | tail -n 1)

        if [ -z "$latest" ]; then
            say "backup: no *.dump.enc at $BACKUP_REMOTE; there is nothing to restore"
            exit 1
        fi

        rclone copyto "$REMOTE/$latest" "/dumps/$latest"

        if [ ! -s "$BACKUP_STAGING/$latest" ]; then
            say "backup: $latest was listed at $BACKUP_REMOTE but did not arrive"
            exit 1
        fi

        # The object is ciphertext; a restore needs the dump inside it. openssl fails on a wrong
        # passphrase and on a damaged file, so this is the key doing the work rather than a promise
        # about it.
        plain=${latest%.enc}

        if ! decrypt <"$BACKUP_STAGING/$latest" >"$BACKUP_STAGING/$plain"; then
            rm -f "$BACKUP_STAGING/$plain"
            say "backup: $latest did not decrypt with the passphrase in $BACKUP_KEY_FILE (wrong key,"
            say "        or the object is damaged); nothing was staged"
            exit 1
        fi

        # A wrong key can decrypt to valid padding once in a few hundred tries; a pg_dump archive
        # starts with its own magic either way. The database is not asked, because a fetch has to
        # work when the database is the thing that is gone.
        if [ "$(head -c 5 "$BACKUP_STAGING/$plain")" != PGDMP ]; then
            rm -f "$BACKUP_STAGING/$plain"
            say "backup: $latest decrypted to something that is not a pg_dump archive; nothing was staged"
            exit 1
        fi

        rm -f "$BACKUP_STAGING/$latest"

        # The only thing on stdout: a caller that captures the output gets the name it must restore.
        printf '%s\n' "$plain"
        ;;
esac
