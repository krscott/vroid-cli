#!/usr/bin/env bash
# Drive a real session through edit, save, render, restart, reopen, and a
# template round trip, checking R-EDIT-1, R-EDIT-3, R-EDIT-5, R-EDIT-6,
# R-EDIT-7, R-VIEW-1, and R-VIEW-2.
# Needs graphics access.
set -euo pipefail
shopt -s failglob

usage() {
    printf '%s\n' \
        "Usage: $0 SESSION SOURCE.vroid" \
        '' \
        'Opens SOURCE in SESSION, dumps its template, edits one Body and two Face' \
        'parameters in one request, saves a copy, renders each view, restarts,' \
        'checks the saved values, and applies the original template. SESSION' \
        'must be stopped and past its first-run dialogs. Set VROID_CLI to choose' \
        'the vroid-cli binary.'
}

fatal_usage() {
    if (($# > 0)); then
        printf '%s\n\n' "$*" >&2
    fi
    usage >&2
    exit 2
}

fatal() {
    printf 'gui-smoke: %s\n' "$*" >&2
    exit 1
}

if [[ ${1:-} == -h || ${1:-} == --help ]]; then
    usage
    exit 0
fi
(($# == 2)) || fatal_usage 'Expected SESSION and SOURCE.vroid'
session=$1
source=$(realpath -- "$2")
[[ -f $source ]] || fatal_usage "No such project: $2"
cli=${VROID_CLI:-vroid-cli}
for command in jq python3 framewisp cmp; do
    command -v "$command" >/dev/null || fatal "Missing required command: $command"
done

vroid() {
    "$cli" "$session" "$1"
}

state=$(vroid '{"method": "status"}')
[[ $(jq .app_running <<<"$state") == false ]] || fatal "Stop session $session first"
out=$(mktemp -d "$(jq -r .directory <<<"$state")/smoke.XXXXXX")
edited=$out/edited.vroid
printf 'gui-smoke: artifacts in %s\n' "$out" >&2

# Add one value to the apply request on stdin.
with_value() {
    jq -c --arg section "$1" --arg category "$2" --arg label "$3" --argjson value "$4" \
        '.[$section][$category][$label] = $value'
}

# Print the current value of a parameter as JSON.
value_of() {
    jq -cn --arg section "$1" --arg category "$2" \
        '{method: "parameters", section: $section, category: $category}' |
        vroid - |
        jq -e --arg label "$3" '[.[] | select(.label == $label)] | if length == 1 then .[0].value else error("expected one \($label)") end'
}

# Choose a new in-range value that differs from the current one.
pick() {
    jq -n --argjson current "$1" 'if $current == 0.125 then -0.125 else 0.125 end'
}

same() {
    jq -en --argjson a "$1" --argjson b "$2" '(($a - $b) | fabs) < 0.0001' >/dev/null
}

input_events() {
    local log
    log=$(vroid '{"method": "status"}' | jq -r .desktop)/inputs.jsonl
    if [[ -f $log ]]; then
        wc -l <"$log"
    else
        printf '0\n'
    fi
}

# launch's interop check reads the BepInEx log of the boot that reported ready,
# so that log must name the boot by the time launch returns.
launch() {
    local launched
    launched=$(vroid '{"method": "launch"}')
    grep -qF "boot $(jq -r .bridge.boot <<<"$launched")" "$(jq -r .directory <<<"$launched")/bepinex/LogOutput.log" ||
        fatal "BepInEx's log did not name the bridge's boot when launch returned"
}

rebuilt() {
    vroid '{"method": "status"}' | jq .bridge.rebuilt
}

modified() {
    vroid '{"method": "status"}' | jq .bridge.modified
}

screenshot() {
    framewisp "$(vroid '{"method": "status"}' | jq -r .desktop)" screenshot "$1" >/dev/null
}

# Render one view: VIEW FRAME WIDTH HEIGHT PATH.
render() {
    jq -cn --arg view "$1" --arg frame "$2" --argjson width "$3" --argjson height "$4" --arg path "$5" \
        '{method: "render", view: $view, frame: $frame, width: $width, height: $height, path: $path}' |
        vroid -
}

# Check a rendered PNG: PATH FRAME WIDTH HEIGHT. Its size must match, it must
# show the character, and the edges that frame the character must be
# background: every edge for a body; for a head, whose frame cuts through the
# neck and may show the shoulders, the top and the upper three quarters of each
# side, which hold the ears.
check_image() {
    python3 - "$@" <<'PY' || fatal "$1 is not a framed render; see above"
import struct
import sys
import zlib

path, frame, width, height = sys.argv[1], sys.argv[2], int(sys.argv[3]), int(sys.argv[4])
data = open(path, "rb").read()
if data[:8] != b"\x89PNG\r\n\x1a\n":
    sys.exit(f"{path}: not a PNG")
chunks, at = {}, 8
while at < len(data):
    length, kind = struct.unpack(">I4s", data[at : at + 8])
    chunks[kind] = chunks.get(kind, b"") + data[at + 8 : at + 8 + length]
    at += 12 + length
w, h, depth, colour, _, _, interlace = struct.unpack(">IIBBBBB", chunks[b"IHDR"])
if (w, h) != (width, height):
    sys.exit(f"{path}: {w}x{h}, expected {width}x{height}")
if depth != 8 or colour not in (2, 6) or interlace:
    sys.exit(f"{path}: unexpected PNG format {depth}/{colour}/{interlace}")
step = 3 if colour == 2 else 4
raw, stride = zlib.decompress(chunks[b"IDAT"]), w * step
rows, previous = [], bytearray(stride)
for y in range(h):
    kind, line = raw[y * (stride + 1)], bytearray(raw[y * (stride + 1) + 1 : (y + 1) * (stride + 1)])
    for x in range(stride):
        left = line[x - step] if x >= step else 0
        up, corner = previous[x], previous[x - step] if x >= step else 0
        if kind == 1:
            line[x] = (line[x] + left) & 255
        elif kind == 2:
            line[x] = (line[x] + up) & 255
        elif kind == 3:
            line[x] = (line[x] + (left + up) // 2) & 255
        elif kind == 4:
            p = left + up - corner
            pa, pb, pc = abs(p - left), abs(p - up), abs(p - corner)
            line[x] = (line[x] + (left if pa <= pb and pa <= pc else up if pb <= pc else corner)) & 255
    rows.append([bytes(line[x : x + 3]) for x in range(0, stride, step)])
    previous = line
background = rows[0][0]
sides = rows[: h * 3 // 4] if frame == "head" else rows
edges = {"top": rows[0], "left": [r[0] for r in sides], "right": [r[-1] for r in sides]}
if frame == "body":
    edges["bottom"] = rows[-1]
for name in edges:
    if any(pixel != background for pixel in edges[name]):
        sys.exit(f"{path}: the character touches the {name} edge")
shown = sum(pixel != background for row in rows for pixel in row)
if shown < w * h // 20:
    sys.exit(f"{path}: only {shown} pixels show the character")
PY
}

# Two edits share a category, so one request changes several of its values.
edits=(
    'Body|Whole Body|Head Size'
    'Face|Face Sets|Eye Size X'
    'Face|Face Sets|Eye Size Y'
)

launch
inputs_before=$(input_events)
jq -cn --arg path "$source" '{method: "open", path: $path}' | vroid - >/dev/null
vroid '{"method": "dump"}' >"$out/original.json"

declare -A expected
request='{"method": "apply"}'
for edit in "${edits[@]}"; do
    IFS='|' read -r section category label <<<"$edit"
    expected[$edit]=$(pick "$(value_of "$section" "$category" "$label")")
    request=$(with_value "$section" "$category" "$label" "${expected[$edit]}" <<<"$request")
done
before=$(rebuilt)
vroid "$request" | jq -e --argjson count "${#edits[@]}" '.changed == $count' >/dev/null ||
    fatal "apply did not change ${#edits[@]} values"
vroid '{"method": "status"}' | jq -e --argjson before "$before" --argjson count "${#edits[@]}" \
    '.bridge.rebuilt >= $before + $count and .bridge.updating == false' >/dev/null ||
    fatal "apply returned before a completed rebuild per value"
for edit in "${edits[@]}"; do
    IFS='|' read -r section category label <<<"$edit"
    actual=$(value_of "$section" "$category" "$label")
    same "$actual" "${expected[$edit]}" || fatal "$label read back $actual after apply ${expected[$edit]}"
done

# A value above its maximum, an unknown parameter, and an unknown category
# refuse the whole request, and each is reported.
IFS='|' read -r section category label <<<"${edits[0]}"
IFS='|' read -r other_section other_category other_label <<<"${edits[1]}"
max=$(jq -cn --arg section "$section" --arg category "$category" \
    '{method: "parameters", section: $section, category: $category}' |
    vroid - | jq --arg label "$label" '.[] | select(.label == $label) | .max')
refused=$(with_value "$section" "$category" "$label" "$(jq -n --argjson max "$max" '$max + 1')" <<<'{"method": "apply"}' |
    with_value "$other_section" "$other_category" "$other_label" "$(pick "${expected[${edits[1]}]}")" |
    with_value "$other_section" "$other_category" 'No Such Parameter' 0 |
    with_value "$other_section" 'No Such Category' 'Any Parameter' 0)
if vroid "$refused" >/dev/null 2>"$out/bounds.err"; then
    fatal "apply above the maximum succeeded"
fi
for reason in 'is outside' 'No Such Parameter: unknown parameter' 'No Such Category: unknown category' 'Nothing changed'; do
    grep -qF "$reason" "$out/bounds.err" || fatal "refusal lacks '$reason': $(<"$out/bounds.err")"
done
for edit in "${edits[@]}"; do
    IFS='|' read -r section category label <<<"$edit"
    actual=$(value_of "$section" "$category" "$label")
    same "$actual" "${expected[$edit]}" || fatal "$label changed to $actual after a refused apply"
done

# Rendering keeps unsaved changes unsaved (R-VIEW-2).
render front body 64 128 "$out/unsaved.png" >/dev/null
[[ $(modified) == true ]] || fatal "rendering cleared the unsaved state"

save=$(jq -cn --arg path "$edited" '{method: "save", path: $path}')
vroid "$save" >/dev/null
if vroid "$save" >/dev/null 2>&1; then
    fatal "second save to $edited succeeded"
fi

# Rendering shows the whole character (R-VIEW-1) and changes nothing in the
# app: values, unsaved state, and the editor's view stay the same (R-VIEW-2).
vroid '{"method": "dump"}' >"$out/before-render.json"
screenshot "$out/before-render.png"
renders=(
    'front|body|512|1024'
    'three_quarter|body|512|1024'
    'side|body|512|1024'
    'back|body|512|1024'
    'front|head|512|512'
    'side|head|512|512'
)
for item in "${renders[@]}"; do
    IFS='|' read -r view frame width height <<<"$item"
    image=$out/render-$view-$frame.png
    render "$view" "$frame" "$width" "$height" "$image" >/dev/null
    check_image "$image" "$frame" "$width" "$height"
done
image=$out/render-front-body.png
cp "$image" "$out/render-front-body.copy"
if render front body 512 1024 "$image" >/dev/null 2>&1; then
    fatal "second render to $image succeeded"
fi
cmp -s "$image" "$out/render-front-body.copy" || fatal "a refused render changed $image"
[[ $(modified) == false ]] || fatal "rendering marked the project modified"
vroid '{"method": "dump"}' >"$out/after-render.json"
cmp -s "$out/before-render.json" "$out/after-render.json" ||
    fatal "dump changed while rendering; see $out/after-render.json"
screenshot "$out/after-render.png"
cmp -s "$out/before-render.png" "$out/after-render.png" ||
    fatal "the editor's view changed while rendering; compare $out/before-render.png and $out/after-render.png"
inputs_after=$(input_events)
((inputs_after == inputs_before)) || fatal "input log grew from $inputs_before to $inputs_after events"

vroid '{"method": "stop"}' >/dev/null
launch
inputs_before=$(input_events)
if render front body 64 64 "$out/unopened.png" >/dev/null 2>"$out/unopened.err"; then
    fatal "render without an open project succeeded"
fi
grep -qiF 'open a project' "$out/unopened.err" || fatal "render without a project: $(<"$out/unopened.err")"
shopt -u failglob
leftovers=("$out"/unopened.png "$out"/.vroid-render-*)
shopt -s failglob
for leftover in "${leftovers[@]}"; do
    [[ ! -e $leftover ]] || fatal "a failed render left $leftover"
done
jq -cn --arg path "$edited" '{method: "open", path: $path}' | vroid - >/dev/null
for edit in "${edits[@]}"; do
    IFS='|' read -r section category label <<<"$edit"
    actual=$(value_of "$section" "$category" "$label")
    same "$actual" "${expected[$edit]}" || fatal "$label reopened as $actual, saved ${expected[$edit]}"
done

# The template dumped before the edits restores every value in a new app instance.
vroid "@$out/original.json" >/dev/null
vroid '{"method": "dump"}' >"$out/restored.json"
jq -e --slurpfile original "$out/original.json" '. == $original[0]' "$out/restored.json" >/dev/null ||
    fatal "dump after applying $out/original.json differs; see $out/restored.json"
inputs_after=$(input_events)
((inputs_after == inputs_before)) || fatal "input log grew from $inputs_before to $inputs_after events"
vroid '{"method": "stop", "force": true}' >/dev/null

jq -n --arg project "$edited" '{ok: true, project: $project}'
