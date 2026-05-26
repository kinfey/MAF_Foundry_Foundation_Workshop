# Checkpoints & Human-in-the-Loop Reference (.NET Workflows)

Two related capabilities that make long-running and interactive workflows practical:

- **Checkpointing** — automatically save state at each super-step boundary so you can resume after a crash or replay from a specific point.
- **Request ports / human-in-the-loop** — pause the workflow, surface a typed request to the host application, and resume once a response is supplied.

Both rely on the same super-step execution model, and they compose: a workflow can be both checkpointed and HITL-enabled.

## Super Steps

A workflow executes in **super-steps**. Each super-step runs every executor whose input is ready, in parallel, and completes once those executors finish. State updates queued during a step become visible at the next step boundary. Checkpoints are created at those boundaries; request ports also suspend at them.

| Event | Member | Notes |
|-------|--------|-------|
| `SuperStepCompletedEvent` | `.CompletionInfo?.Checkpoint` | The checkpoint just created (null if `CheckpointManager` was not supplied). |

## Checkpoint and Resume

Pass a `CheckpointManager` to `RunStreamingAsync`. The framework writes a checkpoint automatically at the end of every super-step.

```csharp
using Microsoft.Agents.AI.Workflows;

CheckpointManager checkpointManager = CheckpointManager.Default;
List<CheckpointInfo> checkpoints = new();

// Initial run — collect checkpoints as we go.
await using StreamingRun checkpointedRun =
    await InProcessExecution.RunStreamingAsync(workflow, NumberSignal.Init, checkpointManager);

await foreach (WorkflowEvent evt in checkpointedRun.WatchStreamAsync())
{
    switch (evt)
    {
        case ExecutorCompletedEvent done:
            Console.WriteLine($"* {done.ExecutorId} completed.");
            break;

        case SuperStepCompletedEvent step
            when step.CompletionInfo?.Checkpoint is CheckpointInfo cp:
            checkpoints.Add(cp);
            Console.WriteLine($"** Checkpoint saved at step {checkpoints.Count}.");
            break;

        case WorkflowOutputEvent output:
            Console.WriteLine($"Workflow output: {output.Data}");
            break;

        case WorkflowErrorEvent err:
            Console.Error.WriteLine(err.Exception);
            break;

        case ExecutorFailedEvent fail:
            Console.Error.WriteLine($"{fail.ExecutorId} failed: {fail.Data}");
            break;
    }
}
```

### Resume from a checkpoint

`RestoreCheckpointAsync` replays the workflow's state from the saved point. The example below restores the same run instance and continues processing:

```csharp
const int checkpointIndex = 5;
CheckpointInfo saved = checkpoints[checkpointIndex];

await checkpointedRun.RestoreCheckpointAsync(saved, CancellationToken.None);

await foreach (WorkflowEvent evt in checkpointedRun.WatchStreamAsync())
{
    // The workflow continues from the restored state.
}
```

You can also restore on a fresh `StreamingRun`:

```csharp
await using StreamingRun resumed =
    await InProcessExecution.RunStreamingAsync(workflow, NumberSignal.Init, checkpointManager);
await resumed.RestoreCheckpointAsync(saved, CancellationToken.None);
```

### Custom checkpoint storage

`CheckpointManager.Default` keeps checkpoints in-memory for the lifetime of the process. For durable storage, implement `CheckpointManager` to persist `CheckpointInfo` payloads to disk, blob storage, a database, etc. The interface only requires serializing and rehydrating the framework-supplied state objects — your executors don't need to know.

### When checkpoints help

- **Recovery** — a transient model failure or process crash leaves work-in-progress recoverable.
- **Replay debugging** — re-run from a known state to reproduce a failure deterministically.
- **HITL flows** — pair checkpoints with a request port so the workflow can pause for hours or days waiting on a human and survive process restarts.
- **A/B experiments** — branch off from a saved state with different downstream logic.

### Caveats

- Side effects (database writes, emails sent, payments charged) **are not undone** on resume. Either make side effects idempotent or quarantine them in dedicated executors with their own idempotency keys.
- Shared state in `IWorkflowContext` is captured in the checkpoint. External state (files, databases) is not — restore it yourself if needed.

## Human-in-the-Loop (`RequestPort`)

A request port executor surfaces a typed `ExternalRequest` to the workflow stream as a `RequestInfoEvent`. The host application reads the request, computes a response, and calls `SendResponseAsync` to resume the workflow.

### Server-side: emit a request

Mirror the [`HumanInTheLoop/HumanInTheLoopBasic`](https://github.com/microsoft/agent-framework/tree/main/dotnet/samples/03-workflows/HumanInTheLoop/HumanInTheLoopBasic) sample. The workflow is a guessing game where a `JudgeExecutor` decides whether the latest guess is too high, too low, or correct. The `GuessNumberExecutor` issues an external request when it needs a human guess:

```csharp
[SendsMessage(typeof(int))]
internal sealed class GuessNumberExecutor() : Executor<NumberSignal>("GuessNumberExecutor")
{
    [MessageHandler]
    public async ValueTask HandleAsync(
        NumberSignal signal, IWorkflowContext context, CancellationToken cancellationToken = default)
    {
        // Ask the host for the next guess.
        await context.SendMessageAsync(
            new ExternalRequest<NumberSignal>(signal),
            cancellationToken);
    }
}
```

`ExternalRequest<TPayload>` carries:
- The current request payload — used by the host to decide what to ask the user.
- An internal correlation id so the framework can route the matching response back to the right port.

### Host-side: handle the request

```csharp
await using StreamingRun handle =
    await InProcessExecution.RunStreamingAsync(workflow, NumberSignal.Init);

await foreach (WorkflowEvent evt in handle.WatchStreamAsync())
{
    switch (evt)
    {
        case RequestInfoEvent req:
        {
            // Inspect the request payload and build a response.
            if (req.Request.TryGetDataAs<NumberSignal>(out var signal))
            {
                int guess = PromptUserForGuess(signal);
                ExternalResponse response = req.Request.CreateResponse(guess);
                await handle.SendResponseAsync(response);
            }
            break;
        }

        case WorkflowOutputEvent done:
            Console.WriteLine($"Workflow completed: {done.Data}");
            return;

        case WorkflowErrorEvent err:
            Console.Error.WriteLine(err.Exception);
            return;
    }
}
```

Key APIs

| Member | Purpose |
|--------|---------|
| `RequestInfoEvent.Request` | The `ExternalRequest` payload — opaque object reference. |
| `request.TryGetDataAs<T>(out T data)` | Strongly typed access to the request payload. Returns `false` if the type doesn't match. |
| `request.CreateResponse<T>(value)` | Build the matching `ExternalResponse` carrying `value`. |
| `handle.SendResponseAsync(response)` | Deliver the response to the workflow and unblock the waiting executor. |

### Multiple request ports in one workflow

You can have several `RequestPort`-style executors; each `RequestInfoEvent` carries enough metadata to match the correct response. Always go through `request.CreateResponse(...)` rather than constructing an `ExternalResponse` manually — the framework uses the embedded correlation to route responses.

### Combining checkpoints with HITL

The natural pairing: a workflow waiting on a human guess could take hours. Add a `CheckpointManager` so the run survives a process restart, then on startup:

1. Load the saved `CheckpointInfo` (from your persistence layer).
2. Call `await run.RestoreCheckpointAsync(saved, ct)`.
3. Re-enter the `WatchStreamAsync` loop — the next event will be the pending `RequestInfoEvent`.
4. Get the human input, call `SendResponseAsync`, continue.

This is the standard pattern for approval workflows, where the human decision may arrive days after the workflow paused.

## Common Errors and Recovery

| Symptom | Cause | Fix |
|--------|-------|-----|
| `SendResponseAsync` throws "no pending request" | The workflow has already moved on, or the correlation id doesn't match. | Always derive responses via `request.CreateResponse(...)`. |
| Workflow appears stuck after first input | Agent executors never received a `TurnToken`. | Send `await run.TrySendMessageAsync(new TurnToken(emitEvents: true))` after the initial input. |
| `RestoreCheckpointAsync` throws on type mismatch | Workflow shape changed (executor renamed / removed) since the checkpoint was written. | Treat the workflow definition as part of the checkpoint contract — version it, or discard old checkpoints. |
| Side effects repeat after resume | Executor wasn't idempotent. | Guard side-effecting calls with an idempotency key stored in shared state. |
| `ExecutorFailedEvent` instead of throw | Exceptions inside executors are converted to events. | Inspect `.ExecutorId` and `.Data`; rethrow yourself in the consumer if you want fail-fast behavior. |
