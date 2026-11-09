#!/usr/bin/env bash
# Drive a real session through edit, save, restart, reopen, and a template
# round trip, checking R-EDIT-1, R-EDIT-3, R-EDIT-5, R-EDIT-6, and R-EDIT-7.
# Needs graphics access.
set -euo pipefail
shopt -s failglob

usage() {
    printf '%s\n' \
        "Usage: $0 SESSION SOURCE.vroid" \
        '' \
        'Opens SOURCE in SESSION, dumps its template, edits one Body and one Face' \
        'parameter, saves a copy, restarts, checks the saved values, and applies' \
        'the original template. SESSION must be stopped and past its first-run' \
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

# Build an apply request that sets one value.
apply_one() {
    jq -cn --arg section "$1" --arg category "$2" --arg label "$3" --argjson value "$4" \
        '{method: "apply"} | .[$section][$category][$label] = $value'
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

rebuilt() {
    vroid '{"method": "status"}' | jq .bridge.rebuilt
}

edits=(
    'Body|Whole Body|Head Size'
    'Face|Face Sets|Eye Size X'
)

vroid '{"method": "launch", "timeout": 1200}' >/dev/null
inputs_before=$(input_events)
jq -cn --arg path "$source" '{method: "open", path: $path}' | vroid - >/dev/null
vroid '{"method": "dump"}' >"$out/original.json"

declare -A expected
for edit in "${edits[@]}"; do
    IFS='|' read -r section category label <<<"$edit"
    current=$(value_of "$section" "$category" "$label")
    target=$(pick "$current")
    before=$(rebuilt)
    apply_one "$section" "$category" "$label" "$target" | vroid - >/dev/null
    vroid '{"method": "status"}' | jq -e --argjson before "$before" \
        '.bridge.rebuilt > $before and .bridge.updating == false' >/dev/null ||
        fatal "apply $label returned before a completed rebuild"
    actual=$(value_of "$section" "$category" "$label")
    same "$actual" "$target" || fatal "$label read back $actual after apply $target"
    expected[$edit]=$target
done

# One value above its maximum refuses the whole request.
IFS='|' read -r section category label <<<"${edits[0]}"
IFS='|' read -r other_section other_category other_label <<<"${edits[1]}"
max=$(jq -cn --arg section "$section" --arg category "$category" \
    '{method: "parameters", section: $section, category: $category}' |
    vroid - | jq --arg label "$label" '.[] | select(.label == $label) | .max')
refused=$(apply_one "$section" "$category" "$label" "$(jq -n --argjson max "$max" '$max + 1')" |
    jq -c --arg s "$other_section" --arg c "$other_category" --arg l "$other_label" \
        --argjson v "$(pick "${expected[${edits[1]}]}")" '.[$s][$c][$l] = $v')
if vroid "$refused" >/dev/null 2>"$out/bounds.err"; then
    fatal "apply above the maximum succeeded"
fi
grep -q 'outside .*Nothing changed' "$out/bounds.err" || fatal "unexpected bounds error: $(<"$out/bounds.err")"
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

# The template dumped before the edits restores every value in a new app instance.
vroid "@$out/original.json" >/dev/null
vroid '{"method": "dump"}' >"$out/restored.json"
jq -e --slurpfile original "$out/original.json" '. == $original[0]' "$out/restored.json" >/dev/null ||
    fatal "dump after applying $out/original.json differs; see $out/restored.json"
inputs_after=$(input_events)
((inputs_after == inputs_before)) || fatal "input log grew from $inputs_before to $inputs_after events"
vroid '{"method": "stop", "force": true}' >/dev/null

jq -n --arg project "$edited" '{ok: true, project: $project}'
