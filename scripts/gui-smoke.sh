#!/usr/bin/env bash
# Drive a real session through edit, save, restart, reopen, and a template
# round trip, checking R-EDIT-1, R-EDIT-3, R-EDIT-5, R-EDIT-6, R-EDIT-7, and
# R-EDIT-8.
# Needs graphics access.
set -euo pipefail
shopt -s failglob

usage() {
    printf '%s\n' \
        "Usage: $0 SESSION SOURCE.vroid" \
        '' \
        'Opens SOURCE in SESSION, dumps its template, edits one Body and two Face' \
        'parameters and four colours (orange hair, green eyes) in one request,' \
        'saves a copy, restarts, checks the saved values, and applies the' \
        'original template. Screenshots before and after the edit land in the' \
        'artifact directory. SESSION must be stopped and past its first-run' \
        'dialogs. Set VROID_CLI to choose the vroid-cli binary.'
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
command -v jq >/dev/null || fatal 'Missing required command: jq'

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

screenshot() {
    framewisp "$(vroid '{"method": "status"}' | jq -r .desktop)" screenshot "$out/$1.png" >/dev/null
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

rebuilt() {
    vroid '{"method": "status"}' | jq .bridge.rebuilt
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
command -v framewisp >/dev/null || fatal 'Missing required command: framewisp'

vroid '{"method": "launch", "timeout": 1200}' >/dev/null
inputs_before=$(input_events)
jq -cn --arg path "$source" '{method: "open", path: $path}' | vroid - >/dev/null
vroid '{"method": "dump"}' >"$out/original.json"

screenshot before

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
screenshot after

# A value above its maximum, an unknown parameter, an unknown category, a number
# for a colour, a shared colour set twice, and a colour that writes several
# refuse the whole request, and each is reported.
IFS='|' read -r section category label <<<"${edits[0]}"
IFS='|' read -r other_section other_category other_label <<<"${edits[1]}"
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
    with_value Face Irises 'Both Eyes' '"#123456"')
if vroid "$refused" >/dev/null 2>"$out/bounds.err"; then
    fatal "apply above the maximum succeeded"
fi
for reason in 'is outside' 'No Such Parameter: unknown parameter' 'No Such Category: unknown category' \
    'Face/Irises/R Eye: is a colour' 'Face/Skin/Color and Body/Whole Body/Color set the same colour' \
    'Face/Irises/Both Eyes: sets R Eye and L Eye together' 'Nothing changed'; do
    grep -qF "$reason" "$out/bounds.err" || fatal "refusal lacks '$reason': $(<"$out/bounds.err")"
done
actual=$(value_of Body 'Whole Body' Color)
same "$actual" "$skin" || fatal "skin changed to $actual after a refused apply"
for edit in "${edits[@]}"; do
    IFS='|' read -r section category label <<<"$edit"
    actual=$(value_of "$section" "$category" "$label")
    same "$actual" "${expected[$edit]}" || fatal "$label changed to $actual after a refused apply"
done

save=$(jq -cn --arg path "$edited" '{method: "save", path: $path}')
vroid "$save" >/dev/null
if vroid "$save" >/dev/null 2>&1; then
    fatal "second save to $edited succeeded"
fi
inputs_after=$(input_events)
((inputs_after == inputs_before)) || fatal "input log grew from $inputs_before to $inputs_after events"

vroid '{"method": "stop"}' >/dev/null
vroid '{"method": "launch", "timeout": 1200}' >/dev/null
inputs_before=$(input_events)
jq -cn --arg path "$edited" '{method: "open", path: $path}' | vroid - >/dev/null
for edit in "${edits[@]}"; do
    IFS='|' read -r section category label <<<"$edit"
    actual=$(value_of "$section" "$category" "$label")
    same "$actual" "${expected[$edit]}" || fatal "$label reopened as $actual, saved ${expected[$edit]}"
done
# The edited project dumps, with each colour under the category it was set in,
# and the hair colour is shared by every hair category.
vroid '{"method": "dump"}' >"$out/edited.json"
for colour in "${colours[@]}"; do
    IFS='|' read -r section category label _ <<<"$colour"
    edit="$section|$category|$label"
    jq -e --arg s "$section" --arg c "$category" --arg l "$label" --argjson v "${expected[$edit]}" \
        '.[$s][$c][$l] == $v' "$out/edited.json" >/dev/null ||
        fatal "$out/edited.json lacks $section/$category/$label ${expected[$edit]}"
done
front='Hair|Front|Main Color'
actual=$(value_of Hair Back 'Main Color')
same "$actual" "${expected[$front]}" || fatal "Hair/Back Main Color is $actual, not Front's"

# The template dumped before the edits restores every value in a new app instance.
vroid "@$out/original.json" >/dev/null
vroid '{"method": "dump"}' >"$out/restored.json"
jq -e --slurpfile original "$out/original.json" '. == $original[0]' "$out/restored.json" >/dev/null ||
    fatal "dump after applying $out/original.json differs; see $out/restored.json"
inputs_after=$(input_events)
((inputs_after == inputs_before)) || fatal "input log grew from $inputs_before to $inputs_after events"
vroid '{"method": "stop", "force": true}' >/dev/null

jq -n --arg project "$edited" '{ok: true, project: $project}'
