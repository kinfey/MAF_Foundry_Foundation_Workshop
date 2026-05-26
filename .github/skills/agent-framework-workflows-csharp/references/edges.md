# Edges Reference (.NET Workflows)

Edges define how messages travel through the workflow graph. The `WorkflowBuilder` exposes a fluent API for direct edges, conditional edges, fan-out, fan-in, and switch-case.

## `WorkflowBuilder` Cheat Sheet

```csharp
Workflow workflow = new WorkflowBuilder(startExecutor)
    .AddEdge(a, b)                                          // direct
    .AddEdge(a, c, condition: result => result is Foo)      // conditional
    .AddFanOutEdge(start, [worker1, worker2, worker3])      // parallel
    .AddFanInBarrierEdge([worker1, worker2, worker3], join) // wait-for-all
    .WithOutputFrom(join)                                   // declare outputs
    .Build();
```

| Builder method | Purpose |
|----------------|---------|
| `new WorkflowBuilder(start)` | Construct with the entry executor that receives the workflow input. |
| `.AddEdge(from, to)` | Direct edge — every output of `from` flows into `to`. |
| `.AddEdge(from, to, condition: Func<object?, bool>)` | Conditional edge — message is delivered to `to` only when the predicate returns `true`. |
| `.AddFanOutEdge(from, [a, b, c])` | One-to-many: each output is delivered to every target in parallel. |
| `.AddFanInBarrierEdge([a, b, c], to)` | Many-to-one barrier: `to` receives a `List<TOut>` once **all** sources finished the current super-step. |
| `.WithOutputFrom(executor1, executor2, ...)` | Declare which executors are allowed to yield `WorkflowOutputEvent`s. |
| `.Build()` | Validate the graph and produce the immutable `Workflow`. |

## Direct Edges

The simplest edge. Each value returned by `from.HandleAsync` becomes the input to `to.HandleAsync`.

```csharp
var workflow = new WorkflowBuilder(uppercase)
    .AddEdge(uppercase, reverse)
    .WithOutputFrom(reverse)
    .Build();
```

Edge type compatibility is checked at `Build()` time: if `uppercase` returns `string` but `reverse` expects `int`, the graph will fail to build with a descriptive exception.

## Conditional Edges

Use `condition:` to branch the graph. The predicate sees the **upstream output** as `object?` — cast it inside the lambda.

```csharp
// From ConditionalEdges/01_EdgeCondition — spam routing.
static Func<object?, bool> IsSpam(bool expected) =>
    result => result is DetectionResult d && d.IsSpam == expected;

Workflow workflow = new WorkflowBuilder(spamDetectionExecutor)
    .AddEdge(spamDetectionExecutor, emailAssistantExecutor, condition: IsSpam(false))
    .AddEdge(emailAssistantExecutor, sendEmailExecutor)
    .AddEdge(spamDetectionExecutor, handleSpamExecutor,  condition: IsSpam(true))
    .WithOutputFrom(handleSpamExecutor, sendEmailExecutor)
    .Build();
```

Guidelines
- **Make conditions mutually exclusive** when only one branch should fire. Overlapping predicates deliver the message to every matching edge.
- **Keep predicates pure and fast.** They run synchronously on the dispatch thread.
- **For >2 branches**, prefer the switch-case helper below or refactor decisions into an upstream classifier executor.

## Switch-Case Edges

When you need an N-way decision tree, model it as a chain of conditional edges from a single upstream executor:

```csharp
static Func<object?, bool> WhenStatus(string s) =>
    r => r is OrderStatus os && os.Status == s;

var workflow = new WorkflowBuilder(classifier)
    .AddEdge(classifier, paidHandler,    condition: WhenStatus("paid"))
    .AddEdge(classifier, pendingHandler, condition: WhenStatus("pending"))
    .AddEdge(classifier, refundHandler,  condition: WhenStatus("refund"))
    .AddEdge(classifier, fallback,       condition: r => true) // default arm
    .WithOutputFrom(paidHandler, pendingHandler, refundHandler, fallback)
    .Build();
```

The "default" arm uses `r => true` and runs only when ordered last and the others didn't match. If you need a strict default (run only when no specific case matched), implement the routing inside a classifier executor that emits typed messages and use `[SendsMessage]` on it.

## Fan-Out / Fan-In

Parallel execution with a join barrier. The fan-out edge delivers the same message to every target; the fan-in barrier waits until all sources complete their current super-step, then delivers a `List<TOut>` to the join executor.

```csharp
// From Concurrent/Concurrent — two specialists answer the same question.
ChatClientAgent physicist = new(chatClient, name: "Physicist", instructions: "...");
ChatClientAgent chemist   = new(chatClient, name: "Chemist",   instructions: "...");

Executor physicistExec = physicist.BindAsExecutor(new AIAgentHostOptions { ForwardIncomingMessages = false });
Executor chemistExec   = chemist.BindAsExecutor(new AIAgentHostOptions { ForwardIncomingMessages = false });

var start = new ConcurrentStartExecutor();
var aggregate = new ConcurrentAggregationExecutor();

var workflow = new WorkflowBuilder(start)
    .AddFanOutEdge(start, [physicistExec, chemistExec])
    .AddFanInBarrierEdge([physicistExec, chemistExec], aggregate)
    .WithOutputFrom(aggregate)
    .Build();
```

### Partitioned map/reduce with one executor per partition

For deterministic workflows that should run the same logic once per partition, create one executor instance per partition with a stable unique `ExecutorId`, then fan out to all instances and fan in to one aggregator. This keeps event streams attributable per partition and avoids relying on hidden mutable global partition state.

```csharp
internal sealed class DemandForecasterExecutor : Executor<string, DemandForecast>
{
    private readonly string _sku;

    public DemandForecasterExecutor(string sku)
        : base($"DemandForecaster_{sku.Replace('-', '_')}")
    {
        _sku = sku;
    }

    public override ValueTask<DemandForecast> HandleAsync(
        string weekIso, IWorkflowContext context, CancellationToken cancellationToken = default)
    {
        int predicted = Math.Abs(_sku.GetHashCode(StringComparison.Ordinal)) % 100 + 50;
        return ValueTask.FromResult(new DemandForecast(_sku, predicted, weekIso));
    }
}

[YieldsOutput(typeof(InventorySummary))]
internal sealed class SupplyChainAggregatorExecutor() : Executor<DemandForecast>("SupplyChainAggregator")
{
    private readonly List<DemandForecast> _forecasts = [];
    private static int s_poCounter;

    public override ValueTask HandleAsync(
        DemandForecast forecast, IWorkflowContext context, CancellationToken cancellationToken = default)
    {
        _forecasts.Add(forecast);
        return ValueTask.CompletedTask;
    }

    protected override async ValueTask OnMessageDeliveryFinishedAsync(
        IWorkflowContext context, CancellationToken cancellationToken = default)
    {
        InventorySummary summary = BuildSummary(_forecasts);
        _forecasts.Clear();
        await context.YieldOutputAsync(summary, cancellationToken);
    }

    private static InventorySummary BuildSummary(IReadOnlyList<DemandForecast> forecasts)
    {
        List<SkuUpdate> updates = [];
        foreach (DemandForecast forecast in forecasts)
        {
            string poId = $"PO-X{Interlocked.Increment(ref s_poCounter) + 1233}";
            updates.Add(new SkuUpdate(forecast.Sku, forecast.PredictedUnitsNextWeek, poId, $"LX-{poId[3..]}"));
        }
        return new InventorySummary(updates, updates.Sum(u => u.Units));
    }
}

DemandForecasterExecutor[] forecasters = skus.Select(sku => new DemandForecasterExecutor(sku)).ToArray();
SupplyChainAggregatorExecutor aggregator = new();

Workflow workflow = new WorkflowBuilder(trigger)
    .AddFanOutEdge(trigger, [.. forecasters.Cast<Executor>()])
    .AddFanInBarrierEdge([.. forecasters.Cast<Executor>()], aggregator)
    .WithOutputFrom(aggregator)
    .Build();
```

### Start executor for fan-out to agents

Agent executors don't process queued messages until a `TurnToken` arrives — the start executor broadcasts both:

```csharp
[SendsMessage(typeof(ChatMessage))]
[SendsMessage(typeof(TurnToken))]
internal sealed partial class ConcurrentStartExecutor() : Executor("ConcurrentStartExecutor")
{
    [MessageHandler]
    public async ValueTask HandleAsync(
        string message, IWorkflowContext context, CancellationToken cancellationToken = default)
    {
        // Broadcast to every connected agent.
        await context.SendMessageAsync(new ChatMessage(ChatRole.User, message), cancellationToken);
        // Then kick them off.
        await context.SendMessageAsync(new TurnToken(emitEvents: false), cancellationToken);
    }
}
```

### Aggregation executor for fan-in

The aggregator's input type is `List<TOut>` where `TOut` is the agents' output type. Accumulate inside `HandleAsync` and emit final output in `OnMessageDeliveryFinishedAsync`:

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
        StringBuilder builder = new();
        foreach (ChatMessage m in _messages)
        {
            builder.AppendLine($"{m.AuthorName}: {m.Text}");
        }
        _messages.Clear();
        return context.YieldOutputAsync(builder.ToString(), cancellationToken);
    }
}
```

## Combining Edges

A single executor can be source/target of any mix of direct, conditional, and fan-out edges. Order of edge declarations does **not** affect dispatch order — every matching edge fires per message.

```csharp
var workflow = new WorkflowBuilder(classify)
    // Sequential happy path
    .AddEdge(classify, validate, condition: r => r is Order o && o.IsValid)
    .AddEdge(validate, charge)
    .AddEdge(charge, fulfill)
    // Error branches
    .AddEdge(classify, reject, condition: r => r is Order o && !o.IsValid)
    .AddEdge(charge, notifyFailure, condition: r => r is ChargeResult c && !c.Success)
    // Parallel notifications on success
    .AddFanOutEdge(fulfill, [notifyCustomer, updateInventory, recordAnalytics])
    .WithOutputFrom(fulfill, reject, notifyFailure)
    .Build();
```

## Graph Validation

`Build()` performs static validation and throws on:

- Unreachable executors (no incoming edge and not the start node).
- Type mismatches between an edge's source output and target input.
- An executor that calls `YieldOutputAsync` but isn't named in `WithOutputFrom` (or marked `[YieldsOutput]`).
- Duplicate `ExecutorId`s in the same graph.
- An executor that uses `SendMessageAsync<T>` without a `[SendsMessage(typeof(T))]` declaration.

Let those exceptions surface during development. They tell you exactly which edge or executor is misconfigured.

## Visualization

`Microsoft.Agents.AI.Workflows` includes a graph exporter — see the [`Visualization`](https://github.com/microsoft/agent-framework/tree/main/dotnet/samples/03-workflows/Visualization) sample for emitting Mermaid or DOT from a built workflow. Useful for documenting complex graphs and validating fan-out / fan-in topology before running.
