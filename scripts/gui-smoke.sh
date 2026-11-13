#!/usr/bin/env bash
# Drive a real session through edit, save, render, restart, reopen, and a
# template round trip, then new characters and an imported accessory,
# checking R-EDIT-1, R-EDIT-3, R-EDIT-5, R-EDIT-6, R-EDIT-7, R-EDIT-8,
# R-EDIT-9, R-EDIT-11, R-EDIT-12, R-EDIT-13, R-EDIT-14 except its by-hand
# offline check, R-EDIT-15, R-EDIT-16, R-SES-4, R-VIEW-1, R-VIEW-2, and
# R-VIEW-4.
# Needs graphics access.
set -euo pipefail
shopt -s failglob

usage() {
    printf '%s\n' \
        "Usage: $0 SESSION SOURCE.vroid" \
        '' \
        'Opens SOURCE in SESSION, dumps its template, edits one Body and two Face' \
        'parameters and four colours (orange hair, green eyes) in one request,' \
        'then selects other Hair Front and Outfit Tops items and adds cat and' \
        'rabbit ears, moving, placing, reshaping, renaming, hiding, and locking' \
        'the cat ears, undoing and redoing both edits, undoes a removal of the' \
        'cat ears and a refused cat tail, recolours the new top, undoing that' \
        'and a later change of top, saves a copy, renders each view, draws the' \
        'thumbnails of those two categories, restarts, checks the saved values,' \
        'renders the cat ears hidden, then shown with their default name, and' \
        'applies the original template. It then creates a character from each base' \
        'twice, restarting between them, and imports the cat ears exported before' \
        'the restart into a new one. Editor screenshots before and after' \
        'the edit land in the artifact directory.' \
        'SESSION must be stopped. Set VROID_CLI to choose the vroid-cli binary.'
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

# Choose a colour that differs from the current one.
pick_colour() {
    jq -n --argjson current "$1" --arg colour "$2" --arg other "$3" \
        'if ($current | ascii_upcase) == $colour then $other else $colour end'
}

# Numbers match within float precision; colours match exactly, in any case.
same() {
    jq -en --argjson a "$1" --argjson b "$2" \
        'if ($a | type) == "string" then ($a | ascii_upcase) == ($b | ascii_upcase) else (($a - $b) | fabs) < 0.0001 end' \
        >/dev/null
}

# Print a category's `items` result.
items_of() {
    jq -cn --arg section "$1" --arg category "$2" '{method: "items", section: $section, category: $category}' | vroid -
}

# Choose a built-in item other than the category's current one.
pick_item() {
    items_of "$1" "$2" | jq -e '.selected as $selected | [.items[] | select(. != $selected)][0]'
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

# Apply the undo file an apply result names, and check that a dump matches
# the one taken before that apply (R-EDIT-11).
undo() {
    local file
    file=$(jq -er .undo <<<"$1")
    vroid "@$file" >/dev/null
    vroid '{"method": "dump"}' >"$out/undone.json"
    jq -e --slurpfile before "$2" '. == $before[0]' "$out/undone.json" >/dev/null ||
        fatal "dump after applying $file differs from $2; see $out/undone.json"
}

modified() {
    vroid '{"method": "status"}' | jq .bridge.modified
}

screenshot() {
    framewisp "$(vroid '{"method": "status"}' | jq -r .desktop)" screenshot "$1" >/dev/null
}

# Screenshot once the editor stops animating, such as its tab underline after
# a request switches sections: two consecutive screenshots must match.
settled_screenshot() {
    local attempt
    screenshot "$1"
    for attempt in {1..20}; do
        sleep 0.25
        screenshot "$1.next"
        if cmp -s "$1" "$1.next"; then
            rm "$1.next"
            return
        fi
        mv "$1.next" "$1"
    done
    fatal "the editor kept changing after $attempt screenshots; see $1"
}

# Render one view: VIEW FRAME WIDTH HEIGHT PATH.
render() {
    jq -cn --arg view "$1" --arg frame "$2" --argjson width "$3" --argjson height "$4" --arg path "$5" \
        '{method: "render", view: $view, frame: $frame, width: $width, height: $height, path: $path}' |
        vroid -
}

# Draw a category's item thumbnails: SECTION CATEGORY PATH.
thumbnails() {
    jq -cn --arg section "$1" --arg category "$2" --arg path "$3" \
        '{method: "thumbnails", section: $section, category: $category, path: $path}' |
        vroid -
}

# Check a PNG: render PATH FRAME WIDTH HEIGHT, or sheet PATH COUNT.
#
# A render's size must match, it must show the character, and the edges that
# frame the character must be background: every edge for a body; for a head,
# whose frame cuts through the neck and may show the shoulders, the top and the
# upper three quarters of each side, which hold the ears.
#
# A thumbnail sheet's size must fit COUNT items, and no thumbnail may be one
# flat colour.
check_png() {
    python3 - "$@" <<'PY' || fatal "$2 is not a valid $1; see above"
import struct
import sys
import zlib

check, path = sys.argv[1], sys.argv[2]
data = open(path, "rb").read()
if data[:8] != b"\x89PNG\r\n\x1a\n":
    sys.exit(f"{path}: not a PNG")
chunks, at = {}, 8
while at < len(data):
    length, kind = struct.unpack(">I4s", data[at : at + 8])
    chunks[kind] = chunks.get(kind, b"") + data[at + 8 : at + 8 + length]
    at += 12 + length
w, h, depth, colour, _, _, interlace = struct.unpack(">IIBBBBB", chunks[b"IHDR"])
if check == "render":
    frame, width, height = sys.argv[3], int(sys.argv[4]), int(sys.argv[5])
else:
    # Rows of 10 cells of 150x224 pixels, a 200-pixel thumbnail above its
    # label, with a 4-pixel gutter around each.
    count = int(sys.argv[3])
    width, height = min(count, 10) * 154 + 4, (count + 9) // 10 * 228 + 4
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
if check == "sheet":
    for index in range(count):
        x, top = 4 + index % 10 * 154, 4 + index // 10 * 228
        if len({pixel for row in rows[top : top + 200] for pixel in row[x : x + 150]}) < 2:
            sys.exit(f"{path}: thumbnail {index} is blank")
    sys.exit()
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
# Colours, each with a target and an alternative if it already has the target.
# Hair needs the shared hair colour and Base Hair's own. Distinct targets show
# the fields are independent. The last read back, Hair/Front, is the panel
# after.png shows; its Main Color field reads the hex set here (R-EDIT-8).
colours=(
    'Face|Irises|R Eye|#2E8B57|#228B22'
    'Face|Irises|L Eye|#3CB371|#32CD32'
    'Hair|Base Hair|Main Color|#FFA500|#FF9900'
    'Hair|Front|Main Color|#FF8C00|#FF7F00'
)

# Categories whose items the smoke test changes.
items=('Hair|Front' 'Outfit|Tops')
# Two accessories of one kind, which VRoid keeps in one group. The cat ears
# hang from another bone, which VRoid allows only while no other ears are worn,
# and are placed, reshaped, renamed, hidden, and locked. VRoid removes a locked
# accessory only once it is unlocked.
ears='[{"item": "CatEar", "Color": "#3A2A20", "bone": "J_Bip_C_Neck", "position": [0, 0.01, -0.005],
    "rotation": [0.0871557, 0, 0, 0.9961947], "scale": [1.25, 1.25, 1.25], "Ear Size (Bigger)": 50,
    "name": "Smoke Ears", "hidden": true, "locked": true},
    {"item": "RabbitEar", "Color": "#F0F0F0", "name": null}]'

launch
# The first launch creates a new session's directory.
out=$(mktemp -d "$(jq -r .directory <<<"$state")/smoke.XXXXXX")
edited=$out/edited.vroid
printf 'gui-smoke: artifacts in %s\n' "$out" >&2
inputs_before=$(input_events)
jq -cn --arg path "$source" '{method: "open", path: $path}' | vroid - >/dev/null
vroid '{"method": "dump"}' >"$out/original.json"
for item in "${items[@]}"; do
    IFS='|' read -r section category <<<"$item"
    items_of "$section" "$category" | jq .items >"$out/items-${section}-${category}.json"
done

screenshot "$out/before.png"

declare -A expected
request='{"method": "apply"}'
for edit in "${edits[@]}"; do
    IFS='|' read -r section category label <<<"$edit"
    expected[$edit]=$(pick "$(value_of "$section" "$category" "$label")")
    request=$(with_value "$section" "$category" "$label" "${expected[$edit]}" <<<"$request")
done
for colour in "${colours[@]}"; do
    IFS='|' read -r section category label target other <<<"$colour"
    edit="$section|$category|$label"
    edits+=("$edit")
    expected[$edit]=$(pick_colour "$(value_of "$section" "$category" "$label")" "$target" "$other")
    request=$(with_value "$section" "$category" "$label" "${expected[$edit]}" <<<"$request")
done
# Skin is one colour shown in several categories; the refusal below sets it twice.
skin=$(value_of Body 'Whole Body' Color)
result=$(vroid "$request")
undo "$result" "$out/original.json"
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
screenshot "$out/after.png"

# Items: one Hair and one Outfit item, and ears added to the accessories.
declare -A expected_item
request=$(vroid '{"method": "accessories"}' | jq -c --argjson ears "$ears" '{method: "apply", Accessories: (.worn + $ears)}')
for item in "${items[@]}"; do
    IFS='|' read -r section category <<<"$item"
    expected_item[$item]=$(pick_item "$section" "$category")
    request=$(with_value "$section" "$category" item "${expected_item[$item]}" <<<"$request")
done
vroid '{"method": "dump"}' >"$out/before-items.json"
result=$(vroid "$request")
undo "$result" "$out/before-items.json"
before=$(rebuilt)
changed=$(vroid "$request" | jq .changed)
((changed >= ${#items[@]} + 2)) || fatal "apply changed $changed items and accessories"
# One rebuild sets an accessory's position, rotation, and scale together, so
# only each item and added accessory needs its own.
vroid '{"method": "status"}' | jq -e --argjson before "$before" --argjson count "$((${#items[@]} + 2))" \
    '.bridge.rebuilt >= $before + $count and .bridge.updating == false' >/dev/null ||
    fatal "apply returned before a completed rebuild per item"
check_items() {
    local item section category
    for item in "${items[@]}"; do
        IFS='|' read -r section category <<<"$item"
        items_of "$section" "$category" | jq -e --argjson item "${expected_item[$item]}" '.selected == $item' >/dev/null ||
            fatal "$section/$category does not wear ${expected_item[$item]} $1"
    done
    vroid '{"method": "accessories"}' | jq -e --argjson ears "$ears" '.worn as $worn | all($ears[]; . as $ear
        | [$worn[] | . as $w | select($ear | to_entries | all(.value == $w[.key]))] | length == 1)' >/dev/null ||
        fatal "the ears are not worn as $ears $1"
}
check_items 'after apply'
# Undoing the removal of the moved cat ears adds them back while the rabbit ears
# stay, which VRoid allows only by adding both again (R-EDIT-11).
vroid '{"method": "dump"}' >"$out/before-unears.json"
result=$(vroid '{"method": "accessories"}' | jq -c '{method: "apply", Accessories: [.worn[] | select(.item != "CatEar")]}' | vroid -)
undo "$result" "$out/before-unears.json"
check_items 'after undoing the removal of the cat ears'
# An added accessory's shape sliders are checked once it is worn (R-EDIT-13):
# one above its maximum fails after the accessory is added, and the undo file
# the error names takes it off again (R-EDIT-11).
vroid '{"method": "dump"}' >"$out/before-tail.json"
if vroid '{"method": "accessories"}' | jq -c '{method: "apply", Accessories: (.worn + [{item: "CatTail", "Tail Curve 1": 2}])}' |
    vroid - >/dev/null 2>"$out/tail.err"; then
    fatal "apply of a cat tail with a shape slider above its maximum succeeded"
fi
grep -qF '/Tail Curve 1: 2 is outside' "$out/tail.err" || fatal "the refused shape slider was not reported: $(<"$out/tail.err")"
undone=$(sed -n 's/.*To undo any change, apply @\(.*\.json\)$/\1/p' "$out/tail.err")
[[ -n $undone ]] || fatal "the error names no undo file: $(<"$out/tail.err")"
undo "$(jq -cn --arg undo "$undone" '{undo: $undo}')" "$out/before-tail.json"

# Outfit colours: a distinct Base Color and Shade Color for each part of the
# new top, which stays the same built-in item (R-EDIT-12) and reads back as set
# (R-EDIT-8).
outfit_colours=$(jq -cn '{method: "parameters", section: "Outfit", category: "Tops"}' | vroid - |
    jq -c 'def distinct($label; $pick):
            [.[] | select(.label == $label) | .value | ascii_upcase] as $now
            | [range($now | length) | if $now[.] == $pick[.] then "#000080" else $pick[.] end]
            | if length == 1 then .[0] else . end;
        {"Base Color": distinct("Base Color"; ["#B22222", "#1E90FF", "#228B22", "#9932CC", "#DAA520", "#008080"]),
         "Shade Color": distinct("Shade Color"; ["#5A1111", "#0F4880", "#114411", "#4C1966", "#6D5210", "#004040"])}')
jq -e '.["Base Color"] != [] and .["Shade Color"] != []' <<<"$outfit_colours" >/dev/null ||
    fatal "Outfit/Tops ${expected_item['Outfit|Tops']} lacks Base Color or Shade Color"
check_outfit() {
    jq -cn '{method: "parameters", section: "Outfit", category: "Tops"}' | vroid - |
        jq -e --argjson want "$outfit_colours" '. as $listed | all($want | keys[]; . as $label
            | ([$want[$label]] | flatten) == [$listed[] | select(.label == $label) | .value | ascii_upcase])' >/dev/null ||
        fatal "Outfit/Tops colours do not read $outfit_colours $1"
}
request=$(jq -c --argjson colours "$outfit_colours" '.Outfit.Tops = $colours' <<<'{"method": "apply"}')
vroid '{"method": "dump"}' >"$out/before-outfit.json"
result=$(vroid "$request")
undo "$result" "$out/before-outfit.json"
vroid "$request" | jq -e --argjson want "$outfit_colours" '.changed == ([$want[]] | flatten | length)' >/dev/null ||
    fatal "apply did not change every part's colours"
check_outfit 'after apply'
check_items 'after setting outfit colours'
# Taking the top off changes no other category's item, so a colour for the
# empty Dresses category is refused before the top comes off (R-EDIT-3).
if vroid '{"method": "apply", "Outfit": {"Tops": {"item": null}, "Dresses": {"Base Color": "#123456"}}}' \
    >/dev/null 2>"$out/untop.err"; then
    fatal "apply of a colour for the empty Dresses category succeeded"
fi
grep -qF 'Nothing changed' "$out/untop.err" || fatal "taking the top off was not refused up front: $(<"$out/untop.err")"
check_items 'after a refused change of top'
# Another top's colours are checked once it is worn (R-EDIT-13): too many of them
# fail after the change of top, and the undo file the error names restores the
# top and the colours set above (R-EDIT-11).
vroid '{"method": "dump"}' >"$out/before-retop.json"
if with_value Outfit Tops item "$(pick_item Outfit Tops)" <<<'{"method": "apply"}' |
    with_value Outfit Tops 'Base Color' '["#123456", "#123456", "#123456", "#123456", "#123456", "#123456"]' |
    vroid - >/dev/null 2>"$out/retop.err"; then
    fatal "apply with too many colours for the new top succeeded"
fi
grep -qF 'Outfit/Tops/Base Color: ' "$out/retop.err" || fatal "the refused top colours were not reported: $(<"$out/retop.err")"
undone=$(sed -n 's/.*To undo any change, apply @\(.*\.json\)$/\1/p' "$out/retop.err")
[[ -n $undone ]] || fatal "the error names no undo file: $(<"$out/retop.err")"
undo "$(jq -cn --arg undo "$undone" '{undo: $undo}')" "$out/before-retop.json"
check_outfit 'after undoing a change of top'

# A value above its maximum, an unknown parameter, an unknown category, a number
# for a colour, a shared colour set twice, a colour that writes several, too
# many colours for an outfit item the request leaves alone, a worn
# accessory's rotation that is no unit quaternion, scale that is not positive,
# unknown bone, and shape slider above its maximum, and ears of one kind on
# bones VRoid cannot combine refuse the whole request, and each is reported.
IFS='|' read -r section category label <<<"${edits[0]}"
IFS='|' read -r other_section other_category other_label <<<"${edits[1]}"
# Pipeline stages run at once, so each request they need runs first.
dress=$(pick_item Outfit Dresses)
top=$(pick_item Outfit Tops)
accessories=$(vroid '{"method": "accessories"}')
worn=$(jq -c .worn <<<"$accessories")
# The bounds `accessories` lists for a cat ear slider, which the refusal names.
ear_bounds=$(jq -ce '[.worn, .sliders] | transpose | [.[] | select(.[0].item == "CatEar") | .[1][]
    | select(.label == "Ear Size (Bigger)")] | if length > 0 then .[0] else error("no CatEar Ear Size (Bigger)") end' <<<"$accessories") ||
    fatal "accessories lists no bounds for the cat ears' Ear Size (Bigger): $accessories"
# Arithmetic drops the CLI's ".0", which the bridge's refusal omits.
ear_refusal=$(jq -r '"/Ear Size (Bigger): \(.max + 1) is outside \(.min + 0)..\(.max + 0)"' <<<"$ear_bounds")
max=$(jq -cn --arg section "$section" --arg category "$category" \
    '{method: "parameters", section: $section, category: $category}' |
    vroid - | jq --arg label "$label" '.[] | select(.label == $label) | .max')
refused=$(with_value "$section" "$category" "$label" "$(jq -n --argjson max "$max" '$max + 1')" <<<'{"method": "apply"}' |
    with_value "$other_section" "$other_category" "$other_label" "$(pick "${expected[${edits[1]}]}")" |
    with_value "$other_section" "$other_category" 'No Such Parameter' 0 |
    with_value "$other_section" 'No Such Category' 'Any Parameter' 0 |
    with_value Face Irises 'R Eye' 0 |
    with_value Face Skin Color '"#123456"' |
    with_value Body 'Whole Body' Color '"#123456"' |
    with_value Face Irises 'Both Eyes' '"#123456"' |
    with_value Hair Back item '"NoSuchItem"' |
    with_value Outfit Dresses item "$dress" |
    with_value Outfit Tops item "$top" |
    with_value Outfit Shoes 'Base Color' '["#123456", "#123456", "#123456", "#123456", "#123456", "#123456"]' |
    jq -c --argjson worn "$worn" --argjson ear "$ear_bounds" '.Accessories = [$worn[] | if .item == "CatEar"
        then .rotation = [0, 0, 0, 2] | .scale = [1, 0, 1] | .bone = "J_No_Such_Bone" | .["Ear Size (Bigger)"] = $ear.max + 1
        elif .item == "RabbitEar" then .bone = "J_Bip_C_Chest" else . end]')
if vroid "$refused" >/dev/null 2>"$out/bounds.err"; then
    fatal "apply above the maximum succeeded"
fi
for reason in 'is outside' 'No Such Parameter: unknown parameter' 'No Such Category: unknown category' \
    'Face/Irises/R Eye: is a colour' 'Face/Skin/Color and Body/Whole Body/Color set the same colour' \
    'Face/Irises/Both Eyes: sets R Eye and L Eye together' 'Hair/Back: unknown item NoSuchItem' \
    'VRoid cannot wear these together' '/rotation: has length 2, not 1' '/bone: unknown bone J_No_Such_Bone' \
    '/scale: every factor must be positive' "$ear_refusal" 'only as copies of one' 'Nothing changed'; do
    grep -qF "$reason" "$out/bounds.err" || fatal "refusal lacks '$reason': $(<"$out/bounds.err")"
done
grep -qE 'Outfit/Shoes/Base Color: (give one colour|[0-9]+ colours have this label)' "$out/bounds.err" ||
    fatal "refusal lacks the Shoes colour count: $(<"$out/bounds.err")"
actual=$(value_of Body 'Whole Body' Color)
same "$actual" "$skin" || fatal "skin changed to $actual after a refused apply"
for edit in "${edits[@]}"; do
    IFS='|' read -r section category label <<<"$edit"
    actual=$(value_of "$section" "$category" "$label")
    same "$actual" "${expected[$edit]}" || fatal "$label changed to $actual after a refused apply"
done
check_items 'after a refused apply'

# Rendering and drawing thumbnails keep unsaved changes unsaved (R-VIEW-2).
render front body 64 128 "$out/unsaved.png" >/dev/null
[[ $(modified) == true ]] || fatal "rendering cleared the unsaved state"
thumbnails Body 'Whole Body' "$out/unsaved-thumbnails.png" >/dev/null
[[ $(modified) == true ]] || fatal "drawing thumbnails cleared the unsaved state"

save=$(jq -cn --arg path "$edited" '{method: "save", path: $path}')
vroid "$save" >/dev/null
if vroid "$save" >/dev/null 2>&1; then
    fatal "second save to $edited succeeded"
fi

# Exporting the moved, placed, and reshaped cat ears changes nothing in the
# app, and an existing destination is refused and left unchanged (R-EDIT-16).
ears_file=$out/ears.vroidcustomitem
accessories=$(vroid '{"method": "accessories"}')
ears_index=$(jq -e '[.worn[].item] | index("CatEar")' <<<"$accessories") || fatal "the cat ears are not worn: $accessories"
jq -c --argjson index "$ears_index" '.worn[$index]' <<<"$accessories" >"$out/exported.json"
vroid '{"method": "dump"}' >"$out/before-export.json"
export_ears=$(jq -cn --argjson index "$ears_index" --arg path "$ears_file" '{method: "export", index: $index, path: $path}')
vroid "$export_ears" >/dev/null
[[ -s $ears_file ]] || fatal "export wrote no $ears_file"
[[ $(modified) == false ]] || fatal "export marked the project modified"
vroid '{"method": "dump"}' | cmp -s - "$out/before-export.json" || fatal "dump changed while exporting the cat ears"
cp "$ears_file" "$out/ears.copy"
if vroid "$export_ears" >/dev/null 2>&1; then
    fatal "second export to $ears_file succeeded"
fi
cmp -s "$ears_file" "$out/ears.copy" || fatal "a refused export changed $ears_file"

# Rendering shows the whole character (R-VIEW-1) and changes nothing in the
# app: values, unsaved state, and the editor's view stay the same (R-VIEW-2).
# The editor shows the Body section while thumbnails are drawn for the Hair and
# Outfit sections; only restoring the section brings Body back.
for item in "${items[@]}"; do
    IFS='|' read -r section category <<<"$item"
    items_of "$section" "$category" >"$out/listed-$section-$category.json"
done
vroid '{"method": "dump"}' >"$out/before-render.json"
items_of Body 'Whole Body' >/dev/null
settled_screenshot "$out/before-render.png"
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
    check_png render "$image" "$frame" "$width" "$height"
done
image=$out/render-front-body.png
cp "$image" "$out/render-front-body.copy"
if render front body 512 1024 "$image" >/dev/null 2>&1; then
    fatal "second render to $image succeeded"
fi
cmp -s "$image" "$out/render-front-body.copy" || fatal "a refused render changed $image"
# Thumbnails show every built-in item of a category, in `items` order
# (R-VIEW-4), and change nothing in the app either (R-VIEW-2).
for item in "${items[@]}"; do
    IFS='|' read -r section category <<<"$item"
    image=$out/thumbnails-$section-$category.png
    thumbnails "$section" "$category" "$image" >"$out/thumbnails-$section-$category.json"
    jq -e --slurpfile listed "$out/listed-$section-$category.json" '.items == $listed[0].items and .columns == 10' \
        "$out/thumbnails-$section-$category.json" >/dev/null ||
        fatal "$section/$category thumbnails do not show its items; see $out/thumbnails-$section-$category.json"
    check_png sheet "$image" "$(jq '.items | length' "$out/thumbnails-$section-$category.json")"
done
cp "$image" "$out/thumbnails.copy"
if thumbnails "$section" "$category" "$image" >/dev/null 2>&1; then
    fatal "second thumbnails to $image succeeded"
fi
cmp -s "$image" "$out/thumbnails.copy" || fatal "refused thumbnails changed $image"
[[ $(modified) == false ]] || fatal "rendering or drawing thumbnails marked the project modified"
settled_screenshot "$out/after-render.png"
cmp -s "$out/before-render.png" "$out/after-render.png" ||
    fatal "the editor's view changed while rendering or drawing thumbnails; compare $out/before-render.png and $out/after-render.png"
vroid '{"method": "dump"}' >"$out/after-render.json"
cmp -s "$out/before-render.json" "$out/after-render.json" ||
    fatal "dump changed while rendering or drawing thumbnails; see $out/after-render.json"
inputs_after=$(input_events)
((inputs_after == inputs_before)) || fatal "input log grew from $inputs_before to $inputs_after events"

vroid '{"method": "stop"}' >/dev/null
launch
inputs_before=$(input_events)
if render front body 64 64 "$out/unopened.png" >/dev/null 2>"$out/unopened.err"; then
    fatal "render without an open project succeeded"
fi
grep -qiF 'open or create a character' "$out/unopened.err" || fatal "render without a project: $(<"$out/unopened.err")"
shopt -u failglob
leftovers=("$out"/unopened.png "$out"/.vroid-render-*)
shopt -s failglob
for leftover in "${leftovers[@]}"; do
    [[ ! -e $leftover ]] || fatal "a failed render left $leftover"
done
jq -cn --arg path "$edited" '{method: "open", path: $path}' | vroid - >/dev/null
[[ $(modified) == false ]] || fatal "open marked the project modified"
# Every edited value, numbers and colours alike, reads back after the save,
# restart, and reopen (R-EDIT-1).
for edit in "${edits[@]}"; do
    IFS='|' read -r section category label <<<"$edit"
    actual=$(value_of "$section" "$category" "$label")
    same "$actual" "${expected[$edit]}" || fatal "$label reopened as $actual, saved ${expected[$edit]}"
done
check_items 'after reopening'
check_outfit 'after reopening'
# Item identifiers name the same items after a restart (R-EDIT-9).
for item in "${items[@]}"; do
    IFS='|' read -r section category <<<"$item"
    items_of "$section" "$category" | jq -e --slurpfile before "$out/items-${section}-${category}.json" '.items == $before[0]' >/dev/null ||
        fatal "$section/$category lists other items after a restart; compare $out/items-${section}-${category}.json"
done
# The edited project dumps, with each colour under the category it was set in,
# and the hair colour is shared by every hair category.
vroid '{"method": "dump"}' >"$out/edited.json"
# The reopened project dumps as it did when saved, including the colour of the
# cat ears, which open hidden and locked.
jq -e --slurpfile saved "$out/before-render.json" '. == $saved[0]' "$out/edited.json" >/dev/null ||
    fatal "dump after reopening differs from $out/before-render.json; see $out/edited.json"
for colour in "${colours[@]}"; do
    IFS='|' read -r section category label _ <<<"$colour"
    edit="$section|$category|$label"
    jq -e --arg s "$section" --arg c "$category" --arg l "$label" --argjson v "${expected[$edit]}" \
        '.[$s][$c][$l] == $v' "$out/edited.json" >/dev/null ||
        fatal "$out/edited.json lacks $section/$category/$label ${expected[$edit]}"
done
jq -e --argjson want "$outfit_colours" '.Outfit.Tops as $tops | all($want | keys[]; $tops[.] == $want[.])' "$out/edited.json" >/dev/null ||
    fatal "$out/edited.json lacks the Outfit/Tops colours $outfit_colours"
front='Hair|Front|Main Color'
actual=$(value_of Hair Back 'Main Color')
same "$actual" "${expected[$front]}" || fatal "Hair/Back Main Color is $actual, not Front's"
# A hidden accessory is left out of renders, as VRoid's editor leaves it out.
# Showing the cat ears also gives them their default name back.
render front body 256 512 "$out/hidden-ears.png" >/dev/null
vroid '{"method": "accessories"}' |
    jq -c '{method: "apply", Accessories: [.worn[] | if .item == "CatEar" then {item, hidden: false, name: null} else {item} end]}' |
    vroid - >/dev/null
vroid '{"method": "accessories"}' | jq -e '[.worn[] | select(.item == "CatEar") | .name == null and .hidden == false] == [true]' >/dev/null ||
    fatal "the cat ears are not shown with their default name"
render front body 256 512 "$out/shown-ears.png" >/dev/null
! cmp -s "$out/hidden-ears.png" "$out/shown-ears.png" ||
    fatal "rendering the cat ears hidden and shown gives one image; see $out/hidden-ears.png"

# A refused import changes nothing (R-EDIT-15): the cat ears while built-in
# ears are worn, a custom item of a kind that is no accessory, and a file that
# is no custom item.
python3 - "$out/top.vroidcustomitem" <<'EOF'
import json
import sys
import zipfile

with zipfile.ZipFile(sys.argv[1], "w") as item:
    meta = {"rootTransferableGroupTypeId": "TransferableGroupType.N00.Level1.Tops"}
    item.writestr("v1customitem/meta.json", json.dumps(meta))
EOF
printf 'not a custom item\n' >"$out/garbage.vroidcustomitem"
vroid '{"method": "dump"}' >"$out/before-imports.json"
refusals=(
    "ears|$ears_file|Report this VRoid limit to the user"
    "top|$out/top.vroidcustomitem|which is not an accessory"
    "garbage|$out/garbage.vroidcustomitem|is not a VRoid custom item file"
)
for refusal in "${refusals[@]}"; do
    IFS='|' read -r name file reason <<<"$refusal"
    if jq -cn --arg path "$file" '{method: "import", path: $path}' | vroid - >/dev/null 2>"$out/import-$name.err"; then
        fatal "import of $file succeeded"
    fi
    grep -qF "$reason" "$out/import-$name.err" || fatal "import of $file: $(<"$out/import-$name.err")"
done
vroid '{"method": "dump"}' | cmp -s - "$out/before-imports.json" || fatal "a refused import changed the project"

# The template dumped before the edits restores every value, item, and
# accessory in a new app instance.
vroid "@$out/original.json" >/dev/null
vroid '{"method": "dump"}' >"$out/restored.json"
jq -e --slurpfile original "$out/original.json" '. == $original[0]' "$out/restored.json" >/dev/null ||
    fatal "dump after applying $out/original.json differs; see $out/restored.json"
inputs_after=$(input_events)
((inputs_after == inputs_before)) || fatal "input log grew from $inputs_before to $inputs_after events"
vroid '{"method": "stop", "force": true}' >/dev/null

# Each base creates a character that wears only built-in items, returns after
# its rebuild, and dumps the same in every app instance (R-EDIT-6, R-EDIT-14).
for round in 1 2; do
    for base in Preset_F Preset_M; do
        launch
        before=$(rebuilt)
        jq -cn --arg base "$base" '{method: "new", base: $base}' | vroid - |
            jq -e --arg base "$base" '.created == $base' >/dev/null || fatal "new $base reported another base"
        vroid '{"method": "status"}' | jq -e --argjson before "$before" '.bridge.rebuilt > $before and .bridge.updating == false' >/dev/null ||
            fatal "new $base returned before a completed rebuild"
        dump=$out/new-$base-$round.json
        vroid '{"method": "dump"}' >"$dump"
        jq -e --arg base "$base" '.Body["Whole Body"].item == $base and ([.. | .item? | select(. == "custom")] | length == 0)' "$dump" >/dev/null ||
            fatal "new $base is not on its base or wears an item that is not built in; see $dump"
        if ((round == 1)); then
            image=$out/new-$base.png
            render front body 256 512 "$image" >/dev/null
            check_png render "$image" body 256 512
        else
            cmp -s "$out/new-$base-1.json" "$dump" || fatal "new $base dumps differently after a restart; compare $out/new-$base-1.json and $dump"
        fi
        if jq -cn --arg path "$source" '{method: "open", path: $path}' | vroid - >/dev/null 2>"$out/reopen.err"; then
            fatal "open after new $base succeeded"
        fi
        grep -qF 'already open' "$out/reopen.err" || fatal "open after new $base: $(<"$out/reopen.err")"
        # An edited new character refuses to stop until it is saved.
        if [[ $round == 1 && $base == Preset_M ]]; then
            vroid "$(with_value Body 'Whole Body' 'Head Size' "$(pick "$(value_of Body 'Whole Body' 'Head Size')")" <<<'{"method": "apply"}')" >/dev/null
            [[ $(modified) == true ]] || fatal "an edited new $base is not marked unsaved"
            if vroid '{"method": "stop"}' >/dev/null 2>"$out/stop.err"; then
                fatal "stop discarded an edited new character"
            fi
            grep -qF 'unsaved changes' "$out/stop.err" || fatal "stop after editing new $base: $(<"$out/stop.err")"
            jq -cn --arg path "$out/new.vroid" '{method: "save", path: $path}' | vroid - >/dev/null
        fi
        vroid '{"method": "stop"}' >/dev/null
    done
done

# The exported ears, imported into a new character, read as custom with the
# values they were exported with, and apply recolours and moves them
# (R-EDIT-15, R-EDIT-16). Built-in ears are refused, since VRoid would add them
# to the import's group (R-EDIT-3), and the import's undo file takes the
# import off again (R-EDIT-11).
launch
inputs_before=$(input_events)
vroid '{"method": "new", "base": "Preset_F"}' >/dev/null
vroid '{"method": "dump"}' >"$out/before-import.json"
# VRoid itself refuses ears without their payload through its error callback,
# shows no dialog, and changes nothing, not even the unsaved state (R-EDIT-15).
python3 - "$out/hollow.vroidcustomitem" <<'EOF'
import json
import sys
import zipfile

with zipfile.ZipFile(sys.argv[1], "w") as item:
    meta = {"rootTransferableGroupTypeId": "TransferableGroupType.N00.Level1.AnimalEar"}
    item.writestr("v1customitem/meta.json", json.dumps(meta))
EOF
if jq -cn --arg path "$out/hollow.vroidcustomitem" '{method: "import", path: $path}' | vroid - >/dev/null 2>"$out/import-hollow.err"; then
    fatal "import of ears without their payload succeeded"
fi
grep -qF 'VRoid cannot import hollow.vroidcustomitem' "$out/import-hollow.err" || fatal "import of hollow ears: $(<"$out/import-hollow.err")"
[[ $(modified) == false ]] || fatal "a refused import marked the new character modified"
vroid '{"method": "dump"}' | cmp -s - "$out/before-import.json" || fatal "a refused import changed the new character"
result=$(jq -cn --arg path "$ears_file" '{method: "import", path: $path}' | vroid -)
jq -e --slurpfile exported "$out/exported.json" '.imported == ($exported[0] | .item = "custom")' <<<"$result" >/dev/null ||
    fatal "the imported ears differ from the exported $(<"$out/exported.json"): $result"
vroid '{"method": "accessories"}' |
    jq -c '{method: "apply", Accessories: [.worn[] | if .item == "custom" then .Color = "#C03030" | .position = [0, 0.05, 0] else . end]}' |
    vroid - >/dev/null
vroid '{"method": "accessories"}' |
    jq -e '[.worn[] | select(.item == "custom")] | length == 1 and .[0].Color == "#C03030" and .[0].position == [0, 0.05, 0]' >/dev/null ||
    fatal "apply did not recolour and move the imported ears"
if vroid '{"method": "accessories"}' | jq -c '{method: "apply", Accessories: (.worn + [{item: "RabbitEar"}])}' |
    vroid - >/dev/null 2>"$out/rabbit.err"; then
    fatal "apply added built-in ears to the imported ears' group"
fi
grep -qF 'Report this VRoid limit to the user' "$out/rabbit.err" || fatal "built-in ears after an import: $(<"$out/rabbit.err")"
undo "$result" "$out/before-import.json"
# VRoid keeps the import's group, so built-in ears stay refused (R-EDIT-3).
if vroid '{"method": "apply", "Accessories": [{"item": "RabbitEar"}]}' >/dev/null 2>"$out/rabbit-after-undo.err"; then
    fatal "apply added built-in ears to the kept group of an undone import"
fi
grep -qF 'Report this VRoid limit to the user' "$out/rabbit-after-undo.err" ||
    fatal "built-in ears after an undone import: $(<"$out/rabbit-after-undo.err")"
# The ears import again into the group VRoid kept, listed after a tail whose
# group is newer, and a second import, while the first is worn, joins it and
# reads back the same; its undo file removes only the second (R-EDIT-11,
# R-EDIT-15).
vroid '{"method": "apply", "Accessories": [{"item": "CatTail"}]}' >/dev/null
import_ears=$(jq -cn --arg path "$ears_file" '{method: "import", path: $path}')
vroid "$import_ears" | jq -e '.imported.item == "custom"' >/dev/null || fatal "the ears do not import into their kept group"
vroid '{"method": "dump"}' >"$out/before-second-import.json"
result=$(vroid "$import_ears")
jq -e --slurpfile exported "$out/exported.json" '.imported == ($exported[0] | .item = "custom")' <<<"$result" >/dev/null ||
    fatal "the second imported ears differ from the exported $(<"$out/exported.json"): $result"
undo "$result" "$out/before-second-import.json"
inputs_after=$(input_events)
((inputs_after == inputs_before)) || fatal "input log grew from $inputs_before to $inputs_after events"
vroid '{"method": "stop", "force": true}' >/dev/null

jq -n --arg project "$edited" '{ok: true, project: $project}'
