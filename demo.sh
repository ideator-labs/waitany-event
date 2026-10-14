#!/usr/bin/env bash
# Proves: a Workflow Core workflow resumes on the FIRST of several events, records the winner,
# and cancels the losing waits (pointer and subscription).
#   bash demo.sh                  all checks must PASS, exit 0
#   bash demo.sh --break          CancelCondition removed from WaitAny, a check must FAIL, exit 1
#   bash demo.sh --break cleanup  losing subscriptions not deleted, a check must FAIL, exit 1
set -euo pipefail
cd "$(dirname "$0")"
[[ -d "$HOME/.dotnet" ]] && export PATH="$HOME/.dotnet:$PATH"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
command -v jq >/dev/null || { echo "jq is required"; exit 2; }

OUT=.demo
SRC=src
rm -rf "$OUT" && mkdir -p "$OUT"

sabotage() {  # sabotage <file> <exact text to remove>
  grep -qF -- "$2" "$1" || { echo "sabotage target not found in $1"; exit 2; }
  F="$1" T="$2" python3 -c 'import os; p=os.environ["F"]; s=open(p).read(); open(p,"w").write(s.replace(os.environ["T"], "", 1))'
}

copy_src() { cp -r src "$OUT/src" && rm -rf "$OUT"/src/*/bin "$OUT"/src/*/obj && SRC="$OUT/src"; }

case "${1:-} ${2:-}" in
  " ") ;;
  "--break "|"--break cancel")
    copy_src
    sabotage "$SRC/WaitAny/WaitAnyExtensions.cs" ", cancelCondition: data => data.Winner != null"
    echo "BREAK MODE: WaitAny no longer passes a CancelCondition, so every event is required. Expect a FAIL." ;;
  "--break cleanup")
    copy_src
    sabotage "$SRC/WaitAny/TerminateLosingWaits.cs" "await subscriptions.TerminateSubscription(sub.Id);"
    echo "BREAK MODE: losing subscriptions are left in the store. Expect a FAIL." ;;
  *) echo "usage: bash demo.sh [--break [cancel|cleanup]]"; exit 2 ;;
esac

FAILED=0
pass() { echo "CHECK PASS  $1"; }
fail() { echo "CHECK FAIL  $1"; FAILED=1; }

if dotnet build "$SRC/WaitAny.Demo" -c Release -o "$OUT/bin" -v quiet -nologo > "$OUT/build.log" 2>&1; then
  pass "1 build: WaitAny + demo compile against WorkflowCore 3.21.0"
else
  cat "$OUT/build.log"; fail "1 build"; echo "RESULT: FAIL"; exit 1
fi

dotnet "$OUT/bin/WaitAny.Demo.dll" > "$OUT/results.jsonl" 2> "$OUT/host.log"

check() {  # check <label> <scenario> <jq filter on that scenario's record that must print true>
  if [[ "$(jq -s "[.[] | select(.scenario == \"$2\")] | length == 1 and (.[0] | $3)" "$OUT/results.jsonl")" == "true" ]]; then pass "$1"; else fail "$1"; fi
}
WAITS='(.waits | map({(.event): .pointer}) | add)'

check "2 approve_only: publish only Approve(\"yes\"), workflow Complete, Winner=Approve, payload=yes, next step ran once" approve_only \
  '.published == ["Approve"] and .status == "Complete" and .winner == "Approve" and .winnerPayload == "yes" and .afterWaitRuns == 1'
check "3 reject_only: publish only Reject(\"no\"), workflow Complete, Winner=Reject, payload=no" reject_only \
  '.published == ["Reject"] and .status == "Complete" and .winner == "Reject" and .winnerPayload == "no" and .afterWaitRuns == 1'
check "4 second event not required: Reject never published, Approve wait Complete, Reject wait Cancelled, done in under 5s" approve_only \
  "$WAITS == {\"Approve\":\"Complete\",\"Reject\":\"Cancelled\"} and .elapsedMs < 5000"
check "5 losing subscription deleted: 0 open subscriptions left for the instance after Approve wins" approve_only \
  '.openSubscriptions == 0'
check "5b losing subscription deleted after Reject wins" reject_only \
  '.openSubscriptions == 0 and '"$WAITS"' == {"Approve":"Cancelled","Reject":"Complete"}'
check "6 both events back to back: exactly one winner, its own payload, one wait Complete and one Cancelled, next step ran once" both_back_to_back \
  '.status == "Complete" and .afterWaitRuns == 1 and ({"Approve":"yes","Reject":"no"}[.winner] == .winnerPayload) and ('"$WAITS"' | [.[]] | sort == ["Cancelled","Complete"]) and '"$WAITS"'[.winner] == "Complete"'
check "7 late loser ignored: Reject after Approve won resumes nothing, Winner=Approve, payload=yes, next step ran once" late_loser \
  '.status == "Complete" and .winner == "Approve" and .winnerPayload == "yes" and .afterWaitRuns == 1 and .openSubscriptions == 0 and '"$WAITS"'.Reject == "Cancelled"'
check "8 correlation key respected: Approve for req-a leaves req-b Runnable with both waits open" other_key_untouched \
  '.status == "Runnable" and .winner == null and .openSubscriptions == 2 and '"$WAITS"' == {"Approve":"WaitingForEvent","Reject":"WaitingForEvent"}'
check "9 control (#428 pattern, no CancelCondition): publish only Approve, still Runnable after 5s, next step never ran" baseline_no_cancel \
  '.status == "Runnable" and .afterWaitRuns == 0 and '"$WAITS"'.Reject == "WaitingForEvent"'

echo "--- results ---"
jq -c '{scenario, status, winner, winnerPayload, afterWaitRuns, waits: '"$WAITS"', openSubscriptions, elapsedMs}' "$OUT/results.jsonl"

if [[ $FAILED -ne 0 ]]; then echo "RESULT: FAIL (raw output in $OUT/)"; exit 1; fi
echo "RESULT: PASS"
