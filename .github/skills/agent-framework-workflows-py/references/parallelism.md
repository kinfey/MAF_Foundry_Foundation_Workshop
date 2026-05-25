# Parallelism — Fan-Out / Fan-In, Aggregation, Map-Reduce

## Fan-out / fan-in

Use `add_fan_out_edges(src, [t1, t2, ...])` to dispatch the same message in parallel and `add_fan_in_edges([s1, s2, ...], dst)` to collect a `list[T]` from the sources into one handler. Both helpers model true many-node groups: fan-out requires at least two targets, and fan-in requires at least two sources.

```python
from agent_framework import (
    Agent, AgentExecutor, AgentExecutorRequest, AgentExecutorResponse,
    Executor, Message, WorkflowBuilder, WorkflowContext, handler,
)
from typing_extensions import Never


class Dispatch(Executor):
    @handler
    async def dispatch(self, prompt: str, ctx: WorkflowContext[AgentExecutorRequest]) -> None:
        await ctx.send_message(AgentExecutorRequest(
            messages=[Message("user", contents=[prompt])],
            should_respond=True,
        ))


class Aggregate(Executor):
    @handler
    async def aggregate(self, results: list[AgentExecutorResponse], ctx: WorkflowContext[Never, str]) -> None:
        by_id = {r.executor_id: r.agent_response.text for r in results}
        consolidated = (
            "Consolidated Insights\n=====================\n\n"
            f"Research:\n{by_id.get('researcher','')}\n\n"
            f"Marketing:\n{by_id.get('marketer','')}\n\n"
            f"Legal:\n{by_id.get('legal','')}\n"
        )
        await ctx.yield_output(consolidated)


workflow = (
    WorkflowBuilder(start_executor=Dispatch(id="dispatcher"))
    .add_fan_out_edges(Dispatch(id="dispatcher"), [researcher, marketer, legal])
    .add_fan_in_edges([researcher, marketer, legal], Aggregate(id="aggregator"))
    .build()
)
```

The fan-in handler's input type must be a `list[T]` (or `list[Union[...]]`) matching what the upstream executors emit.

## Single-target map and manual batching

When one executor emits many messages to one downstream executor, use a direct edge. Do **not** call `add_fan_out_edges(src, [dst])`; that is a single-target edge and will fail validation. If the downstream stage must wait for all messages, buffer them in a stateful executor and forward one batch when complete.

```python
class SupplierSelector(Executor):
    def __init__(self, expected_count: int, id: str = "supplier_selector") -> None:
        super().__init__(id=id)
        self._expected_count = expected_count
        self._forecasts: list[dict] = []

    @handler
    async def collect(self, forecast: dict, ctx: WorkflowContext[list[dict]]) -> None:
        self._forecasts.append(forecast)
        if len(self._forecasts) == self._expected_count:
            batch = self._forecasts
            self._forecasts = []
            await ctx.send_message(batch)


workflow = (
    WorkflowBuilder(start_executor=weekly_trigger)
    .add_edge(weekly_trigger, demand_forecaster)   # weekly_trigger sends one message per SKU
    .add_edge(demand_forecaster, SupplierSelector(expected_count=5))
    .build()
)
```

## Aggregating heterogeneous types

Annotate the fan-in input as `list[int | float | str | ...]` to receive different upstream types in the same call:

```python
class Aggregator(Executor):
    @handler
    async def handle(self, results: list[int | float], ctx: WorkflowContext[Never, list[int | float]]):
        await ctx.yield_output(results)

workflow = (
    WorkflowBuilder(start_executor=dispatcher)
    .add_fan_out_edges(dispatcher, [average_executor, sum_executor])
    .add_fan_in_edges([average_executor, sum_executor], aggregator)
    .build()
)
```

## Live streaming across concurrent branches

While branches run, render per-branch output by binding to `event.executor_id`:

```python
buffers = {"researcher": "", "marketer": "", "legal": ""}
completed: set[str] = set()

async for event in workflow.run(prompt, stream=True):
    if event.type == "executor_completed" and event.executor_id in buffers:
        completed.add(event.executor_id)
        render(buffers, completed)
    elif event.type == "output" and isinstance(event.data, AgentResponseUpdate):
        eid = event.executor_id or ""
        if eid in buffers:
            buffers[eid] += event.data.text
            render(buffers, completed)
```

## Map-reduce with file-backed intermediates

For large inputs, partition once in `Split`, write intermediate files in `Map`, group / shuffle in `Shuffle`, sum per partition in `Reduce`, and finalize in `Completion`:

```python
workflow = (
    WorkflowBuilder(start_executor=split)
    .add_fan_out_edges(split, mappers)            # Split  → N mappers
    .add_fan_in_edges(mappers, shuffle)           # mappers → shuffle
    .add_fan_out_edges(shuffle, reducers)         # Shuffle → M reducers
    .add_fan_in_edges(reducers, completion)       # reducers → completion
    .build()
)
```

Pass file paths between stages (not raw payloads) to keep memory bounded. Each mapper/reducer reads its slice from `ctx.get_state(self.id)`.

## Visualization

Install with the `[viz]` extra and a GraphViz binary, then export the topology:

```python
from agent_framework import WorkflowViz

viz = WorkflowViz(workflow)
print(viz.to_mermaid())
print(viz.to_digraph())
svg_path = viz.export(format="svg")               # raises ImportError without the [viz] extra
```

## When to use what

| Need | Pattern |
| --- | --- |
| Same input → many workers, single join | `add_fan_out_edges` + `add_fan_in_edges` |
| Same input → many workers, no join (independent terminals) | `add_fan_out_edges` only |
| One source emits many messages → one worker → one batched next stage | Direct `add_edge` plus a stateful buffering executor |
| Different inputs from one source per branch | One `add_edge` per target, optionally with `target_id` in `ctx.send_message` |
| Heterogeneous result types into one handler | `list[int | float | ...]` annotation on the fan-in handler |
| Functional API (plain async) | `await asyncio.gather(branch1(), branch2(), ...)` inside `@workflow` |
| Big payloads | Store in `ctx.set_state`, pass IDs; or write to disk and pass file paths |
