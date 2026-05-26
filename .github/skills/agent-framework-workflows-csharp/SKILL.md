---
name: agent-framework-workflows-csharp
description: Build deterministic, multi-step workflows with the Microsoft Agent Framework .NET SDK (Microsoft.Agents.AI.Workflows). Use when composing executors and edges into a DAG, running agents as graph nodes, doing fan-out/fan-in concurrency, conditional routing, shared state, streaming events, checkpoint/resume, human-in-the-loop request ports, or higher-level orchestration patterns (sequential, concurrent, handoff, group chat, Magentic). Covers WorkflowBuilder, AgentWorkflowBuilder, IWorkflowContext, CheckpointManager, and the StreamingRun event loop.
license: MIT
metadata:
  author: Microsoft
  version: "1.0.0"
  package: Microsoft.Agents.AI.Workflows
---

# Agent Framework Workflows (.NET)

Compose deterministic, multi-step workflows on top of the Microsoft Agent Framework .NET SDK. Executors are graph nodes; edges route messages between them. `AIAgent` instances can be dropped in as executors, and pre-built builders cover the common multi-agent patterns (sequential, concurrent, handoff, group chat, Magentic).

## Architecture

```
Input → WorkflowBuilder(startExecutor)
         ├─ .AddEdge(a, b)                           (sequential)
         ├─ .AddEdge(a, b, condition: ...)           (conditional)
         ├─ .AddFanOutEdge(a, [b, c])                (parallel)
         ├─ .AddFanInBarrierEdge([b, c], d)          (join)
         └─ .WithOutputFrom(d)                       (declared outputs)
                    ↓
            workflow = builder.Build()
                    ↓
      await using StreamingRun run =
         await InProcessExecution.RunStreamingAsync(workflow, input);
      await foreach (WorkflowEvent evt in run.WatchStreamAsync()) { ... }
                    ↓
     ExecutorCompletedEvent | AgentResponseUpdateEvent |
     RequestInfoEvent | WorkflowOutputEvent | WorkflowErrorEvent
```

`Executor<TIn, TOut>` is the unit of work. `IWorkflowContext` is the per-call ambient context — it lets you send messages, queue shared-state updates, read shared state, and yield outputs. Agents become executors automatically when added to the graph (or explicitly via `BindAsExecutor`).

## Installation

```bash
dotnet add package Microsoft.Agents.AI.Workflows --prerelease
dotnet add package Microsoft.Agents.AI --prerelease
dotnet add package Microsoft.Extensions.AI --prerelease

# For agent-based samples (any chat client works):
dotnet add package Azure.AI.OpenAI --prerelease
dotnet add package Azure.Identity
```

## Prerequisites

- **.NET 10 SDK** or later
- For workflows that contain `AIAgent` executors, a chat client. The Microsoft samples use `AzureOpenAIClient` with `AzureCliCredential`; any `IChatClient` works.
- For Azure OpenAI samples: a deployment configured and the user signed in via `az login` with `Cognitive Services OpenAI Contributor` on the resource.

## Environment Variables

```bash
# Required for any agent-based workflow that uses Azure OpenAI:
export AZURE_OPENAI_ENDPOINT="https://<resource>.openai.azure.com/"
export AZURE_OPENAI_DEPLOYMENT_NAME="gpt-5.4-mini"

# For samples that use the Azure AI Project client (Concurrent sample):
export AZURE_AI_PROJECT_ENDPOINT="https://<project>.services.ai.azure.com/api/projects/<project-id>"
export AZURE_AI_MODEL_DEPLOYMENT_NAME="gpt-5.4-mini"
```

## Authentication & Lifecycle

> **🔑 Two rules apply to every code sample below:**
>
> 1. **Prefer `DefaultAzureCredential` / `AzureCliCredential`.** Works locally and in Azure with no code changes. Avoid keys and connection strings.
> 2. **Dispose the streaming run.** Always wrap it in `await using StreamingRun run = await InProcessExecution.RunStreamingAsync(...)` so the event channel and any executor resources are released.

```csharp
using Azure.Identity;

// Development
var credential = new AzureCliCredential();

// Production
// var credential = new DefaultAzureCredential();
// or a specific credential: new ManagedIdentityCredential();
```

## Core Workflow

### Basic Workflow with Executors and Edges

Two executors connected sequentially; the second one declares the workflow output. Mirrors [`_StartHere/01_Streaming`](https://github.com/microsoft/agent-framework/tree/main/dotnet/samples/03-workflows/_StartHere/01_Streaming).

```csharp
using Microsoft.Agents.AI.Workflows;

// Define executors
internal sealed class UppercaseExecutor() : Executor<string, string>("UppercaseExecutor")
{
    public override ValueTask<string> HandleAsync(
        string message,
        IWorkflowContext context,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(message.ToUpperInvariant());
}

internal sealed class ReverseTextExecutor() : Executor<string, string>("ReverseTextExecutor")
{
    public override ValueTask<string> HandleAsync(
        string message,
        IWorkflowContext context,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(string.Concat(message.Reverse()));
}

// Build and run
UppercaseExecutor uppercase = new();
ReverseTextExecutor reverse = new();

Workflow workflow = new WorkflowBuilder(uppercase)
    .AddEdge(uppercase, reverse)
    .WithOutputFrom(reverse)
    .Build();

await using StreamingRun run = await InProcessExecution.RunStreamingAsync(workflow, input: "Hello, World!");
await foreach (WorkflowEvent evt in run.WatchStreamAsync())
{
    if (evt is ExecutorCompletedEvent done)
    {
        Console.WriteLine($"{done.ExecutorId}: {done.Data}");
    }
}
```

### Agents as Executors

Drop a `ChatClientAgent` straight into the graph. Agents wrapped as executors **queue** incoming messages and only start processing when they receive a `TurnToken`. Mirrors [`_StartHere/02_AgentsInWorkflows`](https://github.com/microsoft/agent-framework/tree/main/dotnet/samples/03-workflows/_StartHere/02_AgentsInWorkflows).

```csharp
using Azure.AI.OpenAI;
using Azure.Identity;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;

string endpoint = Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT")
                  ?? throw new InvalidOperationException("AZURE_OPENAI_ENDPOINT is not set.");
string deployment = Environment.GetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT_NAME") ?? "gpt-5.4-mini";

IChatClient chatClient = new AzureOpenAIClient(new Uri(endpoint), new AzureCliCredential())
    .GetChatClient(deployment)
    .AsIChatClient();

static ChatClientAgent Translator(string lang, IChatClient client) =>
    new(client, $"You are a translation assistant that translates the provided text to {lang}.");

AIAgent french = Translator("French", chatClient);
AIAgent spanish = Translator("Spanish", chatClient);
AIAgent english = Translator("English", chatClient);

Workflow workflow = new WorkflowBuilder(french)
    .AddEdge(french, spanish)
    .AddEdge(spanish, english)
    .Build();

await using StreamingRun run = await InProcessExecution.RunStreamingAsync(
    workflow, new ChatMessage(ChatRole.User, "Hello World!"));

// Required to kick off agent executors:
await run.TrySendMessageAsync(new TurnToken(emitEvents: true));

await foreach (WorkflowEvent evt in run.WatchStreamAsync())
{
    if (evt is AgentResponseUpdateEvent upd)
    {
        Console.WriteLine($"{upd.ExecutorId}: {upd.Update.Text}");
    }
}
```

### Fan-Out / Fan-In

Run two agents in parallel and collect their answers with a fan-in barrier. Mirrors [`Concurrent/Concurrent`](https://github.com/microsoft/agent-framework/tree/main/dotnet/samples/03-workflows/Concurrent/Concurrent).

```csharp
ChatClientAgent physicist = new(chatClient,
    name: "Physicist",
    instructions: "You answer questions from a physics perspective.");

ChatClientAgent chemist = new(chatClient,
    name: "Chemist",
    instructions: "You answer questions from a chemistry perspective.");

// Bind agents as executors that do NOT forward incoming messages downstream
// (we don't want the user prompt to leak past the agents).
Executor physicistExec = physicist.BindAsExecutor(new AIAgentHostOptions { ForwardIncomingMessages = false });
Executor chemistExec   = chemist.BindAsExecutor(new AIAgentHostOptions { ForwardIncomingMessages = false });

ConcurrentStartExecutor start = new();
ConcurrentAggregationExecutor aggregate = new();

Workflow workflow = new WorkflowBuilder(start)
    .AddFanOutEdge(start, [physicistExec, chemistExec])
    .AddFanInBarrierEdge([physicistExec, chemistExec], aggregate)
    .WithOutputFrom(aggregate)
    .Build();

await using StreamingRun run = await InProcessExecution.RunStreamingAsync(workflow, input: "What is temperature?");
await foreach (WorkflowEvent evt in run.WatchStreamAsync())
{
    if (evt is WorkflowOutputEvent o)
    {
        Console.WriteLine(o.Data);
    }
}
```

For deterministic map/reduce-style workflows where the same executor logic should run once per partition (for example, one demand forecast per SKU), instantiate one executor per partition with a stable unique executor ID, fan out to those instances, then join them with a barrier aggregator. The aggregator can collect messages in `HandleAsync` and emit the final workflow output from `OnMessageDeliveryFinishedAsync`.

See [references/edges.md](references/edges.md) for the `ConcurrentStartExecutor` / `ConcurrentAggregationExecutor` skeletons and the `[SendsMessage]` / `[YieldsOutput]` attributes.

### Conditional Edges

Route messages to different executors based on the upstream result. Mirrors [`ConditionalEdges/01_EdgeCondition`](https://github.com/microsoft/agent-framework/tree/main/dotnet/samples/03-workflows/ConditionalEdges/01_EdgeCondition).

```csharp
static Func<object?, bool> IsSpam(bool expected) =>
    result => result is DetectionResult d && d.IsSpam == expected;

Workflow workflow = new WorkflowBuilder(spamDetector)
    .AddEdge(spamDetector, emailAssistant, condition: IsSpam(expected: false))
    .AddEdge(emailAssistant, sendEmail)
    .AddEdge(spamDetector, handleSpam, condition: IsSpam(expected: true))
    .WithOutputFrom(handleSpam, sendEmail)
    .Build();
```

### Shared State

Pass large blobs by reference instead of along edges. Mirrors [`SharedStates/Program.cs`](https://github.com/microsoft/agent-framework/tree/main/dotnet/samples/03-workflows/SharedStates).

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
        return fileId;   // pass the id downstream
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

### Pre-Built Multi-Agent Patterns (`AgentWorkflowBuilder`)

Skip the manual graph wiring for the common cases. Mirrors [`_StartHere/03_AgentWorkflowPatterns`](https://github.com/microsoft/agent-framework/tree/main/dotnet/samples/03-workflows/_StartHere/03_AgentWorkflowPatterns).

```csharp
// Sequential — each agent's reply becomes the next agent's input.
Workflow seq = AgentWorkflowBuilder.BuildSequential(new[] { french, spanish, english });

// Concurrent — every agent sees the same input; outputs are aggregated.
Workflow conc = AgentWorkflowBuilder.BuildConcurrent(new[] { french, spanish, english });

// Handoff — a triage agent routes to specialists and back.
Workflow handoff = AgentWorkflowBuilder
    .CreateHandoffBuilderWith(triageAgent)
    .WithHandoffs(triageAgent, [mathTutor, historyTutor])
    .WithHandoffs([mathTutor, historyTutor], triageAgent)
    .Build();

// Group chat — round-robin manager with a hard cap.
Workflow group = AgentWorkflowBuilder
    .CreateGroupChatBuilderWith(agents => new RoundRobinGroupChatManager(agents) { MaximumIterationCount = 5 })
    .AddParticipants(new[] { french, spanish, english })
    .WithName("Translation Round Robin Workflow")
    .Build();
```

See [references/agents-in-workflows.md](references/agents-in-workflows.md) for handoff and Magentic orchestration in depth.

### Human-in-the-Loop (`RequestPort`)

Use `RequestInfoEvent` to ask the outside world for input, then return an `ExternalResponse`. Mirrors [`HumanInTheLoop/HumanInTheLoopBasic`](https://github.com/microsoft/agent-framework/tree/main/dotnet/samples/03-workflows/HumanInTheLoop/HumanInTheLoopBasic).

```csharp
await using StreamingRun handle = await InProcessExecution.RunStreamingAsync(workflow, NumberSignal.Init);

await foreach (WorkflowEvent evt in handle.WatchStreamAsync())
{
    switch (evt)
    {
        case RequestInfoEvent req:
            ExternalResponse response = HandleExternalRequest(req.Request);
            await handle.SendResponseAsync(response);
            break;

        case WorkflowOutputEvent done:
            Console.WriteLine($"Workflow completed with result: {done.Data}");
            return;
    }
}
```

### Checkpoint and Resume

Pass a `CheckpointManager` to the run and the framework saves state at every super-step boundary. Mirrors [`Checkpoint/CheckpointAndResume`](https://github.com/microsoft/agent-framework/tree/main/dotnet/samples/03-workflows/Checkpoint/CheckpointAndResume).

```csharp
CheckpointManager checkpointManager = CheckpointManager.Default;
List<CheckpointInfo> checkpoints = new();

await using StreamingRun run = await InProcessExecution.RunStreamingAsync(
    workflow, NumberSignal.Init, checkpointManager);

await foreach (WorkflowEvent evt in run.WatchStreamAsync())
{
    if (evt is SuperStepCompletedEvent step &&
        step.CompletionInfo?.Checkpoint is CheckpointInfo cp)
    {
        checkpoints.Add(cp);
    }
}

// Resume from any saved checkpoint.
await run.RestoreCheckpointAsync(checkpoints[5], CancellationToken.None);
await foreach (WorkflowEvent evt in run.WatchStreamAsync())
{
    // continue handling events
}
```

## Core Types Quick Reference

| Type | Namespace | Purpose |
|------|-----------|---------|
| `WorkflowBuilder` | `Microsoft.Agents.AI.Workflows` | Manual graph construction (executors + edges). |
| `Executor<TIn, TOut>` / `Executor<TIn>` | `Microsoft.Agents.AI.Workflows` | Base class for workflow nodes. Override `HandleAsync`. |
| `IWorkflowContext` | `Microsoft.Agents.AI.Workflows` | Per-call context: `SendMessageAsync`, `QueueStateUpdateAsync`, `ReadStateAsync`, `YieldOutputAsync`. |
| `[YieldsOutput(typeof(T))]` | `Microsoft.Agents.AI.Workflows` | Declares that an executor yields workflow output of type `T`. |
| `[SendsMessage(typeof(T))]` / `[MessageHandler]` | `Microsoft.Agents.AI.Workflows` | Declares message types an executor sends / handles. |
| `AgentWorkflowBuilder` | `Microsoft.Agents.AI.Workflows` | Pre-built multi-agent patterns (sequential / concurrent / handoff / group chat / Magentic). |
| `AIAgentHostOptions` | `Microsoft.Agents.AI.Workflows` | Options for `agent.BindAsExecutor(...)`, e.g. `ForwardIncomingMessages = false`. |
| `TurnToken` | `Microsoft.Agents.AI.Workflows` | Triggers queued agent executors to start processing. |
| `InProcessExecution.RunAsync` | `Microsoft.Agents.AI.Workflows` | Non-streaming run; events collected in `run.NewEvents`. |
| `InProcessExecution.RunStreamingAsync` | `Microsoft.Agents.AI.Workflows` | Streaming run; iterate `run.WatchStreamAsync()`. |
| `CheckpointManager` / `CheckpointInfo` | `Microsoft.Agents.AI.Workflows` | Save/restore super-step state. |
| `ExternalRequest` / `ExternalResponse` | `Microsoft.Agents.AI.Workflows` | Human-in-the-loop request/response payloads. |

## Workflow Event Types

| Event | When it fires |
|-------|--------------|
| `ExecutorCompletedEvent` | An executor finished and emitted `Data`. |
| `AgentResponseUpdateEvent` | Streaming text from an agent executor (`Update.Text`, `Update.Contents`). |
| `RequestInfoEvent` | A `RequestPort` is asking for external input. |
| `SuperStepCompletedEvent` | A super-step finished; checkpoint available on `CompletionInfo.Checkpoint`. |
| `WorkflowOutputEvent` | Workflow yielded an output (via `WithOutputFrom` + `YieldOutputAsync`). |
| `WorkflowErrorEvent` | An unhandled workflow-level error. Inspect `.Exception`. |
| `ExecutorFailedEvent` | A specific executor threw. Inspect `.ExecutorId` and `.Data`. |

Always handle the error events — uncaught executor exceptions don't bubble out of `WatchStreamAsync`; they arrive as events.

## Complete Example

End-to-end translation pipeline that fans out to two specialists, joins their answers, and streams output. Combines the patterns above.

```csharp
using Azure.AI.OpenAI;
using Azure.Identity;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;

string endpoint = Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT")
    ?? throw new InvalidOperationException("AZURE_OPENAI_ENDPOINT is not set.");
string deployment = Environment.GetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT_NAME") ?? "gpt-5.4-mini";

IChatClient chatClient = new AzureOpenAIClient(new Uri(endpoint), new AzureCliCredential())
    .GetChatClient(deployment)
    .AsIChatClient();

ChatClientAgent physicist = new(chatClient,
    name: "Physicist",
    instructions: "Answer briefly from a physics perspective.");
ChatClientAgent chemist = new(chatClient,
    name: "Chemist",
    instructions: "Answer briefly from a chemistry perspective.");

Executor physicistExec = physicist.BindAsExecutor(new AIAgentHostOptions { ForwardIncomingMessages = false });
Executor chemistExec   = chemist.BindAsExecutor(new AIAgentHostOptions { ForwardIncomingMessages = false });

ConcurrentStartExecutor start = new();
ConcurrentAggregationExecutor aggregate = new();

Workflow workflow = new WorkflowBuilder(start)
    .AddFanOutEdge(start, [physicistExec, chemistExec])
    .AddFanInBarrierEdge([physicistExec, chemistExec], aggregate)
    .WithOutputFrom(aggregate)
    .Build();

await using StreamingRun run = await InProcessExecution.RunStreamingAsync(workflow, input: "What is temperature?");
await foreach (WorkflowEvent evt in run.WatchStreamAsync())
{
    switch (evt)
    {
        case AgentResponseUpdateEvent upd:
            Console.Write(upd.Update.Text);
            break;
        case WorkflowOutputEvent done:
            Console.WriteLine();
            Console.WriteLine("--- Final ---");
            Console.WriteLine(done.Data);
            break;
        case WorkflowErrorEvent err:
            Console.Error.WriteLine(err.Exception);
            break;
        case ExecutorFailedEvent fail:
            Console.Error.WriteLine($"{fail.ExecutorId}: {fail.Data}");
            break;
    }
}
```

`ConcurrentStartExecutor` broadcasts the user message followed by a `TurnToken`; `ConcurrentAggregationExecutor` collects `List<ChatMessage>` and yields the joined transcript. See [references/edges.md](references/edges.md) for the full implementations.

## Conventions

- **Always wrap streaming runs in `await using`.** The run owns disposable resources.
- **`AddEdge` accepts a `condition: Func<object?, bool>`** for routing; cast the input inside the lambda.
- **Use shared state for large blobs.** Pass the key downstream and call `context.ReadStateAsync<T>(key, scopeName: ...)` to materialize it.
- **Agents queue until `TurnToken` arrives.** When you mix raw executors with agent executors, always send `new TurnToken(emitEvents: true)` after the initial input — otherwise the agents will never run.
- **Annotate executor surface area** with `[SendsMessage(typeof(T))]`, `[MessageHandler]`, and `[YieldsOutput(typeof(T))]`. The workflow graph validator uses them.
- **Prefer `AgentWorkflowBuilder`** when your composition is one of the known patterns (sequential / concurrent / handoff / group chat / Magentic). Drop down to `WorkflowBuilder` only when you need custom routing or non-agent executors.
- **Handle every event branch.** `WorkflowErrorEvent` and `ExecutorFailedEvent` will not throw on the iterator — log them or rethrow yourself.

## Best Practices

1. **One class per executor.** Keep `HandleAsync` short; push shared logic into helpers. The framework uses the class name as the default `ExecutorId`.
2. **Make executors deterministic where possible.** Side-effecting executors (I/O, agent calls) should be quarantined so checkpoints replay safely.
3. **Use `BindAsExecutor(new AIAgentHostOptions { ForwardIncomingMessages = false })`** when you don't want the user prompt to keep flowing past an agent node.
4. **Checkpoint long workflows.** Pass `CheckpointManager.Default` to `RunStreamingAsync` and stash `CheckpointInfo` objects so you can resume after a crash.
5. **Validate the graph at construction time.** `Build()` throws on missing edges, duplicate executor IDs, or undeclared message types — let those exceptions surface during development, not in production.
6. **For multi-agent orchestration prefer the pre-built builders** in `AgentWorkflowBuilder`; they already handle the `TurnToken` plumbing and aggregation correctly.

## Reference Files

- [references/executors.md](references/executors.md): `Executor<TIn, TOut>`, `IWorkflowContext`, shared state, `YieldOutputAsync`, attribute annotations.
- [references/edges.md](references/edges.md): Sequential, fan-out / fan-in, conditional edges, switch-case, multi-selection routing.
- [references/agents-in-workflows.md](references/agents-in-workflows.md): Agents as executors, `BindAsExecutor`, `TurnToken`, `AgentWorkflowBuilder` patterns (sequential / concurrent / handoff / group chat / Magentic).
- [references/checkpoints-and-hitl.md](references/checkpoints-and-hitl.md): `CheckpointManager`, super steps, `RestoreCheckpointAsync`, request ports, `ExternalRequest` / `ExternalResponse`.
