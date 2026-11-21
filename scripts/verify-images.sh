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
# The RAG image joined this script with the production pack: the host that reads the corpus most
# directly was the one image no gate looked inside. The other two are checked differently on purpose. The Web host reads the corpus from its content
# root, so a missing article is a policy answer that quietly stops happening. The tool server's tools
# evaluate stored data and never open knowledge-base/, and the startup check that inspects a corpus is
# registered by AddRag, which that host does not call — so its image must carry none. That absence is
# asserted here rather than left for the next reader to wonder about, because the image used to carry
# the corpus and the check that said so was true and told nobody anything.
#
# All three images carry the commit they were built from as org.opencontainers.image.revision, and
# that label is compared with this checkout. After a fix had been pushed, "is this image from before or
# after it" was a question this repository could not answer from the image.
#
# Usage:
#   scripts/verify-images.sh
#   KEEP_IMAGES=1 scripts/verify-images.sh     # leave the images behind after a passing run
#
# Environment:
#   MCP_IMAGE  tag for the tool server image (default aienabledrma-mcp-http:corpus-check)
#   WEB_IMAGE  tag for the web host image    (default aienabledrma-web:corpus-check)
#   RAG_IMAGE  tag for the RAG host image    (default aienabledrma-rag:corpus-check)
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
RAG_IMAGE=${RAG_IMAGE:-aienabledrma-rag:corpus-check}
REVISION=${RMA_IMAGE_REVISION:-$(git rev-parse HEAD 2>/dev/null || echo unknown)}
PG_IMAGE=${PG_IMAGE:-pgvector/pgvector:pg16}

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

# The host loads the manifest's articles, one per line, and README.md is not one of them. Counting the
# glob here instead would compare the image against the wrong number and then write the wrong number
# into the assertion.
expected_articles=$(wc -l < knowledge-base/REVIEWED.sha256 | tr -d ' ')

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

# The file counts above prove what the COPY list shipped; they do not prove the host can load it. A
# corpus that is present and unreadable, or present and drifted from its manifest, counts the same
# and answers every question as though the policy said nothing. This boots the web image through its
# own entrypoint in Production — the environment where the startup check refuses anything but the
# reviewed corpus — and reads the count the host itself logs.
#
# The database is a throwaway PostgreSQL built from the image the compose file uses, on its own
# network and with a password that is not one of the development defaults Production refuses. The host
# does not query it during startup, but a smoke test that boots without its configuration would keep
# passing after that configuration stopped resolving.
SMOKE_NETWORK=rma-image-smoke
SMOKE_PG=rma-image-smoke-pg
SMOKE_WEB=rma-image-smoke-web
SMOKE_PASSWORD=image-smoke-not-a-default

smoke_environment=(
    -e ASPNETCORE_ENVIRONMENT=Production
    -e "ConnectionStrings__Rma=Host=$SMOKE_PG;Port=5432;Database=rmadev;Username=rmauser;Password=$SMOKE_PASSWORD"
    -e Rag__Admin__ApiKey=image-smoke-operator-key
    -e Payments__Fake__AllowInProduction=true
)

smoke_cleanup() {
    docker rm -f "$SMOKE_WEB" "$SMOKE_PG" >/dev/null 2>&1 || true
    docker network rm "$SMOKE_NETWORK" >/dev/null 2>&1 || true
}

trap smoke_cleanup EXIT

boot_smoke() {
    local tag=$1 log rc waited=0 ready=no

    smoke_cleanup
    docker network create "$SMOKE_NETWORK" >/dev/null 2>&1 || true

    if ! docker run -d --name "$SMOKE_PG" --network "$SMOKE_NETWORK"             -e POSTGRES_USER=rmauser             -e POSTGRES_PASSWORD="$SMOKE_PASSWORD"             -e POSTGRES_DB=rmadev             "$PG_IMAGE" >/dev/null; then
        fail "Web image boots on its corpus" "the smoke database container would not start"
        smoke_cleanup
        return
    fi

    # Ready is the server's own answer, and a container that died is not asked for sixty seconds.
    for _ in $(seq 1 60); do
        if docker exec "$SMOKE_PG" pg_isready -U rmauser -d rmadev >/dev/null 2>&1; then
            ready=yes
            break
        fi

        if [ "$(docker inspect -f '{{.State.Running}}' "$SMOKE_PG" 2>/dev/null || echo false)" != true ]; then
            break
        fi

        sleep 1
    done

    if [ "$ready" != yes ]; then
        fail "Web image boots on its corpus"             "the smoke database did not become ready; last lines of its log:"
        docker logs "$SMOKE_PG" 2>&1 | tail -8 | sed 's/^/          /'
        smoke_cleanup
        return
    fi

    # Started detached so the log can be read while the host is still running: a host that dies after
    # printing the line is not a boot, and a host that hangs before printing it fails the wait below.
    if ! docker run -d --name "$SMOKE_WEB" --network "$SMOKE_NETWORK"             "${smoke_environment[@]}" "$tag" >/dev/null; then
        fail "Web image boots on its corpus" "docker run refused to start the web image"
        smoke_cleanup
        return
    fi

    while [ "$waited" -lt 60 ]; do
        log=$(docker logs "$SMOKE_WEB" 2>&1 || true)

        if printf '%s' "$log" | grep -Fq "$expected_articles article(s)"                 && printf '%s' "$log" | grep -Fq "0 problem(s)"; then
            pass "Web image boots with $expected_articles article(s) and 0 problem(s)"
            break
        fi

        if [ "$(docker inspect -f '{{.State.Running}}' "$SMOKE_WEB" 2>/dev/null || echo false)" != true ]; then
            log=$(docker logs "$SMOKE_WEB" 2>&1 || true)
            fail "Web image boots on its corpus"                 "the host exited before reporting $expected_articles article(s) and 0 problem(s); last lines of its log:"
            printf '%s
' "$log" | tail -12 | sed 's/^/          /'
            smoke_cleanup
            return
        fi

        sleep 1
        waited=$((waited + 1))
    done

    if [ "$waited" -ge 60 ]; then
        fail "Web image boots on its corpus"             "no '$expected_articles article(s)' / '0 problem(s)' line within 60s; last lines of its log:"
        printf '%s
' "$log" | tail -12 | sed 's/^/          /'
        smoke_cleanup
        return
    fi

    docker rm -f "$SMOKE_WEB" >/dev/null 2>&1 || true

    # The same image with one article removed must refuse to start. In Production the startup check
    # throws on any problem, so the exit code is the assertion; the log is quoted because "exited 1"
    # alone could be any failure, including the one this test is not about.
    # Removed by the manifest's own first line, not by the glob: the first .md alphabetically is
    # knowledge-base/README.md, which the loader ignores, and deleting it would leave the reviewed
    # corpus intact and the host starting normally. Root, because the image runs as $APP_UID and /app
    # is root-owned: without it the delete fails silently and the host starts on an intact corpus,
    # which is a passing test of nothing. The timeout turns "the host started anyway" into a failed
    # check instead of a gate that never returns.
    log=$(timeout 90 docker run --rm --user root --network "$SMOKE_NETWORK" "${smoke_environment[@]}" --entrypoint sh "$tag" -c '
        article=$(head -1 /app/knowledge-base/REVIEWED.sha256 | cut -d" " -f3)
        rm "/app/knowledge-base/$article"
        exec dotnet AIEnabledRma.Web.dll
    ' 2>&1) && rc=0 || rc=$?

    if [ "$rc" -ne 0 ]             && printf '%s' "$log" | grep -Fq "REVIEWED.sha256"             && printf '%s' "$log" | grep -Fq "does not match"; then
        pass "Web image refuses to start when an article is removed (exit $rc)"
    else
        fail "Web image refuses a drifted corpus"             "expected a non-zero exit quoting REVIEWED.sha256 and 'does not match', got exit $rc; last lines:"
        printf '%s
' "$log" | tail -12 | sed 's/^/          /'
    fi

    smoke_cleanup
}
check_image "MCP" "src/AIEnabledRma.Mcp/Dockerfile" "$MCP_IMAGE" empty
check_image "Web" "src/AIEnabledRma.Web/Dockerfile" "$WEB_IMAGE" corpus
check_image "RAG" "src/AIEnabledRma.Rag/Dockerfile" "$RAG_IMAGE" corpus

if [ "$failures" -eq 0 ]; then
    boot_smoke "$WEB_IMAGE"
fi

if [ "${KEEP_IMAGES:-0}" != 1 ] && [ "$failures" -eq 0 ]; then
    docker rmi "$MCP_IMAGE" "$WEB_IMAGE" "$RAG_IMAGE" >/dev/null 2>&1 || true
fi

if [ "$failures" -ne 0 ]; then
    echo
    echo "$failures check(s) failed"
    exit 1
fi

echo
echo "all checks passed"
