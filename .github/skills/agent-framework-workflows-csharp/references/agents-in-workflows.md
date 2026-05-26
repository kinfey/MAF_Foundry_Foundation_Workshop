# Agents in Workflows Reference (.NET)

How to combine `AIAgent` instances with the workflows engine — both as raw graph nodes via `BindAsExecutor`, and via the high-level `AgentWorkflowBuilder` patterns (sequential, concurrent, handoff, group chat, Magentic).

## Two Ways to Compose Agents

| Approach | When to use |
|----------|-------------|
| **Raw graph** — drop `AIAgent` directly into `WorkflowBuilder` (with `AddEdge`, `AddFanOutEdge`, etc.) and call `BindAsExecutor` when you need to tune host options. | You need custom routing, non-agent executors in the same graph, shared state, conditional edges, or a topology that doesn't match a built-in pattern. |
| **`AgentWorkflowBuilder` patterns** — `BuildSequential`, `BuildConcurrent`, `CreateHandoffBuilderWith`, `CreateGroupChatBuilderWith`, Magentic. | You're composing 2+ agents in one of the canonical patterns and don't need custom executors. |

## `ChatClientAgent` Constructors

Pick the constructor that fits the level of configuration you need.

```csharp
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

// Minimal — instructions only.
ChatClientAgent simple = new(chatClient, "You are a translation assistant for French.");

// Named — gives the agent an identity in events and group chat.
ChatClientAgent named = new(
    chatClient,
    name: "Physicist",
    instructions: "Answer briefly from a physics perspective.",
    description: "Expert in physics");

// Full options — structured outputs, tools, response format, etc.
ChatClientAgent rich = new(chatClient, new ChatClientAgentOptions
{
    ChatOptions = new()
    {
        Instructions = "You are a spam detection assistant.",
        ResponseFormat = ChatResponseFormat.ForJsonSchema<DetectionResult>(),
    }
});
```

The `name` is what shows up on `AgentResponseUpdateEvent.ExecutorId` when the agent runs as part of a workflow.

## Agents as Executors (Raw Graph)

When you add an `AIAgent` to `WorkflowBuilder`, it's automatically wrapped in an executor. Use `BindAsExecutor(...)` explicitly when you want to override the default host options.

```csharp
// Default: the agent forwards the incoming user message downstream alongside its reply.
Executor defaultBound = agent.BindAsExecutor();

// Don't echo the user prompt past this node — useful for fan-out / fan-in.
Executor isolated = agent.BindAsExecutor(new AIAgentHostOptions
{
    ForwardIncomingMessages = false,
});
```

`AIAgentHostOptions`
- `ForwardIncomingMessages` (default `true`) — whether the original input message also flows along the outgoing edges. Set to `false` when only the agent's reply should propagate.

### `TurnToken` semantics

Agent-bound executors **queue** every incoming message; they don't actually call the model until they receive a `TurnToken`. This lets you broadcast a question to several agents and have them all wait on the starting line.

```csharp
await using StreamingRun run = await InProcessExecution.RunStreamingAsync(workflow,
    new ChatMessage(ChatRole.User, "Hello World!"));

// Kick off every queued agent in the graph.
await run.TrySendMessageAsync(new TurnToken(emitEvents: true));
```

`TurnToken(emitEvents: bool)`
- `true` → the agent emits `AgentResponseUpdateEvent`s during the run (streaming).
- `false` → the agent runs silently and you only observe the final `ExecutorCompletedEvent`.

If you mix agent executors with raw executors and forget to send a `TurnToken`, the agents will never run and the workflow will appear stuck after the initial inputs propagate.

### Sequential chain of agents

```csharp
static ChatClientAgent Translator(string lang, IChatClient client) =>
    new(client, $"You are a translation assistant that translates the provided text to {lang}.");

AIAgent french  = Translator("French",  chatClient);
AIAgent spanish = Translator("Spanish", chatClient);
AIAgent english = Translator("English", chatClient);

Workflow workflow = new WorkflowBuilder(french)
    .AddEdge(french,  spanish)
    .AddEdge(spanish, english)
    .Build();

await using StreamingRun run = await InProcessExecution.RunStreamingAsync(
    workflow, new ChatMessage(ChatRole.User, "Hello World!"));
await run.TrySendMessageAsync(new TurnToken(emitEvents: true));

await foreach (WorkflowEvent evt in run.WatchStreamAsync())
{
    if (evt is AgentResponseUpdateEvent upd)
    {
        Console.WriteLine($"{upd.ExecutorId}: {upd.Update.Text}");
    }
}
```

### Observing agent output

| Event | Useful members |
|-------|---------------|
| `AgentResponseUpdateEvent` | `ExecutorId`, `Update.Text`, `Update.Contents` (cast to `FunctionCallContent`, `TextContent`, etc.). |
| `ExecutorCompletedEvent` | `ExecutorId`, `Data` — the final value the agent emitted. |

Function-call streaming:

```csharp
if (evt is AgentResponseUpdateEvent upd)
{
    foreach (var fc in upd.Update.Contents.OfType<FunctionCallContent>())
    {
        Console.WriteLine($"Function call → {fc.Name}({fc.Arguments})");
    }
}
```

## `AgentWorkflowBuilder` Patterns

The pre-built patterns handle the `TurnToken` plumbing and aggregation automatically.

### `BuildSequential`

Each agent's reply is fed to the next agent as its input.

```csharp
Workflow seq = AgentWorkflowBuilder.BuildSequential(new[] { french, spanish, english });

await using StreamingRun run = await InProcessExecution.RunStreamingAsync(
    seq, new ChatMessage(ChatRole.User, "Hello World!"));
await run.TrySendMessageAsync(new TurnToken(emitEvents: true));
```

### `BuildConcurrent`

Every agent receives the same input; the builder aggregates the replies and yields a combined output.

```csharp
Workflow conc = AgentWorkflowBuilder.BuildConcurrent(new[] { french, spanish, english });
```

### Handoff

A triage agent routes the user message to a specialist, and specialists can hand control back. Define each handoff direction explicitly.

```csharp
ChatClientAgent triage = new(chatClient,
    name: "Triage",
    instructions: "Route the user to the right tutor agent.");

ChatClientAgent math = new(chatClient,
    name: "MathTutor",
    instructions: "Answer math questions in detail.");

ChatClientAgent history = new(chatClient,
    name: "HistoryTutor",
    instructions: "Answer history questions in detail.");

Workflow handoff = AgentWorkflowBuilder
    .CreateHandoffBuilderWith(triage)
    .WithHandoffs(triage,          [math, history])
    .WithHandoffs([math, history],  triage)
    .Build();
```

The triage agent's instructions should describe each specialist (name + when to delegate) so it can call the framework-generated handoff tools.

### Group Chat

Multiple agents take turns under a manager's control. The framework ships managers like `RoundRobinGroupChatManager`.

```csharp
Workflow group = AgentWorkflowBuilder
    .CreateGroupChatBuilderWith(agents => new RoundRobinGroupChatManager(agents)
    {
        MaximumIterationCount = 5,
    })
    .AddParticipants(new[] { french, spanish, english })
    .WithName("Translation Round Robin Workflow")
    .WithDescription("Three translators answer round-robin until iteration cap.")
    .Build();
```

`MaximumIterationCount` is the kill switch — without it, a group chat will keep going until the manager's natural stop condition triggers.

### Magentic Orchestration

For dynamic plan-then-execute multi-agent flows, use the Magentic builder (see [`Orchestration/Magentic`](https://github.com/microsoft/agent-framework/tree/main/dotnet/samples/03-workflows/Orchestration/Magentic)). The Magentic manager plans subtasks, dispatches them across the registered specialists, and stitches results back into a final answer.

```csharp
Workflow magentic = AgentWorkflowBuilder
    .CreateMagenticBuilderWith(managerAgent)
    .AddParticipants(new[] { researcher, coder, reviewer })
    .Build();
```

The manager agent is typically given instructions that describe the team and how to delegate. Use Magentic when subtasks aren't known upfront and the manager needs to plan dynamically.

## Choosing Between Raw and Built-in Patterns

| You want… | Use |
|-----------|-----|
| Pure agent-to-agent pipeline, fixed order | `AgentWorkflowBuilder.BuildSequential` |
| Several agents answer the same question, results aggregated | `AgentWorkflowBuilder.BuildConcurrent` |
| Triage / specialist routing with bidirectional control | `AgentWorkflowBuilder.CreateHandoffBuilderWith` |
| Bounded multi-agent discussion | `AgentWorkflowBuilder.CreateGroupChatBuilderWith` |
| Dynamic decomposition into subtasks | Magentic builder |
| Mixed agent + custom executor pipeline, conditional routing, shared state | Raw `WorkflowBuilder` + `BindAsExecutor` |
| Human-in-the-loop request port | Raw `WorkflowBuilder` (see [checkpoints-and-hitl.md](checkpoints-and-hitl.md)) |
| Checkpoint / resume | Raw `WorkflowBuilder` + `CheckpointManager.Default` |

## Naming and Identity

- Agent `name` becomes the `ExecutorId` on events. Make it unique within one workflow.
- `description` is consumed by handoff/Magentic managers to decide when to delegate — write it as if it were a tool description.
- The default `ExecutorId` for raw executors is the class name. Pass an explicit name to the base constructor (`Executor<TIn, TOut>("CustomId")`) when you instantiate the same class multiple times in one graph.
