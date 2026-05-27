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

## Workshop-verified gotchas (LAB 04)

These are the traps observed while building the ZavaShop fulfillment workflow in [`workshop/LAB04-fulfillment-workflow/FulfillmentWorkflow/Program.cs`](../../../workshop/LAB04-fulfillment-workflow/FulfillmentWorkflow/Program.cs). Every bullet has been reproduced against `Microsoft.Agents.AI.Workflows 1.7.0` (the version that resolves from `Version="*-*"` in November 2025). The patterns above (sequential, fan-out/fan-in with agents, `AgentWorkflowBuilder`) work as documented — these are the **typed-executor + HITL + checkpoint** corner cases that don't.

1. **`AddFanInBarrierEdge` delivers each upstream message individually, not as a `List<TOut>`.** The "Aggregation executor for fan-in" pattern earlier in this SKILL (an `Executor<List<ChatMessage>>` that receives one bundle) only works for agent executors because `BindAsExecutor` + `TurnToken` machinery wraps the agent output. For **typed executors** that return `TOut` directly (e.g. `Executor<OrderRecord, LegResult>`), the join node receives the messages one at a time. Subclass `Executor<TOut, TJoin?>`, buffer inside an instance field, and gate downstream emission with a **null sentinel + conditional edge**:

   ```csharp
   internal sealed class AllocatorExecutor() : Executor<LegResult, AllocationPlan?>("allocator")
   {
       private readonly List<LegResult> _legs = new();
       private const int ExpectedLegs = 2;   // stock_check + shipping_quote

       public override ValueTask<AllocationPlan?> HandleAsync(
           LegResult leg, IWorkflowContext ctx, CancellationToken ct = default)
       {
           _legs.Add(leg);
           if (_legs.Count < ExpectedLegs)
           {
               return ValueTask.FromResult<AllocationPlan?>(null);  // sentinel — don't fire downstream yet
           }

           AllocationPlan plan = BuildPlan(_legs);
           _legs.Clear();
           return ValueTask.FromResult<AllocationPlan?>(plan);
       }
   }

   // The conditional edge filters out the sentinel:
   builder.AddEdge<AllocationPlan?>(allocator, approval, condition: msg => msg is AllocationPlan);
   ```

   The `OnMessageDeliveryFinishedAsync` recipe in [references/edges.md](references/edges.md) (the `SupplyChainAggregatorExecutor` snippet) also works, but only when the aggregator is a **terminal** node yielding workflow output. If the aggregator's result needs to flow into another executor (HITL gate, dispatcher, …), use the buffer-and-sentinel pattern above — `OnMessageDeliveryFinishedAsync` fires after the super-step closes, by which point the next executor has already missed its delivery window.

2. **The streaming API names are `RunStreamingAsync` / `ResumeStreamingAsync`, not `StreamAsync` / `ResumeStreamAsync`.** And `ResumeStreamingAsync` takes **four** arguments — `(workflow, checkpoint, checkpointManager, cancellationToken)` — there is no `sessionId` parameter on the resume overload. There is also no `Checkpointed<TRun>` type; the call returns a bare `StreamingRun` whether or not a `CheckpointManager` was passed.

   ```csharp
   await using StreamingRun run = await InProcessExecution.RunStreamingAsync(
       workflow, orderId, checkpointManager, sessionId: runId, cancellationToken: ct);

   // Later — fresh process, fresh run:
   await using StreamingRun resumed = await InProcessExecution.ResumeStreamingAsync(
       workflow, savedCheckpoint, checkpointManager, ct);
   ```

3. **Multi-output executors must override `ConfigureProtocol`.** When one executor can both `SendMessageAsync(msg)` downstream *and* `YieldOutputAsync(output)` to the workflow stream — for example, an "approval resume" node that forwards an approved plan onto the dispatcher but yields a `RejectedVoucher` to the caller on rejection — the graph validator needs both declared. Subclass `Executor<TIn>` (no `TOut`) and override the protected `ConfigureProtocol`:

   ```csharp
   internal sealed class ApprovalResumeExecutor() : Executor<HumanApprovalResponse>("approval_resume")
   {
       protected override void ConfigureProtocol(ProtocolBuilder protocol)
       {
           base.ConfigureProtocol(protocol);
           protocol.SendsMessageType(typeof(AllocationPlan));   // forward to dispatch
           protocol.YieldsOutputType(typeof(RejectedVoucher));  // or yield rejection
       }

       public override async ValueTask HandleAsync(
           HumanApprovalResponse resp, IWorkflowContext ctx, CancellationToken ct = default)
       {
           AllocationPlan? plan = await ctx.ReadStateAsync<AllocationPlan>("pending_plan", "Approval", ct);
           if (resp.Approved && plan is not null) await ctx.SendMessageAsync(plan, ct);
           else                                   await ctx.YieldOutputAsync(new RejectedVoucher(...), ct);
       }
   }
   ```

   The `[SendsMessage(...)]` / `[YieldsOutput(...)]` attributes from the earlier sections of this SKILL only register a *single* type each; `ConfigureProtocol` is the way to declare both behaviors on the same node. Attribute-only declaration on a multi-output executor will pass `Build()` but the runtime will drop the messages it wasn't told about.

4. **`ExternalRequest` payload access is via `TryGetDataAs<T>(out T)`** — there is no `request.DataIs<T>()` or `request.Data as T`. Always go through the try-pattern, then build the response via `request.CreateResponse(value)`:

   ```csharp
   if (evt is RequestInfoEvent reqEvt &&
       reqEvt.Request.TryGetDataAs<HumanApprovalRequest>(out HumanApprovalRequest? req))
   {
       bool decision = PromptUser(req);
       ExternalResponse response = reqEvt.Request.CreateResponse(new HumanApprovalResponse(decision, "..."));
       await run.SendResponseAsync(response);
   }
   ```

5. **Wrap a typed workflow as an agent via `workflow.AsAIAgent(...)`, not `AsAgent(...)`.** The Python analog is `workflow.as_agent("ZavaFulfillment")`; in .NET the extension method is `Microsoft.Agents.AI.Workflows.WorkflowHostingExtensions.AsAIAgent(workflow, id, name, description, executionEnvironment, includeExceptionDetails, includeWorkflowOutputsInResponse)`. The returned `AIAgent` exposes the same `RunAsync` surface as any other agent.

   ```csharp
   AIAgent fulfillment = workflow.AsAIAgent(
       id: "zava-fulfillment",
       name: "ZavaFulfillment",
       description: "Order intake → stock + freight → HITL gate → dispatch → finance.");

   AgentRunResponse resp = await fulfillment.RunAsync("ORD-20260524-001");
   ```

   Unlike Python, the .NET wrapped agent does **not** require the start executor to accept `list[ChatMessage]` — any input type that matches the start executor's signature works (a `string` order id is fine in the LAB 04 sample).

6. **Durable checkpoints use `FileSystemJsonCheckpointStore` + `CheckpointManager.CreateJson`.** `CheckpointManager.Default` is in-memory and is gone the moment the process exits — useless for a real HITL workflow where the human approver might come back the next day. For LAB 04, write to a directory and pass the store into `CheckpointManager.CreateJson` (the `customOptions` argument can be `null`):

   ```csharp
   using Microsoft.Agents.AI.Workflows.Checkpointing;

   var store = new FileSystemJsonCheckpointStore(new DirectoryInfo("./_checkpoints"));
   CheckpointManager manager = CheckpointManager.CreateJson(store, customOptions: null);
   ```

   Each super-step writes a JSON file plus a line to `index.jsonl` in the directory; `ResumeStreamingAsync(workflow, checkpoint, manager, ct)` rehydrates from any of them. The package names are `Microsoft.Agents.AI.Workflows.Checkpointing.FileSystemJsonCheckpointStore` and `Microsoft.Agents.AI.Workflows.CheckpointManager` — there is no `FileCheckpointStorage` type in .NET (that name is the Python API).

7. **Conditional edge predicates over nullable types need null-handling inside the lambda.** `AddEdge<TMsg?>(source, target, condition: ...)` lets the sentinel through as `null`; you must guard inside the predicate so the compiler doesn't warn on member access:

   ```csharp
   builder.AddEdge<AllocationPlan?>(
       allocator, approvalGate,
       condition: msg => msg is AllocationPlan plan && plan.TotalUsd >= HitlThresholdUsd);
   builder.AddEdge<AllocationPlan?>(
       allocator, dispatch,
       condition: msg => msg is AllocationPlan plan && plan.TotalUsd <  HitlThresholdUsd);
   ```

   The `msg is AllocationPlan plan` pattern both filters out the buffer sentinel from gotcha #1 *and* gives you a non-null reference for the threshold check, silencing CS8602 without sprinkling `!` operators.

8. **`NU1604` / `NU1902` / `MAAI001` will fire** on a fresh project. The wildcard package version `Version="*-*"` triggers `NU1604` ("missing lower bound"); the prerelease agent SDK ships with known-vuln transitive deps that trigger `NU1902`; and `AgentSkillsProvider` / `AgentInlineSkill` are `[Experimental("MAAI001")]`. Add `<NoWarn>$(NoWarn);NU1604;NU1902;MAAI001</NoWarn>` to the `.csproj` so the build stays clean and the wildcards still pull the latest prerelease.

Bonus shape rules surfaced by the same LAB:

- The `Executor<TIn>` (no `TOut`) override is `public override ValueTask HandleAsync(TIn, IWorkflowContext, CancellationToken)` — note the non-generic `ValueTask`. Use it whenever your handler talks to the framework via `SendMessageAsync` / `YieldOutputAsync` instead of returning a value.
- `WorkflowBuilder` exposes fluent `WithName(string)` / `WithDescription(string)` / `WithOutputFrom(params Executor[])` chained before `.Build()`. The name and description show up on the wrapped agent from gotcha #5 and on diagnostic traces.
- `SuperStepCompletedEvent.CompletionInfo.Checkpoint` is the right tuple for collecting checkpoints — `evt.Checkpoint` and `evt.Data` do **not** exist on this event.
- `ExecutorFailedEvent.Data` and `WorkflowErrorEvent.Data` are both `Exception` instances. Cast and rethrow if you want fail-fast semantics in the consumer.
