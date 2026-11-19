#!/usr/bin/env bash
#
# Builds both runtime images and checks the corpus is inside them.
#
# The publish-side tests prove `dotnet publish` carries knowledge-base/; this proves the image does,
# which is a different claim. The Docker build sees only what the context and .dockerignore allow,
# and it restores from a hand-written COPY list, so it can differ from a publish in both directions
# and has: the tool server published no corpus items at all, and the Web image could not be built
# because a referenced manifest was missing. Neither shows up when publishing from a checkout.
#
# Usage:
#   scripts/verify-images.sh            # build both, check both
#   KEEP_IMAGES=1 scripts/verify-images.sh
#
# Environment:
#   MCP_IMAGE  tag for the tool server image  (default aienabledrma-mcp-http:corpus-check)
#   WEB_IMAGE  tag for the web host image     (default aienabledrma-web:corpus-check)
#   KEEP_IMAGES=1  leave the images behind after a passing run
#
# Requirements: docker with buildx. This does not start a daemon.

set -euo pipefail

MCP_IMAGE=${MCP_IMAGE:-aienabledrma-mcp-http:corpus-check}
WEB_IMAGE=${WEB_IMAGE:-aienabledrma-web:corpus-check}

REPO_ROOT=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
cd "$REPO_ROOT"

failures=0

pass() { printf 'ok    %s\n' "$1"; }

fail() {
    printf 'FAIL  %s\n        %s\n' "$1" "${2:-}"
    failures=$((failures + 1))
}

# The corpus the checkout has, which is what an image has to match. README.md is included by the glob
# and ignored by the loader, so it counts on both sides.
expected=$(find knowledge-base -name '*.md' -type f | wc -l | tr -d ' ')

# A build failure is a check failure, not a reason to stop: the interesting case (the context missing
# the corpus, or a referenced manifest missing from the COPY list) fails here, and the check-style
# summary is what a CI log reader looks for. The tail of the build log is printed because "image did
# not build" is not a diagnosis.
build() {
    local name=$1 dockerfile=$2 tag=$3 log
    log=$(mktemp)

    if docker build -f "$dockerfile" -t "$tag" . >"$log" 2>&1; then
        pass "$name image builds"
        rm -f "$log"
        return 0
    fi

    fail "$name image builds" "docker build failed; last lines of the log:"
    tail -12 "$log" | sed 's/^/          /'
    rm -f "$log"
    return 1
}

# One run per image: prints the article count and whether the manifest is there, because an image can
# carry the articles and not the manifest (and a host that requires provenance then refuses to start,
# which reads as a configuration problem rather than a missing file).
inspect() {
    docker run --rm --entrypoint sh "$1" -c '
        if [ -d /app/knowledge-base ]; then
            printf "%s %s\n" "$(find /app/knowledge-base -name "*.md" -type f | wc -l | tr -d " ")" \
                "$(test -f /app/knowledge-base/REVIEWED.sha256 && echo yes || echo no)"
        else
            printf "0 no\n"
        fi
    '
}

check_image() {
    local name=$1 dockerfile=$2 tag=$3 output count manifest

    if ! build "$name" "$dockerfile" "$tag"; then
        return
    fi

    if ! output=$(inspect "$tag" 2>&1); then
        fail "$name image inspects" "$output"
        return
    fi

    count=${output%% *}
    manifest=${output##* }

    if [ "$count" = "$expected" ] && [ "$count" != 0 ]; then
        pass "$name image carries all $expected articles"
    else
        fail "$name image corpus" "expected $expected .md files under /app/knowledge-base, found $count"
    fi

    if [ "$manifest" = yes ]; then
        pass "$name image carries REVIEWED.sha256"
    else
        fail "$name image manifest" "/app/knowledge-base/REVIEWED.sha256 is missing"
    fi
}

check_image "MCP" "src/AIEnabledRma.Mcp/Dockerfile" "$MCP_IMAGE"
check_image "Web" "src/AIEnabledRma.Web/Dockerfile" "$WEB_IMAGE"

if [ "${KEEP_IMAGES:-0}" != 1 ] && [ "$failures" -eq 0 ]; then
    docker rmi "$MCP_IMAGE" "$WEB_IMAGE" >/dev/null 2>&1 || true
fi

if [ "$failures" -ne 0 ]; then
    echo
    echo "$failures check(s) failed"
    exit 1
fi

echo
echo "all checks passed"
