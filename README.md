# waitany-event

A Workflow Core workflow that waits for the first of several events, then moves on.

```csharp
builder
    .StartWith(_ => ExecutionResult.Next())
    .WaitAny(data => data.RequestId, "Approve", "Reject")
    .Then(context => /* data.Winner is "Approve" or "Reject" */ ExecutionResult.Next());
```

Publish only `Approve` and the workflow completes. `Winner` is `"Approve"`, `WinnerPayload` is the event data, and the `Reject` wait is cancelled with its subscription deleted. Nobody has to publish a fake `Reject` to unstick a `Join`.

This is the "wait for any one of N events" question from Workflow Core issues [#412](https://github.com/danielgerlag/workflow-core/issues/412) and [#428](https://github.com/danielgerlag/workflow-core/issues/428) and discussion [#1351](https://github.com/danielgerlag/workflow-core/discussions/1351), packaged as one extension method on the real `WorkflowCore` 3.21.0 NuGet package.

```bash
bash demo.sh           # 10 checks, RESULT: PASS
bash demo.sh --break   # WaitAny without CancelCondition, checks fail, exit 1
```

Needs the .NET 8 SDK and `jq`. It is a proof of concept with in-memory persistence only. See [AGENTS.md](AGENTS.md) for the data shape, what is real, the limits, and next steps.
