#!/usr/bin/env bash
#
# Builds both runtime images and checks each carries what its host reads.
#
# The publish-side tests prove `dotnet publish` carries the Web host's knowledge-base/; this proves
# the image does, which is a different claim. The Docker build sees only what the context and
# .dockerignore allow, and it restores from a hand-written COPY list, so it can differ from a publish
# in both directions and has: the Web image could not be built at all once because a referenced
# manifest was missing from that list, and nothing in a checkout showed it.
#
# The two images are checked differently on purpose. The Web host reads the corpus from its content
# root, so a missing article is a policy answer that quietly stops happening. The tool server's tools
# evaluate stored data and never open knowledge-base/, and the startup check that inspects a corpus is
# registered by AddRag, which that host does not call — so its image must carry none. That absence is
# asserted here rather than left for the next reader to wonder about, because the image used to carry
# the corpus and the check that said so was true and told nobody anything.
#
# Both images carry the commit they were built from as org.opencontainers.image.revision, and that
# label is compared with this checkout. After a fix had been pushed, "is this image from before or
# after it" was a question this repository could not answer from the image.
#
# Usage:
#   scripts/verify-images.sh
#   KEEP_IMAGES=1 scripts/verify-images.sh     # leave the images behind after a passing run
#
# Environment:
#   MCP_IMAGE  tag for the tool server image (default aienabledrma-mcp-http:corpus-check)
#   WEB_IMAGE  tag for the web host image    (default aienabledrma-web:corpus-check)
#   RMA_IMAGE_CACHE=1    push and pull the layer cache through the GitHub Actions cache. Set by CI,
#                        where the tool server's image is also built by the auth job; unset locally,
#                        where there is no Actions cache to talk to.
#   RMA_IMAGE_REVISION   label value to write (default: git rev-parse HEAD, else "unknown")
#   KEEP_IMAGES=1        leave the images behind after a passing run
#
# Requirements: docker with buildx. This does not start a daemon.

set -euo pipefail

MCP_IMAGE=${MCP_IMAGE:-aienabledrma-mcp-http:corpus-check}
WEB_IMAGE=${WEB_IMAGE:-aienabledrma-web:corpus-check}
REVISION=${RMA_IMAGE_REVISION:-$(git rev-parse HEAD 2>/dev/null || echo unknown)}

REPO_ROOT=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
cd "$REPO_ROOT"

failures=0

pass() { printf 'ok    %s\n' "$1"; }

fail() {
    printf 'FAIL  %s\n        %s\n' "$1" "${2:-}"
    failures=$((failures + 1))
}

# The corpus the checkout has, which is what the Web image has to match. README.md is included by the
# glob and ignored by the loader, so it counts on both sides.
expected=$(find knowledge-base -name '*.md' -type f | wc -l | tr -d ' ')

# A build failure is a check failure, not a reason to stop: the interesting case (the context missing
# the corpus, or a referenced manifest missing from the COPY list) fails here, and the check-style
# summary is what a CI log reader looks for. The tail of the build log is printed because "image did
# not build" is not a diagnosis.
build() {
    local name=$1 dockerfile=$2 tag=$3 log args
    log=$(mktemp)

    args=(buildx build --load -f "$dockerfile" -t "$tag" --build-arg "GIT_REVISION=$REVISION")

    if [ "${RMA_IMAGE_CACHE:-0}" = 1 ]; then
        # One scope per image: a Dockerfile change invalidates its own layers, and a rebuild of the
        # tool server in the auth job and here share the compiled SDK stage.
        args+=(--cache-from "type=gha,scope=$name" --cache-to "type=gha,mode=max,scope=$name")
    fi

    if docker "${args[@]}" . >"$log" 2>&1; then
        pass "$name image builds"
        rm -f "$log"
        return 0
    fi

    fail "$name image builds" "docker buildx build failed; last lines of the log:"
    tail -12 "$log" | sed 's/^/          /'
    rm -f "$log"
    return 1
}

# One run per image: prints the article count, whether the manifest is there, and whether the
# directory exists at all. The last of the three is what distinguishes "the corpus was never copied"
# from "the corpus was copied empty", which fail for different reasons.
inspect() {
    docker run --rm --entrypoint sh "$1" -c '
        directory=no
        [ -d /app/knowledge-base ] && directory=yes
        count=0
        if [ "$directory" = yes ]; then
            count=$(find /app/knowledge-base -name "*.md" -type f | wc -l | tr -d " ")
        fi
        manifest=no
        [ -f /app/knowledge-base/REVIEWED.sha256 ] && manifest=yes
        printf "%s %s %s\n" "$count" "$manifest" "$directory"
    ' 2>&1
}

label() {
    docker image inspect -f '{{ index .Config.Labels "org.opencontainers.image.revision" }}' "$1" 2>/dev/null \
        || printf 'unknown'
}

# expectation is "corpus" (the host reads it) or "empty" (nothing in the host reads it).
check_image() {
    local name=$1 dockerfile=$2 tag=$3 expectation=$4 output count manifest directory actual_label

    if ! build "$name" "$dockerfile" "$tag"; then
        return
    fi

    if ! output=$(inspect "$tag"); then
        fail "$name image inspects" "$output"
        return
    fi

    count=${output%% *}
    manifest=$(printf '%s' "$output" | cut -d' ' -f2)
    directory=$(printf '%s' "$output" | cut -d' ' -f3)

    if [ "$expectation" = corpus ]; then
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
    else
        if [ "$directory" = no ]; then
            pass "$name image carries no knowledge-base"
        else
            fail "$name image corpus"                 "/app/knowledge-base is present ($count .md file(s)) but nothing in this host reads it; "                 "the csproj or the Dockerfile is copying a corpus again"
        fi
    fi

    actual_label=$(label "$tag")

    if [ "$actual_label" = "$REVISION" ]; then
        pass "$name image is labelled with $REVISION"
    else
        fail "$name image revision label" "expected '$REVISION', got '$actual_label'"
    fi
}

check_image "MCP" "src/AIEnabledRma.Mcp/Dockerfile" "$MCP_IMAGE" empty
check_image "Web" "src/AIEnabledRma.Web/Dockerfile" "$WEB_IMAGE" corpus

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
