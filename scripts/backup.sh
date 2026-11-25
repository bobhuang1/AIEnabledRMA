#!/usr/bin/env bash
#
# One database backup, written to a destination outside this host, with retention.
#
# `pg_dump` succeeding says the source could be read. It says nothing about where the file went: a
# dump written into the database container, or into a directory on the host that holds the
# database's own volume, survives exactly the failures a backup exists for -- the disk, the host,
# the account. This script is the copy that leaves: it dumps through the pack's own PostgreSQL
# image, checks the file with `pg_restore --list`, encrypts it to the age identity beside
# rclone.conf, copies it to BACKUP_REMOTE through rclone, and deletes what is past the window on both
# sides.
#
# The encryption is not optional. A dump is every return, address and payment reference in the
# database, and a destination -- a bucket, a backup host, a mounted disk -- is read by everyone its
# own access control admits, which is a different set of people from the ones allowed to read this
# database. The object that leaves is ciphertext and nothing else.
#
# The cipher is age (https://age-encryption.org/v1), encrypting to the X25519 identity beside
# rclone.conf. It replaced `openssl enc -aes-256-cbc`, which had no authentication and no version
# identifier: a CBC object is malleable at the destination, the only thing that noticed was a
# pg_dump magic check at the other end, and a file whose job is to still open in ten years carried
# nothing that says what wrote it. age authenticates its payload (ChaCha20-Poly1305 over a key
# derived through X25519 and HKDF), so a wrong identity or a damaged object fails rather than
# decrypting to something padding-shaped, and its header names the format version. Objects written
# before the change stay restorable until retention deletes the last of them -- see decrypt().
#
# Modes:
#   backup   dump, verify, encrypt, upload, prune (default)
#   fetch    copy the newest dump back from BACKUP_REMOTE, decrypt it, print its name
#   list     print the object names at BACKUP_REMOTE, oldest first
#   purge-plaintext
#            list the unencrypted *.dump objects an earlier version left at BACKUP_REMOTE and delete
#            them. Retention would age them out eventually; this removes them now, and only them. It
#            needs no key, because it deletes rather than opens: a host that has lost its passphrase
#            still has to be able to take the plaintext copies off the destination.
#
# Environment:
#   BACKUP_REMOTE       required. An rclone destination outside this host:
#                       s3:bucket/prefix, b2:bucket/prefix, sftp:host:/path, local:/mnt/offsite/rma
#   BACKUP_KEEP_DAYS    days of dumps to keep, default 14
#   BACKUP_STAGING      local working copy, default /var/backups/aienabledrma
#   COMPOSE_FILE        the pack that holds the database service, default docker-compose.prod.yml
#   ENV_FILE            its env file, default prod.env
#   RCLONE_CONFIG_DIR   directory holding rclone.conf, default /etc/aienabledrma
#   BACKUP_AGE_IDENTITY the age X25519 identity that encrypts every dump, default
#                       $RCLONE_CONFIG_DIR/backup.age.key -- beside rclone.conf, because the two
#                       are the credentials that open the destination and neither belongs in prod.env
#   BACKUP_KEY_FILE     the passphrase of the objects written before the migration to age, default
#                       $RCLONE_CONFIG_DIR/backup.passphrase. Read only when one of those is opened,
#                       so the nightly unit does not depend on a file that is being retired; it can
#                       go once retention has deleted the last object older than the window.
#   RCLONE_IMAGE        default the pinned rclone image below
#
# Requirements: docker, and `age` with `age-keygen` for the cipher (Debian and Ubuntu package them
# as `age`). PostgreSQL and rclone are run as containers, so the host needs neither a database
# client nor rclone of its own -- the same reason the pack's other scheduled job calls compose
# instead of a database client. age is the exception: it is a few hundred kilobytes with no
# dependencies, where pulling a further image onto a production host for one command is a dependency
# with its own upgrade schedule. openssl is needed only while an object from before the migration is
# still inside the window, and only to open it.

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
BACKUP_AGE_IDENTITY=${BACKUP_AGE_IDENTITY:-$RCLONE_CONFIG_DIR/backup.age.key}
# The passphrase the objects written before the migration were encrypted with, beside the identity
# for the same reason. Only read when one of those objects is opened, so retiring it cannot stop the
# nightly run -- only the fetch of a copy older than the migration.
BACKUP_KEY_FILE=${BACKUP_KEY_FILE:-$RCLONE_CONFIG_DIR/backup.passphrase}

say() { printf '%s\n' "$*" >&2; }

usage() {
    cat >&2 <<'EOF'
usage: scripts/backup.sh [backup|fetch|list|purge-plaintext]

  backup  dump the database, encrypt the dump, copy it to BACKUP_REMOTE and prune the window
  fetch   copy the newest object back from BACKUP_REMOTE, decrypt it, and print its name
  list    print the object names at BACKUP_REMOTE, oldest first
  purge-plaintext
          list the unencrypted *.dump objects an earlier version left at BACKUP_REMOTE and delete
          them, leaving every *.dump.enc alone

BACKUP_REMOTE must name an rclone destination outside this host, for example
s3:rma-prod-backups/postgres, sftp:backup.example.com:/srv/rma, or local:/mnt/offsite/rma.

BACKUP_AGE_IDENTITY must name the age identity, by default beside rclone.conf. Without it nothing is
uploaded: the destination holds ciphertext, and a dump with no key is a bucket of noise.

BACKUP_KEY_FILE names the passphrase of the objects written before the migration to age. It is only
read when one of those is fetched, and it can be retired once retention has aged them out.
EOF
}

case "$MODE" in
    backup|fetch|list|purge-plaintext) ;;
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

# A path as a host binary sees it. Docker needs a Windows path for a bind mount under Git Bash, and so
# does `age`, which is a native executable there and cannot open '/c/...'; a Linux host has nothing to
# convert, so this is the path itself. Used at every point a path is handed to age, age-keygen or
# openssl, and never for rclone, whose paths are the container's.
winpath() {
    if command -v cygpath >/dev/null 2>&1; then
        cygpath -w "$1"
    else
        printf '%s' "$1"
    fi
}

# The purge is the one mode that opens nothing: it lists what is readable at the destination and
# deletes it. Requiring the passphrase there would leave the plaintext in place on exactly the host
# that has lost its key, which is the one that has most to remove.
if [ "$MODE" != purge-plaintext ] && [ "$MODE" != list ]; then
    if [ ! -s "$BACKUP_AGE_IDENTITY" ]; then
        # Uploading unencrypted instead would be the failure nobody notices: the schedule stays green
        # and the bucket holds every return in the database. There is no fallback.
        say "backup: BACKUP_AGE_IDENTITY ($BACKUP_AGE_IDENTITY) is missing or empty. Dumps are customer"
        say "        data and are encrypted before they leave; create an X25519 identity beside"
        say "        rclone.conf and keep a copy of it somewhere other than the destination:"
        say "        age-keygen -o $BACKUP_AGE_IDENTITY && chmod 600 $BACKUP_AGE_IDENTITY"
        exit 2
    fi

    command -v age >/dev/null 2>&1 || { say "backup: age is required to encrypt and decrypt the dump"; exit 1; }
    command -v age-keygen >/dev/null 2>&1 || { say "backup: age-keygen is required to read $BACKUP_AGE_IDENTITY"; exit 1; }

    # The recipient is derived from the identity rather than kept in a second file: a recipient file
    # that drifted from the key beside it is a dump nobody can open, and it would fail at the restore
    # rather than here. An identity age cannot parse fails now, at 01:20, and not after the upload.
    if ! age_recipient=$(age-keygen -y "$(winpath "$BACKUP_AGE_IDENTITY")" 2>/dev/null) || [ -z "$age_recipient" ]; then
        say "backup: $BACKUP_AGE_IDENTITY does not hold an age identity; nothing was done"
        exit 2
    fi
fi

command -v docker >/dev/null 2>&1 || { say "backup: docker is required"; exit 1; }

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

# The cipher. Everything this script writes is age v1, encrypted to the recipient of the identity
# beside rclone.conf.
#
# The identity is the recipient's other half, so the key never has to be typed, passed in an
# environment variable, or written into an argument list where `ps` would show it. age does not
# encrypt symmetrically to a passphrase without asking for one interactively (its `-p` mode is
# designed not to be scriptable), which is the reason the key is a file rather than a string.
encrypt() {
    age --encrypt --recipient "$age_recipient"
}

# Objects written before this script moved to age are AES-256-CBC with PBKDF2 and a per-run salt, and
# they stay restorable until retention deletes the last of them: no copy at the destination can be
# older than BACKUP_KEEP_DAYS, so the branch has an end date rather than an owner.
#
# The two formats are told apart by content, not by name -- both are *.dump.enc, and an operator
# fetching last week's copy should not have to know which one it is. `openssl enc -salt` writes the
# eight literal bytes "Salted__" first; age writes its own header there instead.
decrypt() {
    local source=$1

    if [ "$(head -c 8 "$source")" != 'Salted__' ]; then
        age --decrypt --identity "$(winpath "$BACKUP_AGE_IDENTITY")" "$(winpath "$source")"
        return
    fi

    if [ ! -s "$BACKUP_KEY_FILE" ]; then
        # Refusing beats guessing. The object is a legacy one, and the identity that opens everything
        # written since does not open it -- saying so is the difference between a restore that is
        # missing one file and a restore that quietly produces garbage.
        say "backup: $source was written before the migration to age, and opening it needs the"
        say "        passphrase in $BACKUP_KEY_FILE, which is missing or empty. Nothing was staged."
        exit 1
    fi

    command -v openssl >/dev/null 2>&1 || {
        say "backup: openssl is required to open $source, written before the migration to age"
        exit 1
    }

    # tr -d '\r': a passphrase file written by an editor on Windows carries a CR, and a passphrase
    # that silently ends in one is a different key from the same characters typed on the host.
    legacy_passphrase=$(head -n 1 "$BACKUP_KEY_FILE" | tr -d '\r')

    # In the environment rather than on the command line, where `ps` would show it to every account
    # on the host.
    BACKUP_PASSPHRASE=$legacy_passphrase openssl enc -d -aes-256-cbc -pbkdf2 -iter 200000 \
        -pass env:BACKUP_PASSPHRASE -in "$(winpath "$source")"
}

# The umask above applies when this creates the directory; a staging directory that already existed
# - made by an operator, or by an older version of this script - can be 0755, and the plaintext dump
# lives in it for the seconds between pg_dump and the cipher. Restricted here rather than assumed.
if ! mkdir -p "$BACKUP_STAGING" || ! chmod 700 "$BACKUP_STAGING"; then
    say "backup: $BACKUP_STAGING cannot be created or restricted to mode 700. An unencrypted dump"
    say "        would be readable by every account on this host while it exists; refusing to run."
    exit 1
fi

case "$MODE" in
    list)
        rclone lsf "$REMOTE" --files-only --include "*.dump.enc" | sort
        ;;

    purge-plaintext)
        # Objects an earlier version uploaded before the dump was encrypted. They are the exposure
        # the encryption exists to end, and retention only ages them out: until the window closes,
        # every return, address and payment reference in the database sits readable at the
        # destination, for everyone its access policy admits. This removes them now -- and only
        # them, because the pattern is the plaintext name and no object ending .dump.enc matches it.
        #
        # Listed and printed before deleting: a delete that says nothing leaves an operator unable to
        # tell one stale object from a destination nobody ever purged.
        plaintext=$(rclone lsf "$REMOTE" --files-only --include "*.dump" | sort)

        if [ -z "$plaintext" ]; then
            say "backup: no *.dump objects at $BACKUP_REMOTE; the destination holds ciphertext only"
            exit 0
        fi

        plaintext_count=$(printf '%s\n' "$plaintext" | wc -l | tr -d ' ')
        say "backup: $plaintext_count unencrypted *.dump object(s) at $BACKUP_REMOTE:"
        printf '%s\n' "$plaintext" | sed 's/^/        /' >&2

        rclone delete "$REMOTE" --include "*.dump"

        # Asked again rather than assumed: the answer is the difference between a purge and a run
        # that printed a list and changed nothing.
        remaining=$(rclone lsf "$REMOTE" --files-only --include "*.dump" | sort)

        if [ -n "$remaining" ]; then
            say "backup: $BACKUP_REMOTE still lists *.dump objects after the purge:"
            printf '%s\n' "$remaining" | sed 's/^/        /' >&2
            exit 1
        fi

        say "backup: deleted $plaintext_count unencrypted *.dump object(s); every *.dump.enc is untouched"
        ;;

    backup)
        name="rma-$(date -u +%Y%m%dT%H%M%SZ).dump.enc"
        file="$BACKUP_STAGING/$name"
        # The plaintext exists only between the dump and the encryption, and every way out of here,
        # including the failures, deletes it.
        plain="$BACKUP_STAGING/${name%.enc}"

        # The name is fixed to the second, and `rclone copyto` overwrites: a second run inside the
        # same second would replace the copy the first one published, which is a night's backup lost
        # quietly. The drill waits two seconds between its runs for the same reason, and this is the
        # check that makes the wait a convenience rather than a requirement.
        if [ -e "$file" ] || [ -e "$plain" ]; then
            say "backup: $name is already in $BACKUP_STAGING; two runs in the same second would write"
            say "        the same object. Nothing was dumped."
            exit 1
        fi

        say "backup: dumping into ${name%.enc} through docker compose exec postgres in $COMPOSE_FILE, encrypting to $age_recipient"

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
        if ! decrypt "$file" | db pg_restore --list >/dev/null 2>&1; then
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

        # The object is ciphertext; a restore needs the dump inside it. age fails on a wrong identity
        # and on a damaged file -- its payload is authenticated, so this is the key doing the work
        # rather than a promise about it -- and for an object from before the migration, openssl does
        # the same job with the passphrase.
        plain=${latest%.enc}

        if ! decrypt "$BACKUP_STAGING/$latest" >"$BACKUP_STAGING/$plain"; then
            rm -f "$BACKUP_STAGING/$plain"
            say "backup: $latest did not open with $BACKUP_AGE_IDENTITY (wrong identity, or the object"
            say "        is damaged or was tampered with); nothing was staged"
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
