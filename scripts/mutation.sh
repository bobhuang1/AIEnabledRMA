#!/usr/bin/env bash
#
# Mutation testing over the code where a wrong branch is a wrong decision: the policy evaluator, the
# operator and payment operations, and the redactor. Run nightly by .github/workflows/mutation.yml.
#
# Every batch in this repository has been mutation-checked by hand — break the code, watch the test
# go red, put it back — and that is exactly the step that disappears when a change is rushed. This
# runs the same idea mechanically, counts what survived, and compares the number with a committed
# budget that must not rise.
#
# Usage:
#   scripts/mutation.sh              # both projects
#   MUTATION_PROJECTS=privacy scripts/mutation.sh
#
# Exit status:
#   0  survivors at or below the budget in mutation/baseline.json
#   1  a survivor count rose (or the run could not be counted at all)
#
# Requirements: docker. Stryker runs inside the pinned SDK image, the same way every other gate here
# runs, and needs a PostgreSQL for the live suites that cover the operations.

set -euo pipefail

REPO_ROOT=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
cd "$REPO_ROOT"

SDK_IMAGE=${SDK_IMAGE:-mcr.microsoft.com/dotnet/sdk:10.0}
PG_IMAGE=${PG_IMAGE:-pgvector/pgvector:pg16}
NETWORK=rma-mutation
DATABASE=rma-mutation-pg
BASELINE=mutation/baseline.json
WORK=$(mktemp -d)
TREE="$WORK/tree"
PROJECTS=${MUTATION_PROJECTS:-"domain privacy"}

fail() { printf 'FAIL  %s\n        %s\n' "$1" "${2:-}" >&2; exit 1; }

cleanup() {
    if [ "${KEEP:-0}" = 1 ]; then
        printf '\nkept: container %s, working directory %s\n' "$DATABASE" "$WORK"
        return
    fi

    docker rm -f "$DATABASE" >/dev/null 2>&1 || true
    docker network rm "$NETWORK" >/dev/null 2>&1 || true

    # The container writes its reports as root; deleting through the mount from a non-root user
    # fails, and under `set -e` an EXIT trap that fails reports a green run as a red one.
    if [ -d "$WORK" ]; then
        docker run --rm -v "$(winpath "$WORK"):/work" "$SDK_IMAGE" \
            bash -lc 'find /work -mindepth 1 -delete' >/dev/null 2>&1 || true
        rm -rf "$WORK" >/dev/null 2>&1 || true
    fi
}

trap cleanup EXIT

winpath() {
    if command -v cygpath >/dev/null 2>&1; then
        cygpath -w "$1"
    else
        printf '%s' "$1"
    fi
}

command -v docker >/dev/null 2>&1 || fail "mutation" "docker is required"
[ -f "$BASELINE" ] || fail "mutation" "$BASELINE is missing: the run has nothing to compare against"

printf '\n== mutation: %s, budget in %s\n' "$PROJECTS" "$BASELINE"

# The operations suites talk to PostgreSQL, and a mutant they would have killed cannot be called
# killed by a suite that skipped them.
docker network create "$NETWORK" >/dev/null 2>&1 || true
docker rm -f "$DATABASE" >/dev/null 2>&1 || true
docker run -d --name "$DATABASE" --network "$NETWORK" \
    -e POSTGRES_USER=rmauser \
    -e POSTGRES_PASSWORD=rmapassword \
    -e POSTGRES_DB=rmadev \
    "$PG_IMAGE" >/dev/null

ready=no
for _ in $(seq 1 60); do
    if docker exec "$DATABASE" pg_isready -U rmauser -d rmadev >/dev/null 2>&1; then
        ready=yes
        break
    fi
    sleep 1
done
[ "$ready" = yes ] || fail "mutation" "PostgreSQL never accepted connections, so the live suites would skip"

mkdir -p "$TREE"
tar -cf - --exclude=./.git --exclude=./data --exclude='*/bin' --exclude='*/obj' . | tar -xf - -C "$TREE"

for project in $PROJECTS; do
    config="mutation/stryker-$project.json"
    [ -f "$config" ] || fail "mutation" "no config for '$project' ($config)"

    printf '\n-- %s: %s\n' "$project" "$config"

    if ! MSYS_NO_PATHCONV=1 docker run --rm --network "$NETWORK" \
        -v "$(winpath "$TREE"):/src" \
        -v rma-verify-all-nuget:/root/.nuget/packages \
        -w /src/tests/AIEnabledRma.Tests \
        -e "RMA_TEST_CONNECTION=Host=$DATABASE;Port=5432;Database=rmadev;Username=rmauser;Password=rmapassword" \
        "$SDK_IMAGE" bash -lc "
            set -euo pipefail
            dotnet tool restore >/dev/null
            dotnet stryker -f /src/$config --output /src/mutation-reports/$project --verbosity warning
        "; then
        fail "$project" "stryker did not finish"
    fi
done

# Count from the JSON reports rather than from the console summary: the number this gate compares
# has to be the number the report holds, and a log line is one formatting change away from lying.
python - "$TREE" "$PROJECTS" <<'PY' > "$WORK/counts.env"
import json
import pathlib
import sys

tree = pathlib.Path(sys.argv[1])
projects = sys.argv[2].split()

survived = 0
killed = 0
no_coverage = 0
files = 0

for project in projects:
    reports = sorted(tree.glob(f"mutation-reports/{project}/**/*.json"))
    if not reports:
        raise SystemExit(f"no JSON report for {project} under mutation-reports/{project}")

    report = json.loads(reports[-1].read_text(encoding="utf-8"))
    for path, entry in report.get("files", {}).items():
        files += 1
        for mutant in entry.get("mutants", []):
            status = mutant.get("status")
            if status == "Survived":
                survived += 1
            elif status == "Killed":
                killed += 1
            elif status == "NoCoverage":
                no_coverage += 1

print(f"SURVIVED={survived}")
print(f"KILLED={killed}")
print(f"NO_COVERAGE={no_coverage}")
print(f"FILES={files}")
PY

cat "$WORK/counts.env"
. "$WORK/counts.env"

[ "${FILES:-0}" -gt 0 ] || fail "mutation" "the reports covered no files, so nothing was mutated"

budget_survived=$(python -c "import json;print(json.load(open('$BASELINE'))['survivors'])")
budget_uncovored=$(python -c "import json;print(json.load(open('$BASELINE'))['noCoverage'])")

printf '\n  killed:         %s\n' "${KILLED:-0}"
printf '  survivors:      %s (budget %s)\n' "$SURVIVED" "$budget_survived"
printf '  never executed: %s (budget %s)\n' "$NO_COVERAGE" "$budget_uncovored"

status=0
if [ "$SURVIVED" -gt "$budget_survived" ]; then
    printf 'FAIL  surviving mutants rose: %s > %s\n' "$SURVIVED" "$budget_survived" >&2
    status=1
fi

if [ "$NO_COVERAGE" -gt "$budget_uncovored" ]; then
    printf 'FAIL  mutants no test executed rose: %s > %s\n' "$NO_COVERAGE" "$budget_uncovored" >&2
    status=1
fi

if [ "$status" -ne 0 ]; then
    printf '\nThe budget in %s is the number this run must not exceed. Kill the mutant with a test,\n' "$BASELINE"
    printf 'or raise the budget in the same change and say in the message why the extra survivor is\n'
    printf 'acceptable — a budget that moves silently is the hand-run step this gate replaced.\n' >&2
    exit "$status"
fi

printf '\nmutation survivors within budget\n'
