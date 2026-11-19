#!/usr/bin/env bash
#
# Boots Keycloak with the committed realm and the mcp-http profile, then drives both flows the
# runbook describes and asserts what each is supposed to answer: the client-credentials token's
# claims, a 200 with it, a 401 without one, a 403 from an unlisted origin, the preflight with and
# without CORS headers, and the PKCE authorization-code flow with no secret and a verifier.
#
# The realm import is a committed JSON file that Keycloak validates at boot; an invalid field stops
# the import, and every flow after it fails. That is why this runs in CI on every pull request
# instead of being re-typed from docs/mcp.md by hand.
#
# The issuer is the hostname Keycloak is reached by, so tokens are minted from inside the compose
# network (`http://keycloak:8080/...`) where the transport validates them; the authorize page and
# the sign-in form are driven from the host, because that is what a browser reaches.
#
# Usage:
#   scripts/verify-auth.sh            # boot, verify, stop what it started
#   KEEP=1 scripts/verify-auth.sh     # leave the stack running afterwards
#
# Environment:
#   KEYCLOAK_URL  host address for the browser-facing steps, default http://localhost:8080
#   MCP_URL       default http://localhost:5080
#   KEEP=1        do not stop the containers this script started
#
# Requirements: docker compose (v2), curl, python3 (or python). No jq, no openssl.

set -euo pipefail

KEYCLOAK_URL=${KEYCLOAK_URL:-http://localhost:8080}
MCP_URL=${MCP_URL:-http://localhost:5080}
REALM=aienabledrma
PROFILE=mcp-http
CURL_IMAGE=curlimages/curl:8.10.1

REPO_ROOT=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
cd "$REPO_ROOT"

# `command -v` is not enough on Windows, where python3 can be an App Execution Alias that exists
# and refuses to run. Ask the interpreter to prove it works before trusting it.
PY=""
for candidate in python3 python; do
    if command -v "$candidate" >/dev/null 2>&1 && "$candidate" -c 'import sys' >/dev/null 2>&1; then
        PY=$candidate
        break
    fi
done

if [ -z "$PY" ]; then
    echo "verify-auth: python3 is required to read the token responses." >&2
    exit 2
fi

WORK=$(mktemp -d)
started=()
failures=0
network=""
CURL_HELPER=""

cleanup() {
    if [ "${KEEP:-0}" != 1 ] && [ "${#started[@]}" -gt 0 ]; then
        # stop, not down: the containers and their data stay, so a second run is fast and nothing
        # that was here before this script ran is removed.
        docker compose --profile "$PROFILE" stop "${started[@]}" >/dev/null 2>&1 || true
    fi

    if [ -n "$CURL_HELPER" ]; then
        docker rm -f "$CURL_HELPER" >/dev/null 2>&1 || true
    fi

    rm -rf "$WORK"
}

trap cleanup EXIT

# stderr, because authorize_login's stdout is the authorization code.
pass() { printf 'ok    %s\n' "$1" >&2; }

fail() {
    printf 'FAIL  %s\n        %s\n' "$1" "${2:-}" >&2
    failures=$((failures + 1))
}

check() {
    local name=$1 expected=$2 actual=$3

    if [ "$expected" = "$actual" ]; then
        pass "$name ($actual)"
    else
        fail "$name" "expected '$expected', got '$actual'"
    fi
}

check_contains() {
    local name=$1 needle=$2 haystack=$3

    case "$haystack" in
        *"$needle"*) pass "$name" ;;
        *) fail "$name" "expected to contain '$needle', got '$haystack'" ;;
    esac
}

# A claim that must be present with any value: the subject is an id nobody can hard-code, and an
# empty one is the failure worth catching (an imported realm has none of Keycloak's built-in scopes
# unless the import carries them, so sub can be absent while the token is otherwise valid).
check_nonempty() {
    local name=$1 value=$2

    if [ -n "$value" ]; then
        pass "$name ($value)"
    else
        fail "$name" "expected a value, got empty"
    fi
}

# A claim from an access token, printed as one line; empty when the field is absent.
claim() {
    local token=$1 field=$2

    printf '%s' "$token" | "$PY" -c '
import base64, json, sys

token, field = sys.argv[1], sys.argv[2]
payload = token.split(".")[1]
payload += "=" * (-len(payload) % 4)
claims = json.loads(base64.urlsafe_b64decode(payload))
value = claims.get(field)
if isinstance(value, list):
    value = " ".join(value)
print(value if value is not None else "")
' "$token" "$field" | tr -d '\r'
}

wait_for() {
    local name=$1 url=$2 limit=${3:-120} waited=0

    until curl -fsS -o /dev/null "$url" 2>/dev/null; do
        if [ "$waited" -ge "$limit" ]; then
            fail "$name" "not ready at $url after ${limit}s"
            return 1
        fi

        sleep 2
        waited=$((waited + 2))
    done

    pass "$name ($url)"
}

# curl on the compose network: the issuer in the token is whatever host was asked, and the
# transport validates http://keycloak:8080/realms/aienabledrma.
# Git Bash rewrites arguments that look like POSIX paths (`-o /tmp/login.html`) into Windows
# paths before docker sees them, which makes the container's curl write to a path that does not
# exist there. The override is scoped to the container calls: the host curl below relies on
# /dev/null being translated for it.
kc() {
    MSYS_NO_PATHCONV=1 MSYS2_ARG_CONV_EXCL='*' \
        docker run --rm -i --network "$network" "$CURL_IMAGE" "$@"
}

# A token request from inside the network, printing the body and then STATUS:<code>.
kc_token() {
    kc -sS -w '\nSTATUS:%{http_code}' \
        -X POST "http://keycloak:8080/realms/$REALM/protocol/openid-connect/token" "$@"
}

body_of() { printf '%s' "${1%$'\n'STATUS:*}"; }

status_of() { printf '%s' "${1##*STATUS:}"; }

echo "== boot: docker compose --profile $PROFILE"

for service in keycloak mcp-http; do
    id=$(docker compose --profile "$PROFILE" ps -q "$service" 2>/dev/null || true)

    if [ -z "$id" ]; then
        started+=("$service")
        if [ "$service" = mcp-http ]; then
            started+=(postgres)
        fi
    fi
done

if [ "${#started[@]}" -gt 0 ]; then
    docker compose --profile "$PROFILE" up -d --build
else
    # The transport caches the realm's signing keys along with its discovery document. An edited
    # realm is re-imported by the next boot, which mints new keys, and a cached key set makes every
    # machine token answer 401 while the browser flow (which happens later, after a refresh has
    # landed) still passes — a failure that reads as a broken realm and is not one. Restarting the
    # transport costs seconds and makes a reused stack behave like a fresh one.
    echo "note  reusing the running stack; restarting $PROFILE so it reads the realm's current keys"
    docker compose --profile "$PROFILE" restart mcp-http >/dev/null
fi

wait_for "keycloak discovery" "$KEYCLOAK_URL/realms/$REALM/.well-known/openid-configuration" 120
wait_for "transport discovery" "$MCP_URL/.well-known/oauth-protected-resource/mcp" 240

network=$(docker inspect -f '{{range $k, $v := .NetworkSettings.Networks}}{{$k}} {{end}}' \
    "$(docker compose --profile "$PROFILE" ps -q mcp-http)" | awk '{print $1}')

if [ -z "$network" ]; then
    fail "compose network found" "could not read a network from the mcp-http container"
    exit 1
fi

# A long-lived curl container: the cookie jar has to survive between the authorize request and the
# sign-in POST, and files written from the host into a bind mount are not reliably writable on
# Docker Desktop, so the state stays inside the container and comes out with `cat`.
CURL_HELPER=rma-auth-curl-verify
docker rm -f "$CURL_HELPER" >/dev/null 2>&1 || true
docker run -d --name "$CURL_HELPER" --network "$network" "$CURL_IMAGE" sleep 600 >/dev/null

bc() {
    MSYS_NO_PATHCONV=1 MSYS2_ARG_CONV_EXCL='*' \
        docker exec -i "$CURL_HELPER" curl "$@"
}

# The same override for `cat`: without it the container is asked for C:/Users/.../login.html.
cat_from_container() {
    MSYS_NO_PATHCONV=1 MSYS2_ARG_CONV_EXCL='*' docker exec "$CURL_HELPER" cat "$1"
}

TOKEN_RESPONSE="$WORK/token.json"
HEADERS="$WORK/headers.txt"
BODY="$WORK/body.json"
COOKIES="$WORK/cookies.txt"

echo
echo "== client credentials"

RESPONSE=$(kc_token \
    -d 'grant_type=client_credentials' \
    -d 'client_id=mcp-agent' \
    -d 'client_secret=mcp-agent-secret' \
    -d 'scope=rma:read')

check "token endpoint answers" 200 "$(status_of "$RESPONSE")"

TOKEN=$(body_of "$RESPONSE" | "$PY" -c 'import json,sys;print(json.load(sys.stdin).get("access_token",""))')

if [ -z "$TOKEN" ]; then
    fail "access token present" "the token response has no access_token: $(body_of "$RESPONSE")"
else
    pass "access token present"

    # These four are what the transport checks. A realm mistake shows up as a 401/403 that does not
    # say which claim was wrong, so each is asserted here by name.
    check "iss is the compose issuer" "http://keycloak:8080/realms/$REALM" "$(claim "$TOKEN" iss)"
    check_contains "aud names the resource" "aienabledrma-mcp" "$(claim "$TOKEN" aud)"
    check_contains "scope carries rma:read" "rma:read" "$(claim "$TOKEN" scope)"
    check "azp is mcp-agent" "mcp-agent" "$(claim "$TOKEN" azp)"
    check_nonempty "sub names the service account" "$(claim "$TOKEN" sub)"
fi

INITIALIZE='{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"verify-auth","version":"1"}}}'

STATUS=$(curl -sS -o "$BODY" -w '%{http_code}' \
    -X POST "$MCP_URL/mcp" \
    -H "Authorization: Bearer $TOKEN" \
    -H 'Content-Type: application/json' \
    -H 'Accept: application/json, text/event-stream' \
    -d "$INITIALIZE")

check "initialize with a machine token" 200 "$STATUS"

echo
echo "== refusals"

STATUS=$(curl -sS -o "$BODY" -D "$HEADERS" -w '%{http_code}' \
    -X POST "$MCP_URL/mcp" \
    -H 'Content-Type: application/json' \
    -H 'Accept: application/json, text/event-stream' \
    -d "$INITIALIZE")

check "no token is refused" 401 "$STATUS"
check_contains "401 points at the discovery document" \
    "resource_metadata=\"$MCP_URL/.well-known/oauth-protected-resource/mcp\"" \
    "$(tr -d '\r' < "$HEADERS")"

STATUS=$(curl -sS -o "$BODY" -w '%{http_code}' \
    -X POST "$MCP_URL/mcp" \
    -H "Authorization: Bearer $TOKEN" \
    -H 'Origin: http://evil.example' \
    -H 'Content-Type: application/json' \
    -H 'Accept: application/json, text/event-stream' \
    -d "$INITIALIZE")

check "an unlisted origin is refused" 403 "$STATUS"

echo
echo "== preflight"

STATUS=$(curl -sS -o /dev/null -D "$HEADERS" -w '%{http_code}' \
    -X OPTIONS "$MCP_URL/mcp" \
    -H 'Origin: http://localhost:5173' \
    -H 'Access-Control-Request-Method: POST' \
    -H 'Access-Control-Request-Headers: authorization,content-type')

RESPONSE_HEADERS=$(tr -d '\r' < "$HEADERS")

check "listed origin preflight" 204 "$STATUS"
check_contains "origin is echoed" "Access-Control-Allow-Origin: http://localhost:5173" "$RESPONSE_HEADERS"
check_contains "authorization header is allowed" "authorization" "$RESPONSE_HEADERS"

STATUS=$(curl -sS -o /dev/null -D "$HEADERS" -w '%{http_code}' \
    -X OPTIONS "$MCP_URL/mcp" \
    -H 'Origin: http://evil.example' \
    -H 'Access-Control-Request-Method: POST' \
    -H 'Access-Control-Request-Headers: authorization,content-type')

check "unlisted origin preflight" 204 "$STATUS"

if grep -qi '^Access-Control-Allow-Origin:' "$HEADERS"; then
    fail "unlisted origin gets no CORS grant" "the preflight echoed an Access-Control-Allow-Origin for an unlisted origin"
else
    pass "unlisted origin gets no CORS grant"
fi

echo
echo "== PKCE (mcp-browser, no secret)"

# Two command substitutions rather than `read` over one line of output: the trailing newline is
# stripped by the shell, and the CR a Windows python writes is stripped by tr, so neither a stray
# space nor a CR reaches Keycloak, which answers `invalid_request: Invalid parameter: code_challenge`
# for anything that is not 43-128 characters of [A-Za-z0-9-._~].
VERIFIER=$("$PY" -c 'import secrets; print(secrets.token_urlsafe(48))' | tr -d '\r')
CHALLENGE=$(VERIFIER="$VERIFIER" "$PY" -c '
import base64, hashlib, os

verifier = os.environ["VERIFIER"]
digest = hashlib.sha256(verifier.encode()).digest()
print(base64.urlsafe_b64encode(digest).rstrip(b"=").decode())
' | tr -d '\r')

# Inside the network, so the authorization session and the token it produces name the same issuer
# the transport validates. A code obtained through localhost yields iss=http://localhost:8080/...
# and the transport refuses it, which is the hostname rule in docs/mcp.md, not a bug.
AUTHORIZE="http://keycloak:8080/realms/$REALM/protocol/openid-connect/auth"
REDIRECT="http://localhost:5173/callback"

code_from() {
    LOCATION="${1-}" "$PY" -c '
import os, urllib.parse

location = os.environ.get("LOCATION", "")
query = urllib.parse.urlparse(location).query
print(urllib.parse.parse_qs(query).get("code", [""])[0])
'
}

location_header() {
    grep -i '^location:' "$1" | tr -d '\r' | head -1 | cut -d' ' -f2-
}

# A path a Windows python can open: MSYS /tmp is a Windows directory with another name.
host_path() {
    if command -v cygpath >/dev/null 2>&1; then
        cygpath -w "$1"
    else
        printf '%s' "$1"
    fi
}

# authorize + sign in, printing the code on stdout (progress goes to stderr, which is why pass and
# fail write there). Each call uses its own cookie jar, because Keycloak invalidates the code on the
# first failed exchange: the no-verifier check needs a code of its own, not the one exchanged below.
authorize_login() {
    local jar=$1 status action location code

    status=$(bc -sS -c "/tmp/$jar" -b "/tmp/$jar" -D /tmp/auth-headers.txt -o /tmp/login.html -w '%{http_code}'         -G         --data-urlencode 'client_id=mcp-browser'         --data-urlencode 'response_type=code'         --data-urlencode 'scope=openid rma:read'         --data-urlencode "redirect_uri=$REDIRECT"         --data-urlencode "code_challenge=$CHALLENGE"         --data-urlencode 'code_challenge_method=S256'         "$AUTHORIZE")

    cat_from_container /tmp/auth-headers.txt > "$WORK/auth-headers.txt"
    cat_from_container /tmp/login.html > "$WORK/login.html"

    if [ "$status" = 302 ]; then
        location=$(location_header "$WORK/auth-headers.txt")
        code=$(code_from "$location")

        if [ -n "$code" ]; then
            echo "note  keycloak answered authorize with an existing session; using its code" >&2
            printf '%s' "$code"
            return 0
        fi

        fail "authorize redirects to the callback" "location was '$location'"
        return 1
    fi

    check "authorize page opens" 200 "$status"

    action=$(HTML_FILE=$(host_path "$WORK/login.html") "$PY" - <<'PYEOF'
import html, os, re

page = open(os.environ["HTML_FILE"], encoding="utf-8", errors="replace").read()
match = re.search(r'action="([^"]*login-actions/authenticate[^"]*)"', page)
print(html.unescape(match.group(1)) if match else "")
PYEOF
)

    if [ -z "$action" ]; then
        fail "sign-in form has an action" "no login-actions/authenticate form found on the authorize page"
        return 1
    fi

    pass "sign-in form has an action"

    status=$(bc -sS -c "/tmp/$jar" -b "/tmp/$jar" -o /dev/null -D /tmp/login-headers.txt -w '%{http_code}'         -X POST "$action"         -H 'Content-Type: application/x-www-form-urlencoded'         -d 'username=browser-demo'         -d 'password=browser-demo'         -d 'credentialId=')

    cat_from_container /tmp/login-headers.txt > "$WORK/login-headers.txt"

    check "sign-in redirects" 302 "$status"

    printf '%s' "$(code_from "$(location_header "$WORK/login-headers.txt")")"
}

CODE=$(authorize_login jar-first.txt)

if [ -z "$CODE" ]; then
    fail "redirect carries a code" "neither the authorize redirect nor the sign-in redirect carried one"
    echo
    echo "$failures check(s) failed" >&2
    exit 1
fi

pass "redirect carries a code"

# No verifier: the realm must refuse the code on its own. This is what makes PKCE the protection.
RESPONSE=$(kc_token \
    -d 'grant_type=authorization_code' \
    -d 'client_id=mcp-browser' \
    -d "code=$CODE" \
    -d "redirect_uri=$REDIRECT")

check "code without a verifier is refused" 400 "$(status_of "$RESPONSE")"
check_contains "refusal is invalid_grant" "invalid_grant" "$(body_of "$RESPONSE")"

# A fresh code for the exchange: the refusal above consumed the first one, which is itself the
# behaviour that makes a code single-use.
CODE=$(authorize_login jar-second.txt)

if [ -z "$CODE" ]; then
    fail "a second authorization yields a code" "the sign-in flow produced no code on the second run"
    echo
    echo "$failures check(s) failed" >&2
    exit 1
fi

RESPONSE=$(kc_token \
    -d 'grant_type=authorization_code' \
    -d 'client_id=mcp-browser' \
    -d "code=$CODE" \
    -d "redirect_uri=$REDIRECT" \
    -d "code_verifier=$VERIFIER")

check "code with the verifier is exchanged" 200 "$(status_of "$RESPONSE")"

BROWSER_TOKEN=$(body_of "$RESPONSE" | "$PY" -c 'import json,sys;print(json.load(sys.stdin).get("access_token",""))')

check "azp is mcp-browser" "mcp-browser" "$(claim "$BROWSER_TOKEN" azp)"
check_nonempty "browser sub names the user" "$(claim "$BROWSER_TOKEN" sub)"
check_contains "browser scope carries rma:read" "rma:read" "$(claim "$BROWSER_TOKEN" scope)"

STATUS=$(curl -sS -o /dev/null -D "$HEADERS" -w '%{http_code}' \
    -X POST "$MCP_URL/mcp" \
    -H "Authorization: Bearer $BROWSER_TOKEN" \
    -H 'Origin: http://localhost:5173' \
    -H 'Content-Type: application/json' \
    -H 'Accept: application/json, text/event-stream' \
    -d "$INITIALIZE")

check "initialize with a browser token" 200 "$STATUS"
check_contains "response carries the CORS grant" \
    "Access-Control-Allow-Origin: http://localhost:5173" \
    "$(tr -d '\r' < "$HEADERS")"

RESPONSE=$(kc_token \
    -d 'grant_type=authorization_code' \
    -d 'client_id=mcp-browser' \
    -d "code=$CODE" \
    -d "redirect_uri=$REDIRECT" \
    -d "code_verifier=$VERIFIER")

check "a replayed code is refused" 400 "$(status_of "$RESPONSE")"

echo
if [ "$failures" -ne 0 ]; then
    echo "$failures check(s) failed" >&2
    exit 1
fi

echo "all checks passed"
