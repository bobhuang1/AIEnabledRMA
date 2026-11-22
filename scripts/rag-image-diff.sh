#!/usr/bin/env bash
#
# Builds the RAG image at two commits and prints what changed between them, in bytes.
#
# The "the ORM left the RAG image, and here is what that bought" claim in progress.md was measured by
# hand from remembered commands, so rechecking it meant retyping them and being compared against a
# number measured a slightly different way. This is the same measurement with its method written down:
# both trees come from `git archive` of the named commit (uncommitted work cannot leak in), each is
# built through its own src/AIEnabledRma.Rag/Dockerfile, and every byte figure is a sum over the
# image's own layer blobs from `docker save` — not the rounded `docker images` column, which cannot
# show a 2% move. "Uncompressed" means the saved layer blob decompressed: docker stores a built layer
# compressed, so reporting the blob itself as uncompressed puts the compression figure on both lines.
#
# Usage:
#   scripts/rag-image-diff.sh 05f80b5 8e74e61
#
# Prints:
#   - the app layer (the one carrying AIEnabledRma.Rag.dll): uncompressed and compressed bytes,
#     before -> after, with the delta and the percentage
#   - the whole image the same way, plus docker's own rounded size for reference
#   - which layers are byte-identical, so "only the COPY /app layer differs" is checked, not assumed
#   - the assembly diff: /app assemblies removed, added and resized
#
# Environment:
#   KEEP_IMAGES=1  leave the two built images behind (they are tagged rma-rag-diff:<rev>)
#
# Exit status: 0 with the numbers printed; 1 when a revision cannot be archived, an image does not
# build, or the app layer cannot be located. The numbers are output, not a pass/fail budget.
#
# Requirements: docker, git, tar, python.

set -euo pipefail

REPO_ROOT=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
cd "$REPO_ROOT"

DOCKERFILE=src/AIEnabledRma.Rag/Dockerfile

usage() {
    printf 'usage: scripts/rag-image-diff.sh <before-rev> <after-rev>\n' >&2
    exit 2
}

[ "$#" -eq 2 ] || usage
BEFORE=$1
AFTER=$2

fail() { printf 'FAIL  %s\n        %s\n' "$1" "${2:-}" >&2; exit 1; }

command -v docker >/dev/null 2>&1 || fail "rag-image-diff" "docker is required"
command -v git >/dev/null 2>&1 || fail "rag-image-diff" "git is required"

# Not just "python3 exists": on Windows `python3` is often the Store's stub, which exists, runs, and
# exits 49 with "Python was not found". A candidate counts only if it can import the one module the
# sums need.
PYTHON=""
for candidate in python3 python; do
    if command -v "$candidate" >/dev/null 2>&1 && "$candidate" -c 'import gzip' >/dev/null 2>&1; then
        PYTHON=$candidate
        break
    fi
done
[ -n "$PYTHON" ] || fail "rag-image-diff" "a working python is required for the byte sums"

WORK=$(mktemp -d)
IMAGES=()

cleanup() {
    if [ "${KEEP_IMAGES:-0}" = 1 ]; then
        printf '\nkept: %s in %s\n' "${IMAGES[*]:-no images}" "$WORK"
        return
    fi

    for image in "${IMAGES[@]:-}"; do
        [ -n "$image" ] && docker rmi -f "$image" >/dev/null 2>&1 || true
    done

    rm -rf "$WORK" >/dev/null 2>&1 || true
}

trap cleanup EXIT

# docker.exe reads host paths, not the shell's /c/... form.
hostpath() {
    if command -v cygpath >/dev/null 2>&1; then cygpath -w "$1"; else printf '%s' "$1"; fi
}

# One tree per commit, exactly what the repository held at that commit.
prepare() {
    local rev=$1 name=$2 tree
    tree="$WORK/$name"

    git rev-parse --verify --quiet "$rev^{commit}" >/dev/null \
        || fail "rag-image-diff" "'$rev' is not a commit in this repository"

    mkdir -p "$tree"
    git archive --format=tar "$rev" | tar -xf - -C "$tree" \
        || fail "rag-image-diff" "could not archive '$rev'"

    printf '%s' "$tree"
}

build() {
    local rev=$1 tree=$2 tag
    tag="rma-rag-diff:$(printf '%s' "$rev" | tr -c 'A-Za-z0-9_.-' '_')"
    IMAGES+=("$tag")

    printf '\n== building %s from %s\n' "$tag" "$rev" >&2

    # The Dockerfile comes from the commit being built, by absolute path: `-f` is resolved against the
    # current directory, so a repo-relative path would silently build both commits through HEAD's
    # Dockerfile and measure the wrong thing. Each tree's own Dockerfile is also the honest choice —
    # the question is what that commit produced.
    #
    # Plain `docker build`, not buildx: this is a measurement of the image, and the layer cache from
    # the first build is what makes the second one's base layers byte-identical.
    docker build -f "$(hostpath "$tree/$DOCKERFILE")" -t "$tag" --build-arg "GIT_REVISION=$rev" "$(hostpath "$tree")" \
        >"$WORK/$tag.build.log" 2>&1 \
        || { tail -15 "$WORK/$tag.build.log" >&2; fail "rag-image-diff" "'$rev' did not build"; }

    printf '%s' "$tag"
}

# The bytes have to come from the image's own layers. `docker image inspect .Size` is rounded, and the
# claim being rechecked is a 2% move that rounding can hide entirely.
save() {
    local tag=$1 name=$2
    docker save -o "$(hostpath "$WORK/$name.tar")" "$tag" \
        || fail "rag-image-diff" "could not save $tag"

    mkdir -p "$WORK/$name"
    tar -xf "$WORK/$name.tar" -C "$WORK/$name" \
        || fail "rag-image-diff" "could not unpack the saved image $tag"

    docker image inspect -f '{{json .RootFS.Layers}}' "$tag" >"$WORK/$name.layers.json" \
        || fail "rag-image-diff" "could not read the layer list of $tag"

    docker image inspect -f '{{.Size}}' "$tag" >"$WORK/$name.size"
}

before_tree=$(prepare "$BEFORE" before)
after_tree=$(prepare "$AFTER" after)

before_image=$(build "$BEFORE" "$before_tree")
after_image=$(build "$AFTER" "$after_tree")

save "$before_image" before
save "$after_image" after

"$PYTHON" - "$WORK" "$BEFORE" "$AFTER" "$before_image" "$after_image" <<'PY'
import gzip
import json
import os
import subprocess
import sys
import tarfile

work, before_rev, after_rev, before_image, after_image = sys.argv[1:6]

APP_DLL = "app/AIEnabledRma.Rag.dll"


def layers(name):
    root = os.path.join(work, name)
    manifest = json.load(open(os.path.join(root, "manifest.json"), encoding="utf-8"))[0]
    return [os.path.join(root, path) for path in manifest["Layers"]]


def sizes(path):
    """Uncompressed bytes of the layer and the bytes it is stored as.

    `docker save` writes each layer as the blob it was stored as, which is gzip for anything the
    builder or a registry produced. Reporting that blob as "uncompressed" would put the compressed
    figure on the wrong line and make the pair look like two measurements of the same bytes.
    """
    blob = open(path, "rb").read()
    try:
        return len(gzip.decompress(blob)), len(blob)
    except OSError:
        # A layer kept as a plain tar has no compression to undo.
        return len(blob), len(blob)


def app_layer(paths):
    for index, path in enumerate(paths):
        with tarfile.open(path) as archive:
            if APP_DLL in archive.getnames():
                return index
    return None


def digests(name):
    return json.load(open(os.path.join(work, name + ".layers.json"), encoding="utf-8"))


def app_files(image):
    out = subprocess.run(
        ["docker", "run", "--rm", "--entrypoint", "sh", image, "-c",
         'find /app -type f -printf "%s\\t%P\\n"'],
        check=True, capture_output=True, text=True).stdout

    files = {}
    for line in out.splitlines():
        size, _, path = line.partition("\t")
        files[path] = int(size)
    return files


def num(value):
    return f"{value:,}"


def delta(before, after):
    change = after - before
    percent = (change / before * 100) if before else 0.0
    # ASCII, so the output survives a Windows console's cp1252 as well as a pipe.
    return f"{'+' if change >= 0 else '-'}{num(abs(change))} ({percent:+.1f}%)"


before_layers = layers("before")
after_layers = layers("after")

before_index = app_layer(before_layers)
after_index = app_layer(after_layers)

if before_index is None or after_index is None:
    sys.exit(f"could not find the {APP_DLL} layer in "
             f"{before_rev if before_index is None else after_rev}")

before_app = sizes(before_layers[before_index])
after_app = sizes(after_layers[after_index])

before_all = [sizes(path) for path in before_layers]
after_all = [sizes(path) for path in after_layers]
before_total = (sum(size for size, _ in before_all), sum(size for _, size in before_all))
after_total = (sum(size for size, _ in after_all), sum(size for _, size in after_all))

before_digests = digests("before")
after_digests = digests("after")
identical = sum(1 for a, b in zip(before_digests, after_digests) if a == b)

print()
print(f"RAG image {before_rev} -> {after_rev}")
print("  built from git archive of each commit through src/AIEnabledRma.Rag/Dockerfile,"
      " bytes summed over each image's own layer blobs (docker save)")

print()
print(f"app layer (COPY --from=build /app, layer {before_index} -> {after_index})")
print(f"  uncompressed  {num(before_app[0]):>12} -> {num(after_app[0]):>12}  {delta(before_app[0], after_app[0])}")
print(f"  compressed    {num(before_app[1]):>12} -> {num(after_app[1]):>12}  {delta(before_app[1], after_app[1])}")

print()
print("whole image (sum of all layer blobs)")
print(f"  uncompressed  {num(before_total[0]):>12} -> {num(after_total[0]):>12}  {delta(before_total[0], after_total[0])}")
print(f"  compressed    {num(before_total[1]):>12} -> {num(after_total[1]):>12}  {delta(before_total[1], after_total[1])}")

before_rounded = int(open(os.path.join(work, "before.size")).read().strip() or 0)
after_rounded = int(open(os.path.join(work, "after.size")).read().strip() or 0)
print(f"  docker size   {num(before_rounded):>12} -> {num(after_rounded):>12}  "
      f"(rounded by docker; the layer sums above are the measurement)")

print()
if len(before_digests) == len(after_digests) and identical == len(before_digests):
    print(f"layers: all {len(before_digests)} identical — the trees differ but the images do not")
else:
    differing = [index for index, (a, b) in enumerate(zip(before_digests, after_digests)) if a != b]
    print(f"layers: {len(before_digests)} before, {len(after_digests)} after, {identical} identical, "
          f"differing at {differing or 'none'}")

    if differing and differing != [before_index] and differing != [after_index]:
        print(f"  note: the app layer is {before_index} -> {after_index}, so a layer outside it changed "
              f"too — the app-layer delta is not the whole story")

before_files = app_files(before_image)
after_files = app_files(after_image)


def assemblies(files):
    return {path: size for path, size in files.items() if path.endswith(".dll") and "/" not in path}


before_dlls = assemblies(before_files)
after_dlls = assemblies(after_files)

def top_level(files):
    return {path: size for path, size in files.items() if "/" not in path}


before_top = top_level(before_files)
after_top = top_level(after_files)

print()
print(f"/app: {len(before_top)} files, {len(before_dlls)} assemblies -> "
      f"{len(after_top)} files, {len(after_dlls)} assemblies")

removed = sorted(set(before_dlls) - set(after_dlls), key=lambda name: -before_dlls[name])
added = sorted(set(after_dlls) - set(before_dlls), key=lambda name: -after_dlls[name])
resized = sorted(
    (name for name in set(before_dlls) & set(after_dlls) if before_dlls[name] != after_dlls[name]),
    key=lambda name: -abs(after_dlls[name] - before_dlls[name]))

print("assemblies")
for name in removed:
    print(f"  removed  {name:<52} {num(before_dlls[name]):>10}")
for name in added:
    print(f"  added    {name:<52} {num(after_dlls[name]):>10}")
for name in resized:
    print(f"  resized  {name:<52} {num(before_dlls[name]):>10} -> {num(after_dlls[name])} "
          f"({delta(before_dlls[name], after_dlls[name])})")

if not (removed or added or resized):
    print("  none: every assembly is byte-for-byte the same size in both images")
PY
