#!/usr/bin/env bash
#
# Builds the three runtime images and checks each carries what its host reads.
#
# The publish-side tests prove `dotnet publish` carries the Web host's knowledge-base/; this proves
# the image does, which is a different claim. The Docker build sees only what the context and
# .dockerignore allow, and it restores from a hand-written COPY list, so it can differ from a publish
# in both directions and has: the Web image could not be built at all once because a referenced
# manifest was missing from that list, and nothing in a checkout showed it.
#
# The RAG image joined this script with the production pack: the host that reads the corpus most
# directly was the one image no gate looked inside. Both corpus hosts are booted; the other image is
# checked differently on purpose. The Web host reads the corpus from its content
# root, so a missing article is a policy answer that quietly stops happening. The tool server's tools
# evaluate stored data and never open knowledge-base/, and the startup check that inspects a corpus is
# registered by AddRag, which that host does not call — so its image must carry none. That absence is
# asserted here rather than left for the next reader to wonder about, because the image used to carry
# the corpus and the check that said so was true and told nobody anything.
#
# The RAG runtime is chiseled: no shell, no package manager, no debugger. That is why its corpus is
# read out with `docker cp` and counted here instead of by a process reporting a number, why its drift
# case mounts a damaged corpus over the image's own instead of deleting a file inside it, and why the
# entry point is read from the image config instead of repeated in a command line. The absence of a
# shell is asserted as well, so a base that drifted back to the full runtime fails this gate instead of
# passing every other check while carrying 45 MB of tooling into a corpus host.
#
# All three images carry the commit they were built from as org.opencontainers.image.revision, and
# that label is compared with this checkout. After a fix had been pushed, "is this image from before or
# after it" was a question this repository could not answer from the image.
#
# The last check starts the production compose's own web service from deploy/prod.env.example. That
# file was otherwise only resolved, and a stack that parses but does not start is the failure a
# deployment finds first.
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

# docker.exe reads host paths, not the shell's /c/... form, and the drift check below mounts a
# directory from this machine into the container.
hostpath() {
    if command -v cygpath >/dev/null 2>&1; then cygpath -w "$1"; else printf '%s' "$1"; fi
}

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

# One container per image: docker cp pulls /app/knowledge-base out and the counting happens here, on
# the host. The shell version (`docker run --entrypoint sh -c`) cannot work against the chiseled
# runtime, which has no /bin/sh — and docker cp is the stronger check anyway, because it reads the
# files the image actually shipped rather than what a process chose to report about them.
#
# A path that is not there is a failed copy, which is the difference between "the corpus was never
# copied" and "the corpus was copied empty" the three fields below are there to keep apart.
inspect() {
    local tag=$1 out created count=0 manifest=no directory=no

    out=$(mktemp -d)

    if created=$(docker create "$tag" 2>&1); then
        if docker cp "$created:/app/knowledge-base" "$out/knowledge-base" >/dev/null 2>&1; then
            directory=yes
            count=$(find "$out/knowledge-base" -name '*.md' -type f | wc -l | tr -d ' ')
            [ -f "$out/knowledge-base/REVIEWED.sha256" ] && manifest=yes
        fi

        docker rm -f "$created" >/dev/null 2>&1 || true
    else
        printf '%s
' "$created"
        rm -rf "$out"
        return 1
    fi

    rm -rf "$out"
    printf '%s %s %s
' "$count" "$manifest" "$directory"
}

# The commit an image was built from, read back from its own label: "is this image from before or after
# the fix" has to be answerable from the image, not from a shell history.
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
# and answers every question as though the policy said nothing. This boots each corpus-reading image
# through its own entrypoint in Production — the environment where the startup check refuses
# anything but the reviewed corpus — and reads the count the host itself logs.
#
# The database is a throwaway PostgreSQL built from the image the compose file uses, on its own
# network and with a password that is not one of the development defaults Production refuses. The host
# does not query it during startup, but a smoke test that boots without its configuration would keep
# passing after that configuration stopped resolving.
SMOKE_NETWORK=rma-image-smoke
SMOKE_PG=rma-image-smoke-pg
SMOKE_WEB=rma-image-smoke-web
SMOKE_RAG=rma-image-smoke-rag
SMOKE_PASSWORD=image-smoke-not-a-default

smoke_environment=(
    -e ASPNETCORE_ENVIRONMENT=Production
    -e "ConnectionStrings__Rma=Host=$SMOKE_PG;Port=5432;Database=rmadev;Username=rmauser;Password=$SMOKE_PASSWORD"
    -e Rag__Admin__ApiKey=image-smoke-operator-key
    -e Payments__Fake__AllowInProduction=true
    # The web image boots in Production, where the host refuses to start without the operator
    # identity provider; the RAG image ignores these. Nothing here challenges it, so the values
    # only have to be an https URL and a client id.
    -e OperatorAuth__Authority=https://idp.example.com/realms/rma
    -e OperatorAuth__ClientId=rma-operator
    # The bundled example model is refused in Production, for every host that selects a provider.
    -e Ai__Provider=openai-compatible
    -e Ai__OpenAiCompatible__BaseUrl=https://llm.example.com/v1
)

smoke_cleanup() {
    docker rm -f "$SMOKE_WEB" "$SMOKE_RAG" "$SMOKE_PG" >/dev/null 2>&1 || true
    docker network rm "$SMOKE_NETWORK" >/dev/null 2>&1 || true
}

# The production pack is resolved by the CI job on every run, and resolving it proves the file
# parses, not that a service built from it starts. This is the pack's own three application services,
# started from the example env, on a project name of its own so the check can never adopt, overwrite
# or tear down a real stack on the machine, and so its database volume cannot survive between runs.
# The proxy is left out: it would take ports 80 and 443, and nothing asserted here is behind it.
#
# The example env's secrets are placeholders on purpose, and every host now refuses a placeholder
# rather than starting on one, so the smoke supplies synthetic values for exactly those settings.
# They are still checked as values: a deployment that copied the example without replacing them
# fails the same way, which is the behaviour this gate exists to prove.
PROD_PROJECT=rma-image-smoke-prod
PROD_PASSWORD=image-smoke-not-a-default
PROD_OIDC_SECRET=image-smoke-synthetic-client-secret
PROD_STRIPE_KEY=sk_live_synthetic_smoke_key
PROD_WEBHOOK_SECRET=whsec_synthetic_smoke_key
PROD_OPERATIONS_KEY=image-smoke-operator-key
PROD_AI_BASE_URL=https://llm.example.com/v1

prod_compose() {
    POSTGRES_PASSWORD="$PROD_PASSWORD" \
    OIDC_OPERATOR_CLIENT_SECRET="$PROD_OIDC_SECRET" \
    STRIPE_SECRET_KEY="$PROD_STRIPE_KEY" \
    STRIPE_WEBHOOK_SECRET="$PROD_WEBHOOK_SECRET" \
    RMA_OPERATIONS_KEY="$PROD_OPERATIONS_KEY" \
    RAG_ADMIN_KEY="$PROD_OPERATIONS_KEY" \
    AI_PROVIDER=openai-compatible \
    AI_BASE_URL="$PROD_AI_BASE_URL" \
        docker compose -p "$PROD_PROJECT" -f docker-compose.prod.yml --env-file deploy/prod.env.example "$@"
}

prod_cleanup() {
    prod_compose down -v --rmi local --remove-orphans >/dev/null 2>&1 || true
}

trap 'smoke_cleanup; prod_cleanup' EXIT

# Both hosts that read the corpus, parameterised by the entry point that reads it. The RAG host is
# the other one: it loads the corpus from its content root and, outside Development, refuses a
# drifted one through the same startup check the web host runs.
boot_smoke() {
    local host=$1 tag=$2 assembly=$3 container=$4 log rc waited=0 ready=no

    smoke_cleanup
    docker network create "$SMOKE_NETWORK" >/dev/null 2>&1 || true

    if ! docker run -d --name "$SMOKE_PG" --network "$SMOKE_NETWORK"             -e POSTGRES_USER=rmauser             -e POSTGRES_PASSWORD="$SMOKE_PASSWORD"             -e POSTGRES_DB=rmadev             "$PG_IMAGE" >/dev/null; then
        fail "$host image boots on its corpus" "the smoke database container would not start"
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
        fail "$host image boots on its corpus"             "the smoke database did not become ready; last lines of its log:"
        docker logs "$SMOKE_PG" 2>&1 | tail -8 | sed 's/^/          /'
        smoke_cleanup
        return
    fi

    # Started detached so the log can be read while the host is still running: a host that dies after
    # printing the line is not a boot, and a host that hangs before printing it fails the wait below.
    if ! docker run -d --name "$container" --network "$SMOKE_NETWORK"             "${smoke_environment[@]}" "$tag" >/dev/null; then
        fail "$host image boots on its corpus" "docker run refused to start the web image"
        smoke_cleanup
        return
    fi

    while [ "$waited" -lt 60 ]; do
        log=$(docker logs "$container" 2>&1 || true)

        if printf '%s' "$log" | grep -Fq "$expected_articles article(s)"                 && printf '%s' "$log" | grep -Fq "0 problem(s)"; then
            pass "$host image boots with $expected_articles article(s) and 0 problem(s)"
            break
        fi

        if [ "$(docker inspect -f '{{.State.Running}}' "$container" 2>/dev/null || echo false)" != true ]; then
            log=$(docker logs "$container" 2>&1 || true)
            fail "$host image boots on its corpus"                 "the host exited before reporting $expected_articles article(s) and 0 problem(s); last lines of its log:"
            printf '%s
' "$log" | tail -12 | sed 's/^/          /'
            smoke_cleanup
            return
        fi

        sleep 1
        waited=$((waited + 1))
    done

    if [ "$waited" -ge 60 ]; then
        fail "$host image boots on its corpus"             "no '$expected_articles article(s)' / '0 problem(s)' line within 60s; last lines of its log:"
        printf '%s
' "$log" | tail -12 | sed 's/^/          /'
        smoke_cleanup
        return
    fi

    docker rm -f "$container" >/dev/null 2>&1 || true

    # Which binary the image runs is now read from its config: the drift case used to spell it out in
    # a shell one-liner, and a chiseled image has no shell to spell anything in.
    entrypoint=$(docker image inspect -f '{{json .Config.Entrypoint}}' "$tag")

    if printf '%s' "$entrypoint" | grep -Fq "$assembly"; then
        pass "$host image entrypoint runs $assembly"
    else
        fail "$host image entrypoint" "expected $assembly in the entrypoint, image says $entrypoint"
    fi

    # ... and it must refuse it from a damaged corpus mounted over its own. The corpus is copied out and
    # damaged on the host because the chiseled runtime has no shell to delete a file with, and a bind
    # mount is also what a drifted deployment looks like: the files are wrong, the image is not. The
    # article is the manifest's own first line, not the first .md alphabetically — that is
    # knowledge-base/README.md, which the loader ignores, so deleting it would leave the reviewed corpus
    # intact and the host starting normally, a passing test of nothing.
    damaged=$(mktemp -d)
    created=$(docker create "$tag")
    docker cp "$created:/app/knowledge-base" "$damaged/knowledge-base" >/dev/null 2>&1 \
        || fail "$host image refuses a drifted corpus" "could not copy the corpus out of $tag"
    docker rm -f "$created" >/dev/null 2>&1 || true

    article=$(head -1 "$damaged/knowledge-base/REVIEWED.sha256" | cut -d' ' -f3)
    rm -f "$damaged/knowledge-base/$article"

    # In Production the startup check throws on any problem, so the exit code is the assertion; the log
    # is quoted because "exited 1" alone could be any failure, including the one this is not about.
    log=$(timeout 90 docker run --rm --network "$SMOKE_NETWORK" "${smoke_environment[@]}" \
        -v "$(hostpath "$damaged/knowledge-base"):/app/knowledge-base:ro" "$tag" 2>&1) && rc=0 || rc=$?
    rm -rf "$damaged" >/dev/null 2>&1 || true

    if [ "$rc" -ne 0 ] \
            && printf '%s' "$log" | grep -Fq "REVIEWED.sha256" \
            && printf '%s' "$log" | grep -Fq "does not match"; then
        pass "$host image refuses to start when an article is removed (exit $rc)"
    else
        fail "$host image refuses a drifted corpus" \
            "expected a non-zero exit quoting REVIEWED.sha256 and 'does not match', got exit $rc; last lines:"
        printf '%s
' "$log" | tail -12 | sed 's/^/          /'
    fi

    smoke_cleanup
}
# The chiseled base is the reason the two checks above read files instead of running a shell, so it is
# asserted too: an image that drifted back to the full runtime would pass every other check here while
# quietly carrying a shell, a package manager and a debugger into a corpus host that needs none.
check_no_shell() {
    local name=$1 tag=$2

    if docker run --rm --entrypoint sh "$tag" -c true >/dev/null 2>&1; then
        fail "$name image is chiseled" "$tag has /bin/sh, so it is not built on a chiseled runtime"
    else
        pass "$name image carries no shell"
    fi
}

check_image "MCP" "src/AIEnabledRma.Mcp/Dockerfile" "$MCP_IMAGE" empty
check_image "Web" "src/AIEnabledRma.Web/Dockerfile" "$WEB_IMAGE" corpus
check_image "RAG" "src/AIEnabledRma.Rag/Dockerfile" "$RAG_IMAGE" corpus
check_no_shell "RAG" "$RAG_IMAGE"

# The deployment's own composition, booted: this is the check that catches a required value missing
# from the example env or a service the current host refuses before anyone runs `up` against a real
# domain. It builds the web image from the same Dockerfile the build above checked, because the
# service definition is what is under test here, not the image contents twice.
# Waits for one line in one service's log, then quotes the log when it never arrives. The line is
# the host's own statement that it got past startup, which a running container alone does not prove:
# a process that is still alive can be waiting on a retry loop, and a process that logged the line
# and exited is not serving.
prod_wait_for_line() {
    local service=$1 text=$2 container waited=0 log=""

    container=$(prod_compose ps -q "$service")

    # A service that started and exited is still the thing to read the log from; a failed start is
    # otherwise reported as "no container", which is not a diagnosis.
    if [ -z "$container" ]; then
        container=$(prod_compose ps -a -q "$service")
    fi

    if [ -z "$container" ]; then
        fail "prod compose $service boots" "compose reported success but started no $service container"
        return 1
    fi

    while [ "$waited" -lt 90 ]; do
        log=$(docker logs "$container" 2>&1 || true)

        if printf '%s' "$log" | grep -Fq "$text"; then
            pass "prod compose $service boots ($text)"
            return 0
        fi

        if [ "$(docker inspect -f '{{.State.Running}}' "$container" 2>/dev/null || echo false)" != true ]; then
            fail "prod compose $service boots" \
                "the service exited before logging '$text'; last lines of its log:"
            printf '%s\n' "$log" | tail -12 | sed 's/^/          /'
            return 1
        fi

        sleep 1
        waited=$((waited + 1))
    done

    fail "prod compose $service boots" "no '$text' line within 90s; last lines of its log:"
    printf '%s\n' "$log" | tail -12 | sed 's/^/          /'
    return 1
}

prod_boot_smoke() {
    local log service container

    prod_cleanup

    log=$(mktemp)

    # All three application services, because all three are what a deployment runs: the web host was
    # the only one this gate started, so a pack whose tool server or RAG host refused its own
    # configuration looked verified.
    if ! prod_compose up -d --build web mcp rag >"$log" 2>&1; then
        fail "prod compose boots" "docker compose up failed; last lines of its output:"
        tail -12 "$log" | sed 's/^/          /'
        rm -f "$log"
        prod_cleanup
        return
    fi

    rm -f "$log"

    # The two corpus hosts state the article count they loaded; the tool server serves no corpus, so
    # its line is ASP.NET's own "Application started".
    prod_wait_for_line web "$expected_articles article(s)" || { prod_cleanup; return; }
    prod_wait_for_line rag "$expected_articles article(s)" || { prod_cleanup; return; }
    prod_wait_for_line mcp "Application started" || { prod_cleanup; return; }

    # A container that logs its line and exits a second later has not booted. The pause is short
    # because the assertion is about the composition starting, not about serving traffic.
    sleep 3

    for service in web mcp rag; do
        container=$(prod_compose ps -q "$service")

        if [ -n "$container" ] \
            && [ "$(docker inspect -f '{{.State.Running}}' "$container" 2>/dev/null || echo false)" = true ]; then
            pass "prod compose $service stays running"
        else
            fail "prod compose $service stays running" "the service exited within 3s of starting; last lines:"
            docker logs "$container" 2>&1 | tail -12 | sed 's/^/          /'
        fi
    done

    prod_cleanup
}

if [ "$failures" -eq 0 ]; then
    boot_smoke "Web" "$WEB_IMAGE" "AIEnabledRma.Web.dll" "$SMOKE_WEB"
    boot_smoke "RAG" "$RAG_IMAGE" "AIEnabledRma.Rag.dll" "$SMOKE_RAG"
    prod_boot_smoke
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
