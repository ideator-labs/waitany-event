# AGENTS.md

Handoff for a coding agent with no prior context on this repo.

## What this proves

One claim: a Workflow Core workflow can wait for any one of several named events (here `Approve` or `Reject`) on one correlation key. It continues when the first event arrives, records which event won and its payload, and cancels the losing wait. The losing event never has to be published.

It runs on the real `WorkflowCore` 3.21.0 NuGet package, in one .NET 8 process, with the in-memory store. No paid APIs, no network after the NuGet restore.

Why it matters. Workflow Core has `WaitFor` for one event. Users keep asking how to wait for either of two (issues #412 and #428, discussion #1351). The usual answer is `Parallel` plus `WaitFor` plus a hand-written `CancelCondition` per branch. People get it wrong. Without the cancel condition, `Join` waits for every branch, so the workflow hangs until someone publishes the other event. Check 9 shows that hang. Discussion #1396 shows the workaround of publishing the missing event yourself.

## Status

| Piece | Real or stubbed |
|---|---|
| Workflow host, `Parallel`, `WaitFor`, `CancelCondition`, event publishing, in-memory persistence | Real. NuGet `WorkflowCore` 3.21.0. |
| `WaitAny` extension and `TerminateLosingWaits` step | Real, in `src/WaitAny`. About 80 lines. |
| Pointer statuses, `Winner`, payload, open subscription counts in the demo output | Real. Read back from the Workflow Core persistence store after each scenario. |
| Approval workflow | Toy. One counter step after the wait. No external IO. |
| Durability across a host restart | Not shown. In-memory store only. |

`bash demo.sh` passes 10 of 10 checks on a fresh clone. `bash demo.sh --break` fails checks 2 through 6 and exits 1. `bash demo.sh --break cleanup` fails checks 5 and 5b and exits 1.

## Data shape

```
IWaitAnyData                      implemented by the workflow data class
  Winner         string?          name of the first event to arrive, null while waiting
  WinnerPayload  object?          EventData of that event

ApprovalData : IWaitAnyData       the demo workflow data
  RequestId      string           correlation key, used as the event key for every event
  Winner, WinnerPayload
  AfterWaitRuns  int              how many times the step after WaitAny ran. Must be 1.

WaitAny(eventKey, params eventNames) expands to:
  Parallel
    per name: WaitFor(name, eventKey, cancelCondition: data.Winner != null)
              Output: Winner ??= name; WinnerPayload = EventData if Winner == name
  Join
  TerminateLosingWaits            deletes subscriptions still held by Cancelled WaitFor pointers
```

One demo output record per scenario (`.demo/results.jsonl`):

```json
{"scenario":"approve_only","workflow":"Approval","key":"req-1","published":["Approve"],
 "status":"Complete","winner":"Approve","winnerPayload":"yes","afterWaitRuns":1,
 "waits":[{"event":"Approve","pointer":"Complete"},{"event":"Reject","pointer":"Cancelled"}],
 "openSubscriptions":0,"elapsedMs":53}
```

`elapsedMs` runs from the first publish to the snapshot. `openSubscriptions` counts subscriptions in the store for this instance across both event names.

## Repo map

```
demo.sh                              the proof. Builds, runs every scenario once, prints CHECK lines
src/WaitAny/
  IWaitAnyData.cs                    Winner and WinnerPayload contract
  WaitAnyExtensions.cs               WaitAny(...) builder extension and AddWaitAny() DI registration
  TerminateLosingWaits.cs            step that deletes subscriptions left behind by cancelled waits
src/WaitAny.Demo/
  Workflows.cs                       ApprovalWorkflow (uses WaitAny) and BaselineWorkflow (#428 pattern, no CancelCondition)
  Program.cs                         starts the host, runs six scenarios, prints one JSON line each to stdout. Logs go to stderr.
```

## Setup, run, verify (fresh clone)

Prereqs: .NET 8 SDK (`dotnet --list-sdks` shows 8.x), `jq`, and `python3` (break mode only). If the SDK lives in `~/.dotnet`, `demo.sh` puts it on PATH. The first build needs NuGet access.

```bash
git clone https://github.com/ideator-labs/waitany-event.git
cd waitany-event
bash demo.sh
```

Expected: ten `CHECK PASS` lines, a results table, then `RESULT: PASS`, exit 0. It takes about 15 seconds after the first restore. Most of that is check 9 waiting 5 seconds to show the baseline hangs.

```
CHECK PASS  1 build: WaitAny + demo compile against WorkflowCore 3.21.0
CHECK PASS  2 approve_only: publish only Approve("yes"), workflow Complete, Winner=Approve, payload=yes, next step ran once
CHECK PASS  3 reject_only: publish only Reject("no"), workflow Complete, Winner=Reject, payload=no
CHECK PASS  4 second event not required: Reject never published, Approve wait Complete, Reject wait Cancelled, done in under 5s
CHECK PASS  5 losing subscription deleted: 0 open subscriptions left for the instance after Approve wins
CHECK PASS  5b losing subscription deleted after Reject wins
CHECK PASS  6 both events back to back: exactly one winner, its own payload, one wait Complete and one Cancelled, next step ran once
CHECK PASS  7 late loser ignored: Reject after Approve won resumes nothing, Winner=Approve, payload=yes, next step ran once
CHECK PASS  8 correlation key respected: Approve for req-a leaves req-b Runnable with both waits open
CHECK PASS  9 control (#428 pattern, no CancelCondition): publish only Approve, still Runnable after 5s, next step never ran
RESULT: PASS
```

Break it on purpose:

```bash
bash demo.sh --break           # or: bash demo.sh --break cancel
bash demo.sh --break cleanup
```

Both copy `src/` to `.demo/src/`, delete one exact string from the copy, then build and run the copy. The script exits 2 if the string is not found, so a sabotage can never silently do nothing. The real `src/` is never touched, so `bash demo.sh` passes again right after.

- `cancel` removes the `cancelCondition` argument from `WaitAny`. Publishing only `Approve` leaves the instance `Runnable` with the `Reject` wait open. Checks 2 to 6 fail.
- `cleanup` removes the `TerminateSubscription` call. The workflow still completes, but the losing subscription stays in the store. Checks 5 and 5b fail.

Raw output lands in `.demo/` (`results.jsonl`, `host.log`, `build.log`).

## Design decisions

- **Helper over custom step.** `WaitAny` is a builder extension that expands to the documented `Parallel` + `WaitFor` + `CancelCondition` pattern. It uses only public Workflow Core APIs and works with any persistence provider. A custom primitive with one pointer and many subscriptions would need changes to the event consumer, which is internal.
- **Winner on the data, behind an interface.** The cancel condition is an expression over workflow data, so the winner has to live there. `IWaitAnyData` gives the helper a typed place to write it without reflection or user-supplied expressions.
- **First write wins.** The output action does `Winner ??= name`. `WorkflowExecutor` runs the cancellation checks after every pointer and skips pointers that are no longer active, so a loser is cancelled before it can run (source read, check 6 exercises it with both events back to back). The `??=` guard is a second line of defense. Break mode `cancel` shows it holding: both waits complete and `Winner` still stays `Approve`.
- **Delete the losing subscription.** Workflow Core's `CancellationProcessor` marks the pointer `Cancelled` but does not remove its event subscription. The subscription only goes away when a matching event arrives. `TerminateLosingWaits` runs after `Join` and deletes subscriptions owned by cancelled `WaitFor` pointers of this instance. Break mode `cleanup` proves the check is not vacuous.
- **One key for all events.** Approve and reject for one request share the request id. That is the common case and keeps the signature to `(key, names)`.
- **Baseline in the same run.** `BaselineWorkflow` is the #428 code with no cancel condition. Check 9 shows it stays `Runnable`, so the hang is measured, not claimed.
- **Six scenarios, one process.** Each scenario uses its own correlation key, so they cannot see each other's events. Check 8 proves keys stay isolated.

## Limits

- In-memory persistence only. Discussion #1396 reports `Parallel` + `WaitFor` misbehaving after a host restart. This repo does not test restarts.
- One `WaitAny` per workflow. `Winner` is a single field and the cancel condition is `Winner != null`. A second `WaitAny`, or one inside a loop, would see the old winner and cancel at once. Resetting or scoping the winner is next step 2.
- No timeout branch. If no event ever arrives, the instance waits forever, same as plain `WaitFor`.
- All events share one key. Per-event keys are not supported.
- `TerminateLosingWaits` looks up subscriptions with `GetSubscriptions(name, key, DateTime.MaxValue)` and filters by workflow and pointer id. Fine for the in-memory store. Not tested on SQL or Mongo providers, where `DateTime.MaxValue` and query cost may matter.
- `TerminateLosingWaits` removes the subscriptions of every cancelled `WaitFor` pointer in the instance, not only those from this `WaitAny`. That is harmless in this repo. It could surprise a workflow that relies on cancelled waits keeping subscriptions.
- `WinnerPayload` is `object?`. The demo publishes strings. Typed payloads need a cast at the use site.
- Ordering for events published back to back is decided by the event consumer. Check 6 accepts either winner, as long as there is exactly one.

## Next steps (in priority order)

1. **Timeout as a racer.** Add `WaitAny(key, timeout, names)` where a `Delay` branch can win with `Winner = "Timeout"`. Done when a new scenario publishes nothing, the instance is `Complete` with `Winner == "Timeout"` within timeout + 2 s, and both event waits are `Cancelled` with 0 open subscriptions.
2. **Reusable winner slot.** Let `WaitAny` take a winner expression, for example `WaitAny(d => d.Decision, key, names)`, and reset it at the start. Done when one workflow runs two `WaitAny` blocks in a row and a demo check sees a different winner from each.
3. **Durable store and restart.** Run the same scenarios on `WorkflowCore.Persistence.Sqlite`, stopping and starting the host between start and publish. Done when checks 2 to 5 pass across the restart. Write down any #1396 behavior you hit.
4. **Per-event keys.** Accept `(name, key)` pairs. Done when a scenario waits for `Approve` on the request id and `Cancel` on the customer id, and publishing only the customer `Cancel` wins.
5. **Package it.** Ship `src/WaitAny` as a NuGet or propose it upstream as a Workflow Core PR. Done when a fresh console app that references only the package passes checks 2 to 7.

## Working rules

- `bash demo.sh` is the source of truth. Keep it passing and keep both `--break` modes failing.
- Every new claim gets a CHECK line that reads real store state, and a sabotage that makes it fail.
- Keep stdout of the demo app to one JSON line per scenario. Logs go to stderr.
- Keep the `WorkflowCore` package at 3.21.x unless a step needs a newer API.
- Commit as `danielgerlag <daniel@gerlag.ca>`.
- Plain prose. No em-dashes.

## Sources

- Workflow Core: https://github.com/danielgerlag/workflow-core
- WorkflowCore 3.21.0 on NuGet: https://www.nuget.org/packages/WorkflowCore/3.21.0
- Issue #412, wait for 2 events and proceed if any is received: https://github.com/danielgerlag/workflow-core/issues/412
- Issue #428, parallel paths with events do not complete: https://github.com/danielgerlag/workflow-core/issues/428
- Discussion #1351, waiting for multiple events: https://github.com/danielgerlag/workflow-core/discussions/1351
- Discussion #1396, Parallel step does not finish after a host restart: https://github.com/danielgerlag/workflow-core/discussions/1396
- External events docs (`CancelCondition`): https://github.com/danielgerlag/workflow-core/blob/master/docs/external-events.md
- Cancellation logic read for this POC: `src/WorkflowCore/Services/CancellationProcessor.cs` and `src/WorkflowCore/Services/BackgroundTasks/EventConsumer.cs` in the Workflow Core repo
