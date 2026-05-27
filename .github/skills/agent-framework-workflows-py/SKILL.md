---
name: agent-framework-workflows-py
description: Build deterministic multi-step, multi-agent workflows in Python with the Microsoft Agent Framework (`agent-framework`). Use when composing `Executor` nodes and edges, embedding `Agent` instances as workflow nodes, streaming workflow events, fan-out/fan-in, conditional routing, loops, checkpointing and resume, human-in-the-loop with `ctx.request_info()`, sub-workflows with `WorkflowExecutor`, wrapping a workflow as an agent with `workflow.as_agent()`, or using the high-level orchestration builders (`SequentialBuilder`, `ConcurrentBuilder`, `HandoffBuilder`, `GroupChatBuilder`, `MagenticBuilder`).
license: MIT
metadata:
  author: Microsoft
  version: "1.0.0"
  package: agent-framework
---

# Microsoft Agent Framework — Workflows (Python)

Build deterministic, durable, observable multi-agent / multi-step pipelines on top of the Microsoft Agent Framework Python SDK. Workflows are a graph of `Executor` nodes connected by typed edges; agents are first-class executors; events stream as the graph runs; state can be checkpointed and replayed.

## Architecture

```
        ┌──────────────────────────────────────────────────────────────┐
        │                       WorkflowBuilder                        │
        │  start_executor=A, output_from=[...], intermediate_output…   │
        └─────────────┬────────────────────────────────────────────────┘
                      │ .build()
                      ▼
               ┌────────────┐
               │  Workflow  │   ──►  .run(input)  /  .run(stream=True)
               └─────┬──────┘                       /  .run(checkpoint_id=…)
                     │                              /  .run(responses={…})
                     ▼
        ┌────────────────────────────┐
        │  Executor graph (DAG)      │
        │   • Executor / @executor   │
        │   • Agent / AgentExecutor  │   events: executor_invoked,
        │   • WorkflowExecutor (sub) │           executor_completed,
        │   • Orchestration builders │           output, intermediate,
        └────────┬───────────────────┘           request_info, status,
                 │                                superstep_completed
                 ▼
        ┌────────────────────────────┐
        │  WorkflowContext (per run) │   ctx.send_message / ctx.yield_output
        │  state, requests, events   │   ctx.request_info / ctx.set_state
        └────────────────────────────┘
```

Two equivalent authoring styles:

| Style | When to use | Entry points |
| --- | --- | --- |
| **Graph (declarative)** | Branching, loops, fan-out/fan-in, orchestration patterns, sub-workflows, checkpointing | `Executor`, `@executor`, `@handler`, `WorkflowBuilder` |
| **Functional** | Linear or simply branched pipelines expressed as plain async Python | `@workflow`, `@step`, `ctx.request_info` |

## Installation

```bash
# Core workflows ship inside agent-framework / agent-framework-core
pip install agent-framework --pre

# Optional: graph visualization (WorkflowViz.export → SVG)
pip install "agent-framework[viz]" --pre
# Also install GraphViz binary: https://graphviz.org/download/

# Optional: orchestration builders ship inside the core package; or pin directly
pip install agent-framework-orchestrations --pre
```

Orchestration builders are imported from the `agent_framework.orchestrations` submodule:

```python
from agent_framework.orchestrations import (
    SequentialBuilder,
    ConcurrentBuilder,
    HandoffBuilder,
    GroupChatBuilder,
    MagenticBuilder,
)
```

## Environment Variables

Workflow samples that embed an `Agent` use `FoundryChatClient` (lightweight, project-backed; no server-side agent lifecycle):

| Variable | Purpose |
| --- | --- |
| `FOUNDRY_PROJECT_ENDPOINT` | Azure AI Foundry Agent Service (V2) project endpoint |
| `FOUNDRY_MODEL` | Model deployment name in your Foundry project |

Authenticate via `azure.identity.AzureCliCredential()` (run `az login` first), or substitute any `SupportsChatGetResponse` chat client (OpenAI, Azure OpenAI, etc.).

## Core Workflow

### 1. Executors and edges

An `Executor` is a node. Inputs and outputs are typed. Handlers receive a `WorkflowContext[T_Out, T_W_Out]`:

- `ctx.send_message(value)` — forward to downstream nodes
- `ctx.yield_output(value)` — produce a workflow output (terminal yield, but workflows complete when **idle**, not on first yield)
- `ctx.set_state(key, value)` / `ctx.get_state(key)` — per-run scratchpad
- `ctx.request_info(...)` — pause for human / external input

Two equivalent ways to declare a node:

```python
from agent_framework import Executor, WorkflowBuilder, WorkflowContext, executor, handler
from typing_extensions import Never


class UpperCase(Executor):
    @handler
    async def to_upper(self, text: str, ctx: WorkflowContext[str]) -> None:
        await ctx.send_message(text.upper())


@executor(id="reverse_text")
async def reverse_text(text: str, ctx: WorkflowContext[Never, str]) -> None:
    await ctx.yield_output(text[::-1])


def create_workflow():
    upper = UpperCase(id="upper")
    return (
        WorkflowBuilder(start_executor=upper)
        .add_edge(upper, reverse_text)
        .build()
    )
```

Run it:

```python
workflow = create_workflow()
events = await workflow.run("hello world")
print(events.get_outputs())           # ['DLROW OLLEH']
print(events.get_final_state())       # WorkflowRunState.IDLE
```

> **State isolation**: wrap construction in a helper (`create_workflow()`) and call it per run so executor instance state never leaks between runs.

### 2. Agents as workflow nodes

`Agent` objects (and `AgentExecutor`-wrapped agents) plug directly into the graph. Inputs are normally `AgentExecutorRequest`; outputs are `AgentExecutorResponse`. When the start node is an `Agent`, the framework converts `run(str)` / `run(Message(...))` to a request automatically.

```python
from agent_framework import Agent, WorkflowBuilder
from agent_framework.foundry import FoundryChatClient
from azure.identity import AzureCliCredential

client = FoundryChatClient(
    project_endpoint=os.environ["FOUNDRY_PROJECT_ENDPOINT"],
    model=os.environ["FOUNDRY_MODEL"],
    credential=AzureCliCredential(),
)
writer = Agent(client=client, instructions="You write copy.", name="writer")
reviewer = Agent(client=client, instructions="You review copy.", name="reviewer")

workflow = WorkflowBuilder(start_executor=writer).add_edge(writer, reviewer).build()
events = await workflow.run("Slogan for an affordable electric SUV.")
```

### 3. Streaming events

`workflow.run(..., stream=True)` returns an async iterator of `WorkflowEvent`:

| `event.type` | Meaning |
| --- | --- |
| `executor_invoked` / `executor_completed` | Lifecycle per node — observable without code changes |
| `output` | A `ctx.yield_output(...)` value (or agent streaming token via `AgentResponseUpdate`) |
| `intermediate` | A yield from an executor selected by `intermediate_output_from` |
| `request_info` | A `ctx.request_info(...)` call — workflow is paused |
| `status` | `WorkflowRunState.IDLE`, `IDLE_WITH_PENDING_REQUESTS`, etc. |
| `superstep_completed` | A graph "super-step" finished (safe checkpoint boundary) |

```python
async for event in workflow.run(initial, stream=True):
    if event.type == "output":
        print("OUT:", event.data)
    elif event.type == "intermediate":
        print("…  ", event.executor_id, event.data)
```

## Output selection (`output_from` / `intermediate_output_from`)

`WorkflowBuilder` exposes which executor yields are surfaced. `output_from` is an **allow-list** for Workflow Output, not a routing rule; unselected yields are hidden unless `intermediate_output_from` selects them.

| Selection | Workflow Output (`type='output'`) | Intermediate (`type='intermediate'`) | Hidden |
| --- | --- | --- | --- |
| Omit both | All `yield_output` (deprecation warning) | — | — |
| `output_from="all"` | Every output-capable executor | — | — |
| `output_from=[answerer]` | Only `answerer` | — | All others |
| `output_from=[answerer], intermediate_output_from="all_other"` | `answerer` | Every other output-capable executor | — |
| `intermediate_output_from="all_other"` | Builder-internal default executors only | Every output-capable executor | Builder plumbing |
| `output_from=[w], intermediate_output_from=[r, rv]` | `w` | `r`, `rv` | Any others |

Invalid: `output_from="all_other"`, `intermediate_output_from="all"`, the same executor in both lists, duplicates, unknown executors, or both lists empty. Deprecated alias: `output_executors` (use `output_from`).

When the workflow is exposed via `workflow.as_agent()`, Workflow Output becomes the `AgentResponse.text`; Intermediate Output becomes `text_reasoning` content on the response messages.

## Functional API (`@workflow`, `@step`)

Write workflows as plain async Python — no graph, no edges. Use normal `if`/`else`, loops, and `asyncio.gather` for branching and parallelism.

```python
from agent_framework import workflow, step

async def fetch(url: str) -> dict: ...
async def transform(d: dict) -> str: ...

@workflow
async def pipeline(url: str) -> str:
    raw = await fetch(url)
    summary = await transform(raw)
    return f"[OK] {summary}"

result = await pipeline.run("https://example.com")
print(result.get_outputs()[0])

# Streaming events
stream = pipeline.run("https://example.com", stream=True)
async for event in stream:
    if event.type == "output":
        print(event.data)
final = await stream.get_final_response()
```

Add `@step` to a function to cache its result across HITL resume or checkpoint restore — without `@step` every call re-executes from the top on resume. `@step` composes with `asyncio.gather` for cached parallel branches. To enable checkpointing, pass `checkpoint_storage` to `@workflow`:

```python
from agent_framework import InMemoryCheckpointStorage, step, workflow

storage = InMemoryCheckpointStorage()

@step
async def fetch_data(url: str) -> dict: ...

@workflow(checkpoint_storage=storage)
async def pipeline(url: str) -> str:
    raw = await fetch_data(url)
    ...
```

Functional workflows also support `as_agent()`, HITL via `ctx: RunContext` + `ctx.request_info()`, and structured `RunContext` access — all opt-in.

## Methods (graph API quick reference)

| Builder method | Purpose |
| --- | --- |
| `WorkflowBuilder(start_executor=, checkpoint_storage=, max_iterations=, output_from=, intermediate_output_from=)` | Create a builder |
| `.add_edge(src, dst, condition=None, target_id=None)` | Connect two executors; optional predicate over the upstream message |
| `.add_chain([a, b, c])` | Sugar for sequential edges |
| `.add_fan_out_edges(src, [t1, t2, ...])` | Same message to many targets in parallel; requires at least two targets |
| `.add_fan_in_edges([s1, s2, ...], dst)` | Collect a `list[T]` from many sources into one handler; requires at least two sources |
| `.add_switch_case_edge_group(src, [Case(condition=, target=), Default(target=)])` | One-of-N routing |
| `.add_multi_selection_edge_group(src, targets, selection_func=)` | Dynamic subset fan-out |
| `.build()` → `Workflow` | Materialize the graph |

| Workflow method | Purpose |
| --- | --- |
| `await workflow.run(message, stream=False)` | Run; returns `WorkflowRunResult` (or async iterator if `stream=True`) |
| `workflow.run(checkpoint_id=...)` | Resume from a saved checkpoint |
| `workflow.run(responses={request_id: value})` | Resume after `request_info` |
| `result.get_outputs()` | List of `type='output'` payloads |
| `result.get_final_state()` | `WorkflowRunState` enum |
| `result.get_request_info_events()` | Pending HITL requests |
| `workflow.as_agent("agent_id")` | Wrap workflow as an `Agent` |

| Context method | Purpose |
| --- | --- |
| `ctx.send_message(msg, target_id=None)` | Forward to downstream (optionally one specific target) |
| `ctx.yield_output(value)` | Emit a workflow output (subject to `output_from` selection) |
| `ctx.set_state(key, value)` / `ctx.get_state(key)` | Per-run state |
| `await ctx.request_info(request_data=..., response_type=..., request_id=None)` | Pause for human / external input |
| `@response_handler` | Method that receives the resumed response |

## Complete Example — Spam triage with conditional routing

```python
import asyncio
import os
from typing import Any
from pydantic import BaseModel
from typing_extensions import Never

from agent_framework import (
    Agent, AgentExecutor, AgentExecutorRequest, AgentExecutorResponse,
    Message, WorkflowBuilder, WorkflowContext, executor,
)
from agent_framework.foundry import FoundryChatClient
from azure.identity import AzureCliCredential


class Detection(BaseModel):
    is_spam: bool
    reason: str
    email_content: str


class EmailReply(BaseModel):
    response: str


def is_spam(expected: bool):
    def cond(msg: Any) -> bool:
        if not isinstance(msg, AgentExecutorResponse):
            return True
        try:
            return Detection.model_validate_json(msg.agent_response.text).is_spam == expected
        except Exception:
            return False
    return cond


@executor(id="to_reply_request")
async def to_reply_request(resp: AgentExecutorResponse, ctx: WorkflowContext[AgentExecutorRequest]) -> None:
    parsed = Detection.model_validate_json(resp.agent_response.text)
    await ctx.send_message(AgentExecutorRequest(
        messages=[Message("user", contents=[parsed.email_content])],
        should_respond=True,
    ))


@executor(id="send_reply")
async def send_reply(resp: AgentExecutorResponse, ctx: WorkflowContext[Never, str]) -> None:
    parsed = EmailReply.model_validate_json(resp.agent_response.text)
    await ctx.yield_output(f"Sent:\n{parsed.response}")


@executor(id="handle_spam")
async def handle_spam(resp: AgentExecutorResponse, ctx: WorkflowContext[Never, str]) -> None:
    parsed = Detection.model_validate_json(resp.agent_response.text)
    await ctx.yield_output(f"Spam: {parsed.reason}")


def make_agent(name: str, instructions: str, fmt: type[BaseModel]) -> Agent:
    return Agent(
        client=FoundryChatClient(
            project_endpoint=os.environ["FOUNDRY_PROJECT_ENDPOINT"],
            model=os.environ["FOUNDRY_MODEL"],
            credential=AzureCliCredential(),
        ),
        instructions=instructions,
        name=name,
        default_options={"response_format": fmt},
    )


async def main() -> None:
    detector = AgentExecutor(make_agent(
        "spam_detector",
        "Detect spam. Reply JSON {is_spam, reason, email_content}.",
        Detection,
    ))
    writer = AgentExecutor(make_agent(
        "email_writer",
        "Draft a professional reply. Reply JSON {response}.",
        EmailReply,
    ))

    workflow = (
        WorkflowBuilder(start_executor=detector)
        .add_edge(detector, to_reply_request, condition=is_spam(False))
        .add_edge(to_reply_request, writer)
        .add_edge(writer, send_reply)
        .add_edge(detector, handle_spam, condition=is_spam(True))
        .build()
    )

    request = AgentExecutorRequest(
        messages=[Message("user", contents=["Hi team, follow-up notes from today’s meeting…"])],
        should_respond=True,
    )
    result = await workflow.run(request)
    print(result.get_outputs()[0])


if __name__ == "__main__":
    asyncio.run(main())
```

## Orchestration patterns (quick reference)

| Pattern | Builder | What it does |
| --- | --- | --- |
| Sequential | `SequentialBuilder(participants=[...])` | Chain agents with a shared conversation context |
| Concurrent | `ConcurrentBuilder(participants=[...])` | Fan-out to all participants in parallel, default aggregator returns combined `Messages` |
| Handoff | `HandoffBuilder(participants=[...]).with_start_agent(...)` | Mesh routing; auto-injected handoff tools; `HandoffAgentUserRequest` for HITL |
| GroupChat | `GroupChatBuilder(participants=[...]).with_orchestrator(agent=)` | Manager (agent or function) picks the next speaker each round |
| Magentic | `MagenticBuilder(participants=[...], manager_agent=, max_round_count=)` | Multi-agent planner/executor with progress ledger, plan review, checkpointing |

Each builder exposes `.build()` → `Workflow`, plus `.as_agent()` to expose the whole orchestration as a single agent. All builders honor the same `output_from` / `intermediate_output_from` selection model.

`HandoffBuilder` requires every participant agent to set `require_per_service_call_history_persistence=True`, or `.build()` raises `ValueError`.

## Human-in-the-loop (`ctx.request_info`)

```python
from agent_framework import Executor, WorkflowContext, handler, response_handler
from dataclasses import dataclass

@dataclass
class HumanFeedback:
    prompt: str

class TurnManager(Executor):
    @handler
    async def ask(self, _: str, ctx: WorkflowContext) -> None:
        await ctx.request_info(
            request_data=HumanFeedback(prompt="Approve? (yes/no)"),
            response_type=str,
        )

    @response_handler
    async def on_feedback(self, original: HumanFeedback, reply: str, ctx: WorkflowContext[Never, str]) -> None:
        await ctx.yield_output(f"User said: {reply}")
```

```python
# Drive the loop:
stream = workflow.run("start", stream=True)
pending = await collect_request_info(stream)
while pending:
    answers = {req_id: input(req.prompt) for req_id, req in pending.items()}
    stream = workflow.run(stream=True, responses=answers)
    pending = await collect_request_info(stream)
```

For agent-level tool approvals, set `approval_mode="always_require"` on a `@tool`; the workflow then emits `request_info` events whose `data` is a `FunctionApprovalRequestContent` — reply with `data.to_function_approval_response(approved=True)`.

## Checkpointing

Plug a checkpoint storage into `WorkflowBuilder(checkpoint_storage=...)` (graph) or `@workflow(checkpoint_storage=...)` (functional). A checkpoint is written after each super-step.

| Storage | Use |
| --- | --- |
| `InMemoryCheckpointStorage()` | Tests, demos |
| `FileCheckpointStorage(storage_path=...)` | Local dev, multi-process recovery |
| `CosmosCheckpointStorage(...)` | Durable production checkpoints in Azure Cosmos DB NoSQL |

```python
storage = InMemoryCheckpointStorage()
workflow = (
    WorkflowBuilder(start_executor=start, checkpoint_storage=storage)
    .add_edge(start, worker).add_edge(worker, worker)  # self-loop
    .build()
)

# Resume from a checkpoint
latest = await storage.get_latest(workflow_name=workflow.name)
events = workflow.run(checkpoint_id=latest.checkpoint_id, stream=True)
```

Per-executor state survives a restart by overriding `on_checkpoint_save` / `on_checkpoint_restore`:

```python
from typing import Any
from typing_extensions import override

class Worker(Executor):
    def __init__(self, id: str) -> None:
        super().__init__(id=id)
        self._cache: dict[int, list[tuple[int, int]]] = {}

    @override
    async def on_checkpoint_save(self) -> dict[str, Any]:
        return {"cache": self._cache}

    @override
    async def on_checkpoint_restore(self, state: dict[str, Any]) -> None:
        self._cache = state.get("cache", {})
```

## Sub-workflows and `workflow.as_agent()`

Embed a built `Workflow` as a node via `WorkflowExecutor(child, id="child")`:

```python
from agent_framework import WorkflowExecutor

child = build_validation_workflow()
parent = (
    WorkflowBuilder(start_executor=orchestrator)
    .add_edge(orchestrator, WorkflowExecutor(child, id="validate"))
    .add_edge("validate", delivery)
    .build()
)
```

A child's `ctx.request_info(...)` bubbles up as a `SubWorkflowRequestMessage` to the parent — handle it in the parent and reply with `request.create_response(value)` to intercept (no propagation to the caller).

Expose any workflow as an agent:

```python
agent = workflow.as_agent("my_agent")
response = await agent.run("user prompt")
print(response.text)                   # Workflow Output
# Intermediate Output is preserved as text_reasoning content on response.messages
```

This unlocks the reflection pattern (Worker ↔ Reviewer loop wrapped as a single agent), and lets a workflow act as a sub-agent inside another workflow.

## Conventions

- **One executor = one responsibility.** Keep handlers small; transform → forward.
- **Carry typed payloads on edges**, not raw strings. Use `@dataclass` / Pydantic for structured handoffs.
- **Use `response_format`** on agents that drive routing — never parse free text.
- **Store large payloads in `ctx.set_state`** and pass small references (IDs) on edges.
- **Helper-build pattern**: wrap construction in `def create_workflow(): ...` so each run gets fresh executor instances.
- **Single-target map pattern**: if one executor emits many messages to one downstream executor, use `.add_edge(src, dst)` rather than `.add_fan_out_edges(src, [dst])`. If those messages must become one batch before the next stage, use a stateful `Executor` with `@handler` to buffer and forward once complete.
- **Pick explicit `output_from`** in new code; compatibility mode emits a deprecation warning.
- **`@step` only what's expensive.** Cheap functions can stay un-decorated and replay freely.
- **Treat participant names as stable identifiers** — especially for Magentic checkpoints.

## Best Practices

- Build the graph once, call `workflow.run(...)` many times; runs share workflow but not executor state when you use the helper-build pattern.
- Use `stream=True` for any long-running or interactive workflow so you can render progress and capture `request_info`.
- Combine `@workflow(checkpoint_storage=...)` + `@step` to get durable, replayable pipelines with minimal boilerplate.
- For HITL: emit a typed `request_data` dataclass so the caller has a contract, not a string blob.
- For sub-workflows that emit `request_info`, decide up front whether the parent intercepts (handle `SubWorkflowRequestMessage`) or the outer caller receives the request.
- For orchestration builders, pick `output_from` deliberately — Sequential keeps the last participant by default; Concurrent / GroupChat / Magentic keep their aggregator/manager outputs.
- For visualization, build with `[viz]` extra and call `WorkflowViz(workflow).to_mermaid()` / `.to_digraph()` / `.export(format="svg")`.
- Validate agent JSON with Pydantic — defensive parsing prevents dead routes.

## Reference Files

| File | Topic |
| --- | --- |
| [references/getting_started.md](references/getting_started.md) | Executors, edges, agents in a workflow, streaming |
| [references/control_flow.md](references/control_flow.md) | Conditions, switch-case, multi-selection, loops, intermediate vs terminal outputs |
| [references/parallelism.md](references/parallelism.md) | Fan-out/fan-in, mixed-type aggregation, map-reduce, visualization |
| [references/functional_api.md](references/functional_api.md) | `@workflow`, `@step`, streaming, HITL, agents in functional pipelines |
| [references/human_in_the_loop.md](references/human_in_the_loop.md) | `ctx.request_info`, approvals, declaration-only tools |
| [references/checkpointing.md](references/checkpointing.md) | In-memory / file / Cosmos storage, resume, sub-workflow + HITL checkpoints |
| [references/orchestrations.md](references/orchestrations.md) | `SequentialBuilder`, `ConcurrentBuilder`, `HandoffBuilder`, `GroupChatBuilder`, `MagenticBuilder` |
| [references/composition.md](references/composition.md) | `WorkflowExecutor`, sub-workflow request interception, `workflow.as_agent()`, shared sessions, kwargs |

## Workshop-verified gotchas (LAB 04)

These are the seven traps observed while building the ZavaShop fulfillment workflow in [`workshop/LAB04-fulfillment-workflow/fulfillment_workflow.py`](../../../workshop/LAB04-fulfillment-workflow/fulfillment_workflow.py). Every bullet has been reproduced against `agent-framework 1.0.0rc3`.

1. **No `from __future__ import annotations`** in modules that use `@response_handler`. The validator (`_request_info_mixin.py`) inspects the **raw** `WorkflowContext[X, Y]` annotation; with deferred annotations it sees a `str` instead of the resolved generic and raises `Invalid handler signature`. Keep workflow modules annotation-eager.
2. **`workflow.as_agent()` always dispatches `list[Message]`** to the start executor — it asserts `is_type_compatible(list[Message], start.input_types)`. If your true intake input is `str`, write a class-based `IntakeExecutor(Executor)` with **two** `@handler` methods (`str` *and* `list[Message]`) that share a private `_emit`, so the same node serves both `workflow.run("ORD-…")` and `workflow.as_agent(...).run("ORD-…")`.
3. **`FileCheckpointStorage` is type-allow-listed.** Every user dataclass must appear in `allowed_checkpoint_types=["__main__:OrderRecord", "fulfillment_workflow:OrderRecord", …]` under **all** module names you ship under (typically `__main__` for the CLI run plus the importable module name when another LAB imports the file). Without this, resume fails with `Checkpoint deserialization blocked for type '…'`. Pattern:

   ```python
   user_types = ("OrderRecord", "StockReport", "AllocationPlan", ...)
   allowed = sorted({
       f"{m}:{t}"
       for m in (__name__, "__main__", "fulfillment_workflow")
       for t in user_types
   })
   storage = FileCheckpointStorage(storage_path=".checkpoints",
                                   allowed_checkpoint_types=allowed)
   ```

4. **Drain the stream after `request_info`.** Checkpoints are flushed at the **end** of each super-step. If you `break` out of `async for event in workflow.run(..., stream=True)` the moment you see `request_info`, the post-super-step checkpoint that carries the pending request is never written, and the subsequent resume raises `No pending requests found in workflow context`. Collect `pending[event.request_id] = event.data` and let the iterator end naturally — the workflow has paused, there is nothing left to do.

5. **A paused workflow instance cannot be re-run.** After pausing on `request_info`, the original `Workflow` still has `_is_running=True`, so calling `workflow.run(...)` on it again raises `Workflow is already running. Concurrent executions are not allowed.`. Build a fresh instance via your `build_workflow()` factory (same checkpoint dir, same allow-list) and call `fresh_wf.run(checkpoint_id=..., responses={req_id: reply})` in **one** call — this single entry point covers both restore and send-responses.

6. **`event.source_executor_id` raises for non-`request_info` events.** It is a property that throws `RuntimeError` for every event type except `"request_info"`. Use `event.executor_id` everywhere; it is always defined when the event has a source.

7. **No `ConcurrentBuilder` for typed executors.** `ConcurrentBuilder` is designed for fanning a `list[ChatMessage]` user prompt out to multiple chat agents and merging back as `list[ChatMessage]`. To fan a typed dataclass (e.g. `OrderRecord`) out to multiple `@executor` functions and merge via `list[LegResult]`, build the DAG explicitly:

   ```python
   WorkflowBuilder(start_executor=intake,
                   checkpoint_storage=storage,
                   name="ZavaFulfillment",
                   output_from=[finance, approval])
       .add_fan_out_edges(intake, [stock_check, shipping_quote])
       .add_fan_in_edges([stock_check, shipping_quote], allocator)
       .add_edge(allocator, approval)
       .add_edge(approval, dispatch)
       .add_edge(dispatch, finance)
       .build()
   ```

   `checkpoint_storage=` is a **kwarg** on `WorkflowBuilder` — there is no `.with_checkpoint_storage(...)` method.

Bonus shape rules surfaced by the same LAB: `Message("user", "hello")` iterates the string into per-character `Content` objects — pass `Message("user", ["hello"])`; read text via `m.text`. And `WorkflowEvent.type` is the discriminator (`"executor_invoked"`, `"output"`, `"request_info"`, `"superstep_completed"`), **not** `event.kind`.

