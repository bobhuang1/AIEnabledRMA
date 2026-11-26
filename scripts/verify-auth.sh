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
#   KEYCLOAK_ADMIN / KEYCLOAK_ADMIN_PASSWORD  master-realm admin for the shape checks below.
#                 The username defaults to `admin`; the password is generated per run when unset, so
#                 the gate never logs in with a credential written down in this repository.
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

# A per-run password for the master-realm admin. The realm commits demo clients and a demo user on
# purpose; the admin account is different, because it can rewrite all of them, and `admin`/`admin` is
# a credential published in this repository. The value is generated here, handed to compose through
# the variable the container bootstraps from, and never printed. An operator who already has a stack
# running can pass their own, which is why the variable is honoured first.
KEYCLOAK_ADMIN_PASSWORD=${KEYCLOAK_ADMIN_PASSWORD:-$("$PY" -c 'import secrets; print(secrets.token_urlsafe(24))' | tr -d '')}
export KEYCLOAK_ADMIN_PASSWORD

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
    # The transport caches the realm's signing keys with its discovery document. An edited realm is
    # re-imported by the next boot, which mints new keys, and a cached key set made every machine
    # token answer 401 while the browser flow (which happens later) still passed — a failure that
    # reads as a broken realm and is not one. The host now refetches the key set when it sees an
    # unknown key id (docs/mcp.md, "Every token is a 401 right after the realm was re-imported").
    # The restart stays because it makes a reused stack behave like a fresh one, and because this
    # gate must not depend on the behaviour it exists to prove.
    # A reused stack holds whatever realm was imported when it started, and the shape checks below
    # are about what the committed file produces. Keycloak's data directory is not a volume, so
    # recreating the container re-imports the file from scratch; the transport is restarted after it
    # because the re-import mints new signing keys.
    echo "note  reusing the running stack; re-importing the realm and restarting $PROFILE"
    docker compose --profile "$PROFILE" rm -sf keycloak >/dev/null 2>&1
    docker compose --profile "$PROFILE" up -d --wait keycloak >/dev/null
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
echo "== realm shape (admin API, after the import)"

# Until now the imported shape was only ever read by hand: a client that lost its service account,
# a scope that lost its audience mapper or a demo user that lost its password all fail as broken
# features, and the failure looks like a token problem rather than a realm problem. The admin API is
# the only place that says which of those it is, so the facts the flows depend on are asserted here,
# right after the import the boot above guarantees.
ADMIN_TOKEN=$(curl -sS -X POST "$KEYCLOAK_URL/realms/master/protocol/openid-connect/token"     -d 'grant_type=password'     -d 'client_id=admin-cli'     -d "username=${KEYCLOAK_ADMIN:-admin}"     -d "password=$KEYCLOAK_ADMIN_PASSWORD"     | "$PY" -c 'import json,sys;print(json.load(sys.stdin).get("access_token",""))')

# Not check_nonempty: that prints the value, and this value is an admin token.
check "admin token from the master realm" "yes" "$([ -n "$ADMIN_TOKEN" ] && echo yes || echo no)"

admin_get() {
    curl -sS -H "Authorization: Bearer $ADMIN_TOKEN" "$KEYCLOAK_URL/admin/realms/$REALM/$1"
}

admin_get clients > "$WORK/clients.json"
admin_get client-scopes > "$WORK/scopes.json"
admin_get 'users?username=browser-demo&exact=true' > "$WORK/users.json"
admin_get 'users?username=operator-demo&exact=true' > "$WORK/operator.json"

BROWSER_USER_ID=$("$PY" -c '
import json, sys
users = json.load(open(sys.argv[1], encoding="utf-8"))
print(users[0]["id"] if users else "")
' "$WORK/users.json" | tr -d '')

if [ -n "$BROWSER_USER_ID" ]; then
    admin_get "users/$BROWSER_USER_ID/credentials" > "$WORK/credentials.json"
else
    printf '[]' > "$WORK/credentials.json"
fi

# The operator's realm-role mapping, read from the admin API rather than from the realm file: this is
# what the import actually produced, which is the only thing the token claim can follow.
OPERATOR_USER_ID=$("$PY" -c 'import json,sys; users=json.load(open(sys.argv[1], encoding="utf-8")); print(users[0]["id"] if users else "")' "$WORK/operator.json" | tr -d '\r')

if [ -n "$OPERATOR_USER_ID" ]; then
    admin_get "users/$OPERATOR_USER_ID/role-mappings/realm" > "$WORK/operator-roles.json"
else
    printf '[]' > "$WORK/operator-roles.json"
fi

# Each row is name, expected and actual, so one table drives the same `check` the rest of the file
# uses. Reading it through process substitution keeps the failure counter in this shell.
realm_shape() {
    "$PY" - "$WORK/clients.json" "$WORK/scopes.json" "$WORK/users.json" "$WORK/credentials.json" \
        "$WORK/operator.json" "$WORK/operator-roles.json" <<'PYTHON'
import json, sys

# LF on every platform: a table read by bash must not carry a carriage return into the comparison.
sys.stdout.reconfigure(newline=chr(10))

clients_path, scopes_path, users_path, credentials_path, operator_path, operator_roles_path = sys.argv[1:7]
clients = {c["clientId"]: c for c in json.load(open(clients_path, encoding="utf-8"))}
scopes = {s["name"]: s for s in json.load(open(scopes_path, encoding="utf-8"))}
users = json.load(open(users_path, encoding="utf-8"))
credentials = json.load(open(credentials_path, encoding="utf-8"))
operator = (json.load(open(operator_path, encoding="utf-8")) or [{}])[0]
operator_roles = json.load(open(operator_roles_path, encoding="utf-8"))


def row(name, expected, actual):
    print(f"{name}	{expected}	{actual}")


def mapper(name, protocol_mapper):
    return next(
        (m for m in scopes.get(name, {}).get("protocolMappers", []) if m["protocolMapper"] == protocol_mapper),
        {},
    )


agent = clients.get("mcp-agent", {})
row("mcp-agent is present", "yes", "yes" if agent else "no")
row("mcp-agent is confidential", "yes", "yes" if agent and not agent.get("publicClient") else "no")
row("mcp-agent has a service account", "yes", "yes" if agent.get("serviceAccountsEnabled") else "no")
row("mcp-agent has a secret", "yes", "yes" if agent.get("secret") else "no")
row("mcp-agent default scopes", "basic, rma:read", ", ".join(sorted(agent.get("defaultClientScopes", []))))

browser = clients.get("mcp-browser", {})
browser_attributes = browser.get("attributes", {})
row("mcp-browser is present", "yes", "yes" if browser else "no")
row("mcp-browser is public", "yes", "yes" if browser.get("publicClient") else "no")
row("mcp-browser runs the authorization-code flow", "yes", "yes" if browser.get("standardFlowEnabled") else "no")
row("mcp-browser redirect", "http://localhost:5173/*", ", ".join(browser.get("redirectUris", [])))
row("mcp-browser requires S256", "S256", browser_attributes.get("pkce.code.challenge.method", ""))
row("mcp-browser default scopes", "basic, rma:read", ", ".join(sorted(browser.get("defaultClientScopes", []))))

read_scope = scopes.get("rma:read", {})
row("rma:read appears in the token scope", "true", read_scope.get("attributes", {}).get("include.in.token.scope", ""))
row(
    "rma:read adds the transport's audience",
    "aienabledrma-mcp",
    mapper("rma:read", "oidc-audience-mapper").get("config", {}).get("included.custom.audience", ""),
)

# The unmask tool's grant. It is a separate scope from the read one so that reading masked data
# and reading one unmasked record are separate permissions, and the machine client may request it
# without holding it by default.
support_scope = scopes.get("rma:support", {})
row("rma:support appears in the token scope", "true", support_scope.get("attributes", {}).get("include.in.token.scope", ""))
row(
    "mcp-agent may request rma:support",
    "rma:support",
    ", ".join(sorted(agent.get("optionalClientScopes", []))),
)
row(
    "mcp-agent does not hold rma:support by default",
    "basic, rma:read",
    ", ".join(sorted(agent.get("defaultClientScopes", []))),
)

basic_scope = scopes.get("basic", {})
row("basic adds the subject", "oidc-sub-mapper", mapper("basic", "oidc-sub-mapper").get("protocolMapper", ""))
row("basic is not a token scope", "false", basic_scope.get("attributes", {}).get("include.in.token.scope", ""))
row(
    "basic adds the user name",
    "oidc-usermodel-property-mapper",
    mapper("basic", "oidc-usermodel-property-mapper").get("protocolMapper", ""),
)

demo = users[0] if users else {}
row("browser-demo is present", "yes", "yes" if demo.get("username") == "browser-demo" else "no")
row("browser-demo is enabled", "yes", "yes" if demo.get("enabled") else "no")
row("browser-demo has no required actions", "", ", ".join(demo.get("requiredActions") or []))
row("browser-demo has a password", "password", ", ".join(c.get("type", "") for c in credentials))

# The operator back office (P10). The host's policy reads a flat 'roles' claim, and Keycloak's
# default role claim is nested under realm_access, so the client scope's mapper is as much a part
# of this gate as the client and the user are: without it a correct sign-in is denied every page.
operator_client = clients.get("rma-operator", {})
row("rma-operator is present", "yes", "yes" if operator_client else "no")
row("rma-operator is confidential", "yes", "yes" if operator_client and not operator_client.get("publicClient") else "no")
row(
    "rma-operator runs the authorization-code flow",
    "yes",
    "yes" if operator_client.get("standardFlowEnabled") else "no",
)
row("rma-operator redirect", "http://localhost:5000/Operator/signin-oidc", ", ".join(operator_client.get("redirectUris", [])))
row(
    "rma-operator default scopes",
    "basic, operator-roles",
    ", ".join(sorted(operator_client.get("defaultClientScopes", []))),
)
row(
    "operator-roles flattens realm roles into the roles claim",
    "roles",
    mapper("operator-roles", "oidc-usermodel-realm-role-mapper").get("config", {}).get("claim.name", ""),
)
row("operator-demo is present", "yes", "yes" if operator.get("username") == "operator-demo" else "no")
row(
    "operator-demo has the rma:operator realm role",
    "rma:operator",
    ", ".join(sorted(role.get("name", "") for role in operator_roles if role.get("name") == "rma:operator")),
)
PYTHON
}

realm_shape > "$WORK/shape.tsv"

# The row count is a check of its own. A python error writes nothing to the table, and a loop over an
# empty table is a run with no failures — the gate would report success for having checked nothing.
ROWS=$(wc -l < "$WORK/shape.tsv" | tr -d ' ')
check "realm shape rows produced" 31 "$ROWS"

# The table is written by python, which on Windows ends its lines with CRLF; the escape is stripped
# so a passing value does not compare unequal to itself.
while IFS=$'	' read -r name expected actual; do
    check "${name%$''}" "${expected%$''}" "${actual%$''}"
done < "$WORK/shape.tsv"

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

# The unmask tool's grant is a scope the token has to ask for. A realm that does not define it
# leaves the tool permanently refused, and that refusal is a sentence in a tool result rather than
# a 403, so nothing else in this gate would notice. This mints the token the tool's check looks for
# and asserts the scope arrived; whether the caller can then read a record is the suite's assertion.
SUPPORT_RESPONSE=$(kc_token \
    -d 'grant_type=client_credentials' \
    -d 'client_id=mcp-agent' \
    -d 'client_secret=mcp-agent-secret' \
    -d 'scope=rma:read rma:support')

check "support token endpoint answers" 200 "$(status_of "$SUPPORT_RESPONSE")"

SUPPORT_TOKEN=$(body_of "$SUPPORT_RESPONSE" | "$PY" -c 'import json,sys;print(json.load(sys.stdin).get("access_token",""))')

if [ -z "$SUPPORT_TOKEN" ]; then
    fail "support token present" "rma:support was requested and not granted: $(body_of "$SUPPORT_RESPONSE")"
else
    pass "support token present"
    check_contains "support token carries rma:support" "rma:support" "$(claim "$SUPPORT_TOKEN" scope)"
    check_contains "support token keeps the transport's audience" "aienabledrma-mcp" "$(claim "$SUPPORT_TOKEN" aud)"
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
echo "== tokens the transport must refuse"

# The realm carries a client whose tokens live one second, so the expiry case costs a minute rather
# than five, and whose optional scopes let the gate mint a token with the wrong audience or with the
# right audience and no read scope. A gate that only showed the accepted token would pass with token
# validation switched off; these three are what make "fails closed" measurable.
negative_token() {
    local scope=$1 response

    response=$(kc_token         -d 'grant_type=client_credentials'         -d 'client_id=mcp-negative'         -d 'client_secret=mcp-negative-secret'         -d "scope=$scope")

    body_of "$response" | "$PY" -c 'import json,sys;print(json.load(sys.stdin).get("access_token",""))'
}

# The same request the accepted token made; only the token differs.
initialize_with() {
    curl -sS -o "$BODY" -w '%{http_code}'         -X POST "$MCP_URL/mcp"         -H "Authorization: Bearer $1"         -H 'Content-Type: application/json'         -H 'Accept: application/json, text/event-stream'         -d "$INITIALIZE"
}

WRONG_AUDIENCE=$(negative_token 'audience-wrong')
check_contains "wrong-audience token names another resource" "aienabledrma-not-mcp" "$(claim "$WRONG_AUDIENCE" aud)"
check "a token for another audience is refused" 401 "$(initialize_with "$WRONG_AUDIENCE")"

NO_SCOPE=$(negative_token 'audience-aienabledrma-mcp')
check_contains "scope-less token names the resource" "aienabledrma-mcp" "$(claim "$NO_SCOPE" aud)"
if printf '%s' "$(claim "$NO_SCOPE" scope)" | grep -qw 'rma:read'; then
    fail "scope-less token has no rma:read" "it carries rma:read, so the refusal below would prove nothing"
else
    pass "scope-less token has no rma:read"
fi
check "a token without rma:read is refused" 403 "$(initialize_with "$NO_SCOPE")"

EXPIRING=$(negative_token 'rma:read')
check "a fresh short-lived token is accepted" 200 "$(initialize_with "$EXPIRING")"
echo "note  waiting 65s so the one-second token outlives the transport's one-minute clock skew"
sleep 65
check "the same token is refused once expired" 401 "$(initialize_with "$EXPIRING")"

echo
echo "== operator sign-in (rma-operator)"

# The operator back office (P10) is the one authenticated surface in the product, and until now no
# gate minted a token for it: the realm had no operator client, no rma:operator role and no mapper
# that would produce the claim the host's policy reads. This drives the password grant the committed
# realm enables for the gate, and asserts the claim, not just the sign-in.
OPERATOR_RESPONSE=$(kc_token \
    -d 'grant_type=password' \
    -d 'client_id=rma-operator' \
    -d 'client_secret=rma-operator-secret' \
    -d 'username=operator-demo' \
    -d 'password=operator-demo' \
    -d 'scope=openid')

check "operator token endpoint answers" 200 "$(status_of "$OPERATOR_RESPONSE")"

OPERATOR_TOKEN=$(body_of "$OPERATOR_RESPONSE" | "$PY" -c 'import json,sys;print(json.load(sys.stdin).get("access_token",""))')

if [ -z "$OPERATOR_TOKEN" ]; then
    fail "operator access token present" "the token response has no access_token: $(body_of "$OPERATOR_RESPONSE")"
else
    pass "operator access token present"

    # Both the subject and the name: the sub is what the audit trail must never fall back to when a
    # username is available, and an imported realm has no built-in profile scope, so the name comes
    # from the mapper this realm declares on 'basic'.
    check_nonempty "operator access token names its subject" "$(claim "$OPERATOR_TOKEN" sub)"
    check "operator access token names the operator" "operator-demo" "$(claim "$OPERATOR_TOKEN" preferred_username)"
    check_contains "operator token carries rma:operator in a flat roles claim" "rma:operator" "$(claim "$OPERATOR_TOKEN" roles)"
fi

# The role has to come from the person, not from the client: a second sign-in through the same
# client as a user without the role must not produce it.
NON_OPERATOR_RESPONSE=$(kc_token \
    -d 'grant_type=password' \
    -d 'client_id=rma-operator' \
    -d 'client_secret=rma-operator-secret' \
    -d 'username=browser-demo' \
    -d 'password=browser-demo' \
    -d 'scope=openid')

NON_OPERATOR_TOKEN=$(body_of "$NON_OPERATOR_RESPONSE" | "$PY" -c 'import json,sys;print(json.load(sys.stdin).get("access_token",""))')

if printf '%s' "$(claim "$NON_OPERATOR_TOKEN" roles)" | grep -qw 'rma:operator'; then
    fail "a user without the role gets no rma:operator" "the browser-demo token carries the operator role"
else
    pass "a user without the role gets no rma:operator"
fi

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
