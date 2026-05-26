# Executors Reference (.NET Workflows)

Detailed patterns for writing custom executors. An executor is the unit of work in a workflow graph — a class that handles a typed input message, optionally produces a typed output, and communicates with the rest of the graph through `IWorkflowContext`.

## Base Classes

| Base | When to use |
|------|------------|
| `Executor<TIn, TOut>` | Common case: consume one message type, produce one output that flows along edges. |
| `Executor<TIn>` | Terminal / side-effect node: handle a message but don't emit downstream messages (you may still call `context.YieldOutputAsync(...)`). |
| `Executor` (parameterless generic) | Multi-handler executor: declare each input with `[MessageHandler]`. Use with `[SendsMessage(typeof(T))]` so the graph validator knows what you produce. |

```csharp
using Microsoft.Agents.AI.Workflows;

internal sealed class UppercaseExecutor() : Executor<string, string>("UppercaseExecutor")
{
    public override ValueTask<string> HandleAsync(
        string message,
        IWorkflowContext context,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(message.ToUpperInvariant());
}
```

The string passed to the base constructor is the **ExecutorId** — it appears on every event (`ExecutorCompletedEvent.ExecutorId`, `AgentResponseUpdateEvent.ExecutorId`, etc.). Make it unique inside one workflow; the default is the class name.

## `IWorkflowContext`

Every `HandleAsync` receives an `IWorkflowContext`. The methods you'll use most:

| Member | Purpose |
|--------|--------|
| `SendMessageAsync(T, CancellationToken)` | Send a message of type `T` to subscribers; used by multi-handler executors instead of returning a value. |
| `QueueStateUpdateAsync<T>(key, value, scopeName, ct)` | Stage a shared-state update. Visible to downstream executors **after** the current super-step boundary. |
| `ReadStateAsync<T>(key, scopeName, ct)` | Read shared state written by an earlier super-step. Returns `null` if the key was never set. |
| `YieldOutputAsync<T>(value, ct)` | Emit a `WorkflowOutputEvent` carrying `value`. Requires the executor to be declared in `WithOutputFrom(...)` (or marked `[YieldsOutput(typeof(T))]`). |
| `AddEventAsync(WorkflowEvent, ct)` | Push a custom event into the workflow stream. |

## Attribute Annotations

The graph builder uses attributes to validate connections. Apply them to executor types — not methods (except `[MessageHandler]`).

```csharp
[SendsMessage(typeof(ChatMessage))]
[SendsMessage(typeof(TurnToken))]
internal sealed partial class ConcurrentStartExecutor() : Executor("ConcurrentStartExecutor")
{
    [MessageHandler]
    public async ValueTask HandleAsync(
        string message,
        IWorkflowContext context,
        CancellationToken cancellationToken = default)
    {
        await context.SendMessageAsync(new ChatMessage(ChatRole.User, message), cancellationToken);
        await context.SendMessageAsync(new TurnToken(emitEvents: false), cancellationToken);
    }
}
```

| Attribute | Effect |
|-----------|-------|
| `[SendsMessage(typeof(T))]` | Declares an outgoing message type. Required when you use `SendMessageAsync` instead of returning a value. |
| `[MessageHandler]` | Marks a method as a typed message handler. The first parameter is the message; the method must return `ValueTask` or `ValueTask<T>`. |
| `[YieldsOutput(typeof(T))]` | Declares this executor yields workflow output of type `T` via `YieldOutputAsync`. The graph builder lights up `WithOutputFrom(this)` for it. |

## Returning vs. Sending Messages

There are two ways to push data downstream:

1. **Return a value** from `HandleAsync` in `Executor<TIn, TOut>` — the framework forwards it to all outgoing edges.
2. **Call `context.SendMessageAsync(...)`** — required when you publish more than one downstream message, or when the output type varies. Declare every type with `[SendsMessage(typeof(T))]`.

```csharp
// (1) Return — simple sequential routing.
public override ValueTask<string> HandleAsync(string m, IWorkflowContext c, CancellationToken ct = default) =>
    ValueTask.FromResult(m.ToUpperInvariant());

// (2) Send — multiple downstream messages from one handler.
[SendsMessage(typeof(ChatMessage))]
[SendsMessage(typeof(TurnToken))]
internal sealed partial class StartExec() : Executor("StartExec")
{
    [MessageHandler]
    public async ValueTask HandleAsync(string m, IWorkflowContext c, CancellationToken ct = default)
    {
        await c.SendMessageAsync(new ChatMessage(ChatRole.User, m), ct);
        await c.SendMessageAsync(new TurnToken(emitEvents: false), ct);
    }
}
```

## Shared State

Shared state is a key/value store scoped by a `scopeName` string. Updates are queued during a super-step and become visible at the next super-step boundary, so concurrent executors in the same step don't see each other's writes mid-flight.

```csharp
internal static class FileContentStateConstants
{
    public const string FileContentStateScope = "FileContentState";
}

internal sealed class FileReadExecutor() : Executor<string, string>("FileReadExecutor")
{
    public override async ValueTask<string> HandleAsync(
        string message, IWorkflowContext context, CancellationToken cancellationToken = default)
    {
        string content = Resources.Read(message);
        string fileId = Guid.NewGuid().ToString("N");
        await context.QueueStateUpdateAsync(
            fileId, content,
            scopeName: FileContentStateConstants.FileContentStateScope,
            cancellationToken);

        return fileId; // pass the id, not the blob
    }
}

internal sealed class WordCountingExecutor() : Executor<string, FileStats>("WordCountingExecutor")
{
    public override async ValueTask<FileStats> HandleAsync(
        string message, IWorkflowContext context, CancellationToken cancellationToken = default)
    {
        string content = await context.ReadStateAsync<string>(
            message,
            scopeName: FileContentStateConstants.FileContentStateScope,
            cancellationToken)
            ?? throw new InvalidOperationException("File content state not found");

        return new FileStats
        {
            WordCount = content.Split([' ', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries).Length,
        };
    }
}
```

Rules of thumb
- **Pass keys, not blobs**, along edges. Edges are part of the workflow's serialized form (think checkpointing) — keeping large payloads out of them keeps checkpoints small.
- **One scope per logical dataset.** Co-locate related keys under a single `scopeName` constant.
- **Don't read your own writes inside the same super-step.** They aren't visible until the next one.

## Yielding Workflow Output

`WorkflowOutputEvent` fires whenever an executor yields output. Two requirements:

1. The executor type must be reachable via `WithOutputFrom(...)` on the builder, **or** decorated with `[YieldsOutput(typeof(T))]`.
2. The executor must call `context.YieldOutputAsync(value, ct)`.

```csharp
[YieldsOutput(typeof(string))]
internal sealed class SendEmailExecutor() : Executor<EmailResponse>("SendEmailExecutor")
{
    public override async ValueTask HandleAsync(
        EmailResponse message, IWorkflowContext context, CancellationToken cancellationToken = default) =>
        await context.YieldOutputAsync($"Email sent: {message.Response}", cancellationToken);
}
```

If multiple executors can produce output, list them all in `WithOutputFrom(a, b, ...)` and consume each `WorkflowOutputEvent` in your event loop:

```csharp
Workflow workflow = new WorkflowBuilder(spamDetector)
    .AddEdge(spamDetector, emailAssistant, condition: IsSpam(false))
    .AddEdge(emailAssistant, sendEmail)
    .AddEdge(spamDetector, handleSpam, condition: IsSpam(true))
    .WithOutputFrom(handleSpam, sendEmail)   // either branch can yield output
    .Build();
```

## Multi-Message Aggregation (`OnMessageDeliveryFinishedAsync`)

When an executor needs to wait for *all* upstream messages of a super-step before producing output (typical fan-in), accumulate inside `HandleAsync` and finalize in `OnMessageDeliveryFinishedAsync`:

```csharp
[YieldsOutput(typeof(string))]
internal sealed partial class ConcurrentAggregationExecutor()
    : Executor<List<ChatMessage>>("ConcurrentAggregationExecutor")
{
    private readonly List<ChatMessage> _messages = [];

    public override ValueTask HandleAsync(
        List<ChatMessage> message, IWorkflowContext context, CancellationToken ct = default)
    {
        _messages.AddRange(message);
        return ValueTask.CompletedTask;
    }

    protected override ValueTask OnMessageDeliveryFinishedAsync(
        IWorkflowContext context, CancellationToken cancellationToken = default)
    {
        StringBuilder result = new();
        foreach (ChatMessage m in _messages)
        {
            result.AppendLine($"{m.AuthorName}: {m.Text}");
        }
        _messages.Clear();
        return context.YieldOutputAsync(result.ToString(), cancellationToken);
    }
}
```

## Errors and Cancellation

- `HandleAsync` receives a `CancellationToken` — pass it down to every async call you make (HTTP, agent runs, file I/O). The framework respects cancellation at super-step boundaries.
- Unhandled exceptions in `HandleAsync` become `ExecutorFailedEvent` on the stream (not throws). Always handle this event in the consumer.
- Throw `InvalidOperationException` with a meaningful message when invariants are violated — the message ends up in `executorFailed.Data`.

```csharp
public override async ValueTask<EmailResponse> HandleAsync(
    DetectionResult message, IWorkflowContext context, CancellationToken cancellationToken = default)
{
    if (message.IsSpam)
    {
        throw new InvalidOperationException("This executor should only handle non-spam messages.");
    }

    var email = await context.ReadStateAsync<Email>(message.EmailId, scopeName: "EmailState", cancellationToken)
        ?? throw new InvalidOperationException("Email not found.");

    var response = await _emailAssistantAgent.RunAsync(email.EmailContent, cancellationToken: cancellationToken);
    return JsonSerializer.Deserialize<EmailResponse>(response.Text)!;
}
```
