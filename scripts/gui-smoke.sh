#!/usr/bin/env bash
# Drive a real session through edit, save, restart, and reopen, checking
# R-EDIT-1, R-EDIT-3, R-EDIT-5, and R-EDIT-6. Needs graphics access.
set -euo pipefail
shopt -s failglob

usage() {
    printf '%s\n' \
        "Usage: $0 SESSION SOURCE.vroid" \
        '' \
        'Opens SOURCE in SESSION, edits one Body and one Face parameter, saves a' \
        'copy, restarts, and checks the saved values. SESSION must be stopped and' \
        'past its first-run dialogs. Set VROID_CLI to choose the vroid-cli binary.'
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
    "$cli" --session "$session" "$@"
}

state=$(vroid status)
[[ $(jq .app_running <<<"$state") == false ]] || fatal "Stop session $session first"
out=$(mktemp -d "$(jq -r .directory <<<"$state")/smoke.XXXXXX")
edited=$out/edited.vroid
printf 'gui-smoke: artifacts in %s\n' "$out" >&2

# Print the current value of a parameter as JSON.
value_of() {
    vroid parameters "$1" "$2" | jq -e --arg label "$3" '[.[] | select(.label == $label)] | if length == 1 then .[0].value else error("expected one \($label)") end'
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
    log=$(vroid status | jq -r .desktop)/inputs.jsonl
    if [[ -f $log ]]; then
        wc -l <"$log"
    else
        printf '0\n'
    fi
}

edits=(
    'Body|Whole Body|Head Size'
    'Face|Face Sets|Eye Size X'
)

vroid launch --timeout 1200 >/dev/null
inputs_before=$(input_events)
vroid open "$source" >/dev/null

declare -A expected
for edit in "${edits[@]}"; do
    IFS='|' read -r section category label <<<"$edit"
    current=$(value_of "$section" "$category" "$label")
    target=$(pick "$current")
    rebuilt=$(vroid status | jq .bridge.rebuilt)
    vroid set "$section" "$category" "$label" "$target" >/dev/null
    vroid status | jq -e --argjson before "$rebuilt" \
        '.bridge.rebuilt > $before and .bridge.updating == false' >/dev/null ||
        fatal "set $label returned before a completed rebuild"
    actual=$(value_of "$section" "$category" "$label")
    same "$actual" "$target" || fatal "$label read back $actual after set $target"
    expected[$edit]=$target
done

IFS='|' read -r section category label <<<"${edits[0]}"
max=$(vroid parameters "$section" "$category" | jq --arg label "$label" '.[] | select(.label == $label) | .max')
if vroid set "$section" "$category" "$label" "$(jq -n --argjson max "$max" '$max + 1')" >/dev/null 2>"$out/bounds.err"; then
    fatal "set above the maximum succeeded"
fi
grep -q 'within' "$out/bounds.err" || fatal "unexpected bounds error: $(<"$out/bounds.err")"
actual=$(value_of "$section" "$category" "$label")
same "$actual" "${expected[${edits[0]}]}" || fatal "$label changed to $actual after a refused set"

vroid save "$edited" >/dev/null
if vroid save "$edited" >/dev/null 2>&1; then
    fatal "second save to $edited succeeded"
fi
inputs_after=$(input_events)
((inputs_after == inputs_before)) || fatal "input log grew from $inputs_before to $inputs_after events"

vroid stop >/dev/null
vroid launch --timeout 1200 >/dev/null
inputs_before=$(input_events)
vroid open "$edited" >/dev/null
for edit in "${edits[@]}"; do
    IFS='|' read -r section category label <<<"$edit"
    actual=$(value_of "$section" "$category" "$label")
    same "$actual" "${expected[$edit]}" || fatal "$label reopened as $actual, saved ${expected[$edit]}"
done
inputs_after=$(input_events)
((inputs_after == inputs_before)) || fatal "input log grew from $inputs_before to $inputs_after events"
vroid stop >/dev/null

jq -n --arg project "$edited" '{ok: true, project: $project}'
