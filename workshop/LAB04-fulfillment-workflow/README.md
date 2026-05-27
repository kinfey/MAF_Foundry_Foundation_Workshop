# LAB 4 — Order Fulfillment Orchestration: Multi-Agent Workflow + HITL + Checkpoint

> **Powered by SKILL** (pick one track):
> - Python: [`agent-framework-workflows-py`](../../.github/skills/agent-framework-workflows-py/SKILL.md)
> - .NET (C#): [`agent-framework-workflows-csharp`](../../.github/skills/agent-framework-workflows-csharp/SKILL.md)
>
> **Foundry model**: `gpt-5.5`
> **Chinese edition**: [README.zh.md](./README.zh.md)

---

## Choose your stack

| Track | Build artefacts | Skill files | Data helper |
|-------|-----------------|-------------|-------------|
| 🐍 **Python** | `fulfillment_workflow.py` | [`agent-framework-workflows-py/SKILL.md`](../../.github/skills/agent-framework-workflows-py/SKILL.md) | [`zava_data.py`](../data/zava_data.py) |
| 🟦 **.NET (C#)** | `FulfillmentWorkflow/` | [`agent-framework-workflows-csharp/SKILL.md`](../../.github/skills/agent-framework-workflows-csharp/SKILL.md) | [`ZavaData.cs`](../data/ZavaData.cs) |

Python track is documented in [§Tasks](#tasks); .NET track is documented in [§.NET implementation path](#net-implementation-path). Same fixtures (`orders.json`, `inventory.json`, `carriers.json`, `warehouses.json`), same $1000 approval threshold, same scenarios A (`ORD-20260524-001`, $196.50) and B (`ORD-20260524-002`, $1500).

---

## Story

ZavaShop's order-to-shipment flow looks like this:

```
intake ──┬─► stock_check ──┐
         └─► shipping_quote ┴─► allocator ─► approval (HITL ≥ $1000) ─► dispatch ─► finance
```

The seven nodes are deterministic Python executors (`@executor` / `Executor` subclasses) — **not** chat agents. The whole workflow is later wrapped with `workflow.as_agent("ZavaFulfillment")` so LAB 5's control tower can call it like a single agent. Requirements:

1. **Deterministic orchestration**: stitch the 7 executors into a graph with `WorkflowBuilder`; every event is traceable.
2. **Fan-out / fan-in**: `stock_check` + `shipping_quote` run **concurrently** via `.add_fan_out_edges(intake, [stock_check, shipping_quote])` and merge in `allocator` via `.add_fan_in_edges([stock_check, shipping_quote], allocator)`. (No `ConcurrentBuilder` — that builder fans out the user prompt to chat agents; here we fan out a typed `OrderRecord` to executors.)
3. **HITL**: when total ≥ $1000, the `approval` executor calls `ctx.request_info(...)` and the workflow pauses with `WorkflowRunState.IDLE_WITH_PENDING_REQUESTS`.
4. **Checkpointing**: every super-step writes a file under `.checkpoints/`. A separate process restores via `workflow.run(checkpoint_id=..., responses={...})`.
5. **Packageable**: `fulfillment_agent = workflow.as_agent("ZavaFulfillment")` exposes the whole graph as one `Agent`, ready for LAB 5.

### Data this LAB consumes

All five executors operate on the shared fixtures under [`workshop/data/`](../data/README.md):

- [`orders.json`](../data/orders.json) — the two demo orders:
  - `ORD-20260524-001` (STD_445 Liu Wei, total **$196.50** — under $1000 → Scenario A, no HITL).
  - `ORD-20260524-002` (VIP_003 Aisha Mohammed, total **$1500.00** — over the HITL threshold → Scenario B).
- [`inventory.json`](../data/inventory.json) — stock used by `stock_agent.get_stock` (reuses LAB 1's wrapper around `find_stock`).
- [`carriers.json`](../data/carriers.json) — source for the 3 carrier quotes returned by `shipping_agent.quote_freight`. Each row gives `lanes` + `base_usd` + `per_kg_usd` + `transit_days_typical`, so quoted prices are reproducible.
- [`warehouses.json`](../data/warehouses.json) — used by `dispatch` to map the order's `fulfillment_center` to the warehouse manager (e.g. `SEA-01 → Mei Tanaka`).

---

## Learning goals

- Use `Executor` + `@handler` to write custom typed nodes; use `@executor` for one-shot function nodes.
- Build a graph with `WorkflowBuilder(start_executor=, name=, output_from=, checkpoint_storage=)` plus `.add_fan_out_edges` / `.add_fan_in_edges` / `.add_edge`.
- Run `stock_check` and `shipping_quote` in **parallel** via fan-out from `intake`, then fan in at `allocator`.
- Use `ctx.request_info(request_data=ApprovalRequest, response_type=ApprovalResponse)` for HITL, and a `@response_handler` to resume.
- Use `FileCheckpointStorage(storage_path=..., allowed_checkpoint_types=[...])` so user dataclasses survive serialization.
- Use `workflow.as_agent("ZavaFulfillment")` to expose the entire workflow behind the `Agent` interface.
- Stream and consume `executor_invoked` / `executor_completed` / `request_info` / `superstep_completed` / `output` events — `event.executor_id` is safe; `event.source_executor_id` raises unless `event.type == "request_info"`.

---

## Microsoft Learn references

- [Agent Framework — Workflows overview](https://learn.microsoft.com/en-us/agent-framework/workflows/index)
- [Workflows — Executors](https://learn.microsoft.com/en-us/agent-framework/workflows/executors)
- [Workflows — Edges (fan-out / fan-in / conditional)](https://learn.microsoft.com/en-us/agent-framework/workflows/edges)
- [Workflows — WorkflowBuilder & execution (events, streaming, checkpoint, request_info)](https://learn.microsoft.com/en-us/agent-framework/workflows/workflows)
- [Foundry — Agent development lifecycle](https://learn.microsoft.com/en-us/azure/foundry/agents/concepts/development-lifecycle)

> SKILL entry: [`agent-framework-workflows-py/SKILL.md`](../../.github/skills/agent-framework-workflows-py/SKILL.md)

---

## Tasks

### Step 1 — Pick the ZavaShop Coding Agent in Agent Mode

In VS Code Copilot Chat, switch to **Agent Mode**, open the agent picker, select **`zavashop-coding-agent`**, and send a prompt that names the LAB **and** the language:

```
I'm doing LAB 4 in Python — build the ZavaShop fulfillment workflow with concurrent stock+shipping, HITL approval and checkpoint resume.
```

> Do not prefix with `@zavashop-coding-agent`. The agent is chosen from the dropdown; the chat text is plain task description (always state LAB number + language).

The Coding Agent will:

1. Load [`.github/skills/agent-framework-workflows-py/SKILL.md`](../../.github/skills/agent-framework-workflows-py/SKILL.md) and the `references/parallelism.md`, `references/human_in_the_loop.md`, `references/checkpointing.md`, `references/composition.md` subpages.
2. Load this LAB README and the verified [§Workshop‑verified gotchas](../../.github/skills/agent-framework-workflows-py/SKILL.md#workshop-verified-gotchas-lab-04) section of the SKILL.
3. Create `fulfillment_workflow.py` under [`workshop/LAB04-fulfillment-workflow/`](.):
   ```
   intake ─┬► stock_check ─┐
           └► shipping_quote ┴► allocator ► approval (HITL) ► dispatch ► finance
   ```
   - All 7 nodes are deterministic Python executors — `intake / approval` are `class … (Executor)` subclasses with `@handler` + `@response_handler`; `stock_check / shipping_quote / allocator / dispatch / finance` are `@executor` function nodes.
   - **No `from __future__ import annotations`** in this file — it breaks the `@response_handler` validator (the framework reads the raw annotation string and fails to resolve `WorkflowContext[...]`).
   - `IntakeExecutor` exposes **two** `@handler` methods (`str` *and* `list[Message]`) so the same node serves both `workflow.run("ORD-…")` and `workflow.as_agent(…).run("ORD-…")`.
   - Concurrency comes from `.add_fan_out_edges(intake, [stock_check, shipping_quote])` + `.add_fan_in_edges([stock_check, shipping_quote], allocator)` — **not** `ConcurrentBuilder` (that builder is for chat agents).
   - `approval` calls `ctx.request_info(request_data=ApprovalRequest, response_type=ApprovalResponse)` when total ≥ $1000; a `@response_handler` resumes the flow.
   - `FileCheckpointStorage(storage_path=".checkpoints", allowed_checkpoint_types=[...])` — list every user dataclass `module:QualName` (under both `__main__` and `fulfillment_workflow`) so resume can deserialize.
   - End with `fulfillment_agent = workflow.as_agent("ZavaFulfillment")` for LAB 5 to consume.
4. Run two scenarios: < $1000 completes end-to-end (Scenario A); ≥ $1000 pauses, gets resumed from a checkpoint *in a fresh workflow instance* with `responses={...}` (Scenario B). The Coding Agent will NOT skip `ctx.request_info` nor reuse the same paused `Workflow` instance — see the gotcha bullets below.

> The Coding Agent will NOT skip `ctx.request_info` nor force-commit through a discarded checkpoint — that is the core teaching point of this LAB.

### Step 2 — Define the seven nodes

Typed dataclasses on every edge — never a raw `str`. The full source lives in [`fulfillment_workflow.py`](./fulfillment_workflow.py); the skeleton below highlights the pattern.

```python
# IMPORTANT: do NOT add `from __future__ import annotations` to this file.
# The @response_handler validator reads the raw WorkflowContext[...] annotation
# and fails to resolve it when annotations are deferred.

import sys
from dataclasses import dataclass
from pathlib import Path
from typing import Any, Literal, Never

from agent_framework import (
    Executor, FileCheckpointStorage, Message, WorkflowBuilder,
    WorkflowContext, executor, handler, response_handler,
)

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "data"))
from zava_data import find_order, find_stock, load_carriers, load_skus, load_warehouses

HITL_THRESHOLD_USD = 1000.0


@dataclass
class OrderRecord:
    order_id: str
    customer_id: str
    lines: list[dict[str, Any]]
    ship_to_city: str
    ship_to_warehouse: str
    total_usd: float

# … StockReport, FreightQuote, AllocationPlan, ApprovalRequest, ApprovalResponse, DispatchResult …


# 1. intake — class executor with TWO handlers so the same node accepts
#    both workflow.run("ORD-…") and workflow.as_agent().run("ORD-…").
class IntakeExecutor(Executor):
    def __init__(self, id: str = "intake") -> None:
        super().__init__(id=id)

    async def _emit(self, order_id: str, ctx: WorkflowContext[OrderRecord]) -> None:
        raw = find_order(order_id)
        if raw is None:
            raise ValueError(f"Unknown order: {order_id}")
        await ctx.send_message(OrderRecord(**raw))

    @handler
    async def from_string(self, order_id: str, ctx: WorkflowContext[OrderRecord]) -> None:
        await self._emit(order_id.strip(), ctx)

    @handler
    async def from_messages(self, messages: list[Message], ctx: WorkflowContext[OrderRecord]) -> None:
        # as_agent() always dispatches list[Message]; extract the last text turn.
        await self._emit((messages[-1].text or "").strip(), ctx)


# 2 & 3. stock_check / shipping_quote — pure @executor functions; no LLM needed
#        for this LAB, the carrier table + inventory table are deterministic.
@executor(id="stock_check")
async def stock_check(order: OrderRecord, ctx: WorkflowContext[LegResult]) -> None:
    rows = [find_stock(ln["sku"], order.ship_to_warehouse) for ln in order.lines]
    # …compute StockReport, send LegResult(kind="stock", …) downstream
    await ctx.send_message(LegResult(kind="stock", order=order, stock=report))


@executor(id="shipping_quote")
async def shipping_quote(order: OrderRecord, ctx: WorkflowContext[LegResult]) -> None:
    quotes = [
        CarrierQuote(c["carrier_id"], c["name"],
                     round(c["base_usd"] + c["per_kg_usd"] * total_kg, 2),
                     int(c["transit_days_typical"]))
        for c in load_carriers() if lane in c["lanes"]
    ][:3]
    await ctx.send_message(LegResult(kind="freight", order=order, freight=fq))


# 4. allocator — fan-in: receives list[LegResult] from the two upstream executors
@executor(id="allocator")
async def allocator(legs: list[LegResult], ctx: WorkflowContext[AllocationPlan]) -> None:
    stock_leg   = next(l for l in legs if l.kind == "stock")
    freight_leg = next(l for l in legs if l.kind == "freight")
    plan = AllocationPlan(order=stock_leg.order, stock=stock_leg.stock,
                          freight=freight_leg.freight,
                          total_usd=round(stock_leg.order.total_usd
                                          + freight_leg.freight.cheapest.price_usd, 2))
    await ctx.send_message(plan)


# 5. approval — HITL gate, with checkpointable state in self._pending
class ApprovalExecutor(Executor):
    def __init__(self, id: str = "approval") -> None:
        super().__init__(id=id)
        self._pending: dict[str, Any] | None = None  # JSON-able AllocationPlan

    @handler
    async def gate(self, plan: AllocationPlan,
                   ctx: WorkflowContext[AllocationPlan, dict[str, Any]]) -> None:
        if plan.total_usd < HITL_THRESHOLD_USD:
            await ctx.send_message(plan)
            return
        self._pending = dataclasses.asdict(plan)
        await ctx.request_info(
            request_data=ApprovalRequest(
                order_id=plan.order.order_id,
                customer_id=plan.order.customer_id,
                total_usd=plan.total_usd,
                reason="stock_shortage" if not plan.stock.all_in_stock
                       else "amount_over_threshold",
            ),
            response_type=ApprovalResponse,
        )

    @response_handler
    async def resume(
        self,
        original: ApprovalRequest,
        reply: ApprovalResponse,
        ctx: WorkflowContext[AllocationPlan, dict[str, Any]],
    ) -> None:
        plan = _plan_from_dict(self._pending)
        self._pending = None
        if not reply.approved:
            await ctx.yield_output({"status": "rejected",
                                    "order_id": plan.order.order_id,
                                    "reason": reply.reason})
            return
        await ctx.send_message(plan)

    async def on_checkpoint_save(self) -> dict[str, Any]:
        return {"pending": self._pending}

    async def on_checkpoint_restore(self, state: dict[str, Any]) -> None:
        self._pending = state.get("pending")


# 6. dispatch + 7. finance — @executor, emit dispatch order / shipped voucher
@executor(id="dispatch")
async def dispatch(plan: AllocationPlan, ctx: WorkflowContext[DispatchResult]) -> None: ...

@executor(id="finance")
async def finance(result: DispatchResult,
                  ctx: WorkflowContext[Never, dict[str, Any]]) -> None:
    await ctx.yield_output({"status": "shipped", "order_id": result.order_id, ...})
```

> **Resolve, don't parse.** `intake` takes an order id and looks it up in [`orders.json`](../data/orders.json) — it does *not* try to parse the customer's natural-language sentence. That keeps downstream nodes deterministic and lets the same fixtures power LAB 5. The `from_messages` handler exists solely so `workflow.as_agent()` (which always dispatches `list[Message]`) can reach the same `_emit` path.

### Step 3 — Assemble + checkpoint + as_agent

```python
def build_workflow():
    CHECKPOINT_DIR.mkdir(parents=True, exist_ok=True)

    # FileCheckpointStorage allow-lists every type it will deserialize.
    # Register each user dataclass under BOTH __main__ (CLI run) and
    # 'fulfillment_workflow' (when LAB 5 imports this module).
    user_types = (
        "OrderRecord", "StockLine", "StockReport", "CarrierQuote",
        "FreightQuote", "LegResult", "AllocationPlan",
        "ApprovalRequest", "ApprovalResponse", "DispatchResult",
    )
    allowed = sorted({
        f"{m}:{t}"
        for m in (__name__, "__main__", "fulfillment_workflow")
        for t in user_types
    })

    storage = FileCheckpointStorage(
        storage_path=str(CHECKPOINT_DIR),
        allowed_checkpoint_types=allowed,
    )
    intake = IntakeExecutor()
    approval = ApprovalExecutor()
    wf = (
        WorkflowBuilder(
            start_executor=intake,
            checkpoint_storage=storage,
            name="ZavaFulfillment",
            output_from=[finance, approval],  # finance on success, approval on rejection
        )
        .add_fan_out_edges(intake, [stock_check, shipping_quote])  # parallel
        .add_fan_in_edges([stock_check, shipping_quote], allocator)  # merge
        .add_edge(allocator, approval)
        .add_edge(approval, dispatch)
        .add_edge(dispatch, finance)
        .build()
    )
    return wf, storage


workflow, checkpoint_storage = build_workflow()
fulfillment_agent = workflow.as_agent("ZavaFulfillment")  # for LAB 5
```

### Step 4 — Run twice: once under $1000, once ≥ $1000 (HITL)

```python
# Scenario A — ORD-20260524-001 ($196.50): no request_info, finance yields shipped voucher
async for event in workflow.run("ORD-20260524-001", stream=True, include_status_events=False):
    if event.type == "executor_invoked":
        print("  ►", event.executor_id)
    elif event.type == "output":
        print("  ★", event.data)        # {"status": "shipped", ...}

# Scenario B — ORD-20260524-002 ($1500 + freight = $2034.50): pauses at approval
pending: dict[str, ApprovalRequest] = {}
async for event in workflow.run("ORD-20260524-002", stream=True, include_status_events=False):
    if event.type == "request_info":
        pending[event.request_id] = event.data
    # IMPORTANT: do NOT break here. Let the stream iterate to its natural end —
    # the *post*-superstep checkpoint that carries the pending request is only
    # written after the superstep completes. Breaking early loses it on disk.
```

> Use `event.executor_id`, not `event.source_executor_id` — the latter is a property that raises `RuntimeError` for every event type except `"request_info"`.

### Step 5 — Resume from a checkpoint

After Scenario B has drained, the same process (or a brand-new one) builds a **fresh** workflow instance and resumes by handing it both the `checkpoint_id` and the `responses` map:

```python
latest = await checkpoint_storage.get_latest(workflow_name=workflow.name)
fresh_wf, _ = build_workflow()              # MUST be a new Workflow instance
responses = {
    req_id: ApprovalResponse(approved=True, reason="supervisor: ship it")
    for req_id in pending
}
async for event in fresh_wf.run(checkpoint_id=latest.checkpoint_id,
                                responses=responses,
                                stream=True,
                                include_status_events=False):
    if event.type == "output":
        print("voucher:", event.data)       # → {"status": "shipped", ...}
```

> The original `workflow` instance still has `_is_running=True` after pausing on `request_info` — calling `workflow.run(...)` on it again raises `Workflow is already running. Concurrent executions are not allowed.` Build a fresh `Workflow` from `build_workflow()` (same checkpoint dir, same allow-list) to represent a new process attaching to the paused state.

### Workshop-verified gotchas (read before coding)

Pulled from the verified LAB 4 implementation — every bullet has been observed in `agent-framework 1.0.0rc3`:

1. **No `from __future__ import annotations`.** The `@response_handler` validator (`_request_info_mixin.py`) inspects the raw `WorkflowContext[X, Y]` annotation string and raises if it is a string instead of the resolved generic. Keep this file annotation-eager.
2. **`workflow.as_agent()` always dispatches `list[Message]`** into the start executor — it asserts `is_type_compatible(list[Message], start.input_types)`. If your real intake takes `str`, expose **two** `@handler` methods (`str` + `list[Message]`) that share a private `_emit`.
3. **`FileCheckpointStorage` is type-allow-listed.** Every user dataclass must be in `allowed_checkpoint_types=["__main__:OrderRecord", "fulfillment_workflow:OrderRecord", …]`. Without this, resume fails with `Checkpoint deserialization blocked for type '…'`.
4. **Drain the stream after `request_info`.** Checkpoints are flushed at the **end** of each super-step. If you `break` out of the stream as soon as you see `request_info`, the post-super-step checkpoint carrying that pending request is not yet on disk and resume reports `No pending requests found in workflow context`. Let the `async for` end naturally — once the workflow paused, it has nothing else to do.
5. **A paused workflow instance cannot be re-run.** After pausing on `request_info`, the same `Workflow` still has `_is_running=True`. Build a fresh instance via `build_workflow()` and call `fresh_wf.run(checkpoint_id=..., responses=...)`.
6. **`event.source_executor_id` raises for non-`request_info` events** — use `event.executor_id` everywhere.
7. **No `ConcurrentBuilder` for typed executors.** `ConcurrentBuilder` fans the user prompt out to chat agents and aggregates `list[Message]`; LAB 4 fans a typed `OrderRecord` out to two executors and merges via `list[LegResult]` — use `.add_fan_out_edges` + `.add_fan_in_edges`.

---

## Acceptance criteria

- [ ] In the streamed events, stock_check and shipping_quote enter `executor_invoked` **at the same time** (proves concurrency).
- [ ] Scenario A (`ORD-20260524-001`, $196.50) completes end-to-end without ever raising a `request_info` event.
- [ ] Scenario B (`ORD-20260524-002`, $1500) always pauses at `approval` with an event of kind `request_info`.
- [ ] Once approval is filled in, dispatch + finance continue and the final `yield_output` returns `{"status": "shipped", ...}`.
- [ ] The checkpoint directory contains multiple superstep files; resuming continues from the breakpoint with no double-decrement of stock.
- [ ] `fulfillment_agent = workflow.as_agent()` is callable from external code like any agent (returns a summary-style reply).
- [ ] `intake` resolves the order via `find_order` (no free-form parsing), and `quote_freight` returns rows derived from `carriers.json` (carrier IDs match `FEDEX` / `DHL` / `USPS` / `ARAMEX` / `SFEXPRESS`).

---

## .NET implementation path

Same DAG, same HITL gate, same checkpoint resume.

### Step 1 — Pick the ZavaShop Coding Agent in Agent Mode (C#)

In VS Code Copilot Chat → **Agent Mode** → agent picker → **`zavashop-coding-agent`**, then send:

```
I'm doing LAB 4 in C# — build the order-fulfillment workflow with HITL approval and checkpoint resume.
```

It will create `FulfillmentWorkflow/` under [`workshop/LAB04-fulfillment-workflow/`](.) with `..\..\data\ZavaData.cs` linked and these packages (all `Version="*-*"`): `Microsoft.Agents.AI`, `Microsoft.Agents.AI.Workflows`, `Microsoft.Extensions.AI`, `Azure.Identity`. The `.csproj` should also carry `<NoWarn>$(NoWarn);NU1604;NU1902;MAAI001</NoWarn>` so the prerelease wildcards and `[Experimental]` annotations don't break the build. Unlike LAB 1–LAB 3, this LAB does **not** call any Foundry agent — all seven nodes are deterministic .NET executors — so neither `Microsoft.Agents.AI.Foundry` nor `Azure.AI.Projects` is needed.

> The .NET track ships **`Microsoft.Agents.AI.Workflows` 1.7.0** (the version that resolves from `Version="*-*"` as of November 2025). The API surface below has been verified against that build. If a probe in your environment disagrees with what is documented here, trust the probe and read [`.github/skills/agent-framework-workflows-csharp/SKILL.md`](../../.github/skills/agent-framework-workflows-csharp/SKILL.md) — specifically the **Workshop-verified gotchas (LAB 04)** section at the bottom.

### Step 2 — Build the typed DAG with `WorkflowBuilder`

Each node is its own `Executor<TIn, TOut>` (or `Executor<TIn>` for nodes that talk to `IWorkflowContext` directly), wired together with fan-out, fan-in barrier and conditional edges:

```csharp
using Microsoft.Agents.AI.Workflows;
using Microsoft.Agents.AI.Workflows.Checkpointing;
using ZavaShop.Workshop.Data;

var intake          = new IntakeExecutor();
var stockCheck      = new StockCheckExecutor();
var shippingQuote   = new ShippingQuoteExecutor();
var allocator       = new AllocatorExecutor();          // Executor<LegResult, AllocationPlan?>
var approvalBuilder = new ApprovalRequestBuilderExecutor();
var approvalPort    = RequestPort.Create<HumanApprovalRequest, HumanApprovalResponse>("approval_port");
var approvalResume  = new ApprovalResumeExecutor();     // Executor<HumanApprovalResponse>
var dispatch        = new DispatchExecutor();
var finance         = new FinanceExecutor();            // Executor<DispatchResult>

Workflow workflow = new WorkflowBuilder(intake)
    .AddFanOutEdge(intake, [stockCheck, shippingQuote])
    .AddFanInBarrierEdge([stockCheck, shippingQuote], allocator)
    // Conditional edges — gate over $1,000, auto-allocate below.
    .AddEdge<AllocationPlan?>(allocator, approvalBuilder,
        condition: msg => msg is AllocationPlan plan && plan.TotalUsd >= 1000m)
    .AddEdge<AllocationPlan?>(allocator, dispatch,
        condition: msg => msg is AllocationPlan plan && plan.TotalUsd <  1000m)
    .AddEdge(approvalBuilder, approvalPort)
    .AddEdge(approvalPort,    approvalResume)
    .AddEdge<AllocationPlan>(approvalResume, dispatch)   // approved path
    .AddEdge(dispatch, finance)
    .WithOutputFrom(finance, approvalResume)             // either ships or rejects
    .WithName("ZavaFulfillment")
    .WithDescription("Order intake → stock + freight → HITL gate → dispatch → finance.")
    .Build();
```

Three patterns to notice that don't show up in the standard SKILL examples:

- **The fan-in barrier delivers messages individually**, not bundled as `List<LegResult>` — see gotcha #1 in the C# SKILL. `AllocatorExecutor` keeps an instance-field buffer and returns `null` until both legs are in, then returns the assembled plan. The conditional edges above filter the `null` sentinel out by pattern-matching on `AllocationPlan`.
- **The HITL gate is a `RequestPort`**, not an inline `ctx.RequestInfoAsync(...)` call (that method doesn't exist in .NET). `ApprovalRequestBuilderExecutor` stashes the plan in shared state under scope `"Approval"`, key `"pending_plan"`, then forwards a `HumanApprovalRequest` to the port; the resume executor reads the plan back after the human replies.
- **`ApprovalResumeExecutor` is `Executor<HumanApprovalResponse>` with no `TOut`** — it can either `SendMessageAsync(plan)` onto the dispatcher (approved) or `YieldOutputAsync(rejected)` (rejected). Both behaviors must be declared in a `ConfigureProtocol` override (gotcha #3 in the C# SKILL).

### Step 3 — Wire durable checkpoints + the HITL event loop

```csharp
using Microsoft.Agents.AI.Workflows.Checkpointing;

var store = new FileSystemJsonCheckpointStore(new DirectoryInfo("./_checkpoints"));
CheckpointManager manager = CheckpointManager.CreateJson(store, customOptions: null);

await using StreamingRun run = await InProcessExecution.RunStreamingAsync(
    workflow, "ORD-20260524-002", manager, sessionId: Guid.NewGuid().ToString(), CancellationToken.None);

CheckpointInfo? lastCheckpoint = null;
await foreach (WorkflowEvent evt in run.WatchStreamAsync())
{
    switch (evt)
    {
        case SuperStepCompletedEvent s when s.CompletionInfo?.Checkpoint is { } cp:
            lastCheckpoint = cp;
            break;

        case RequestInfoEvent req
            when req.Request.TryGetDataAs<HumanApprovalRequest>(out HumanApprovalRequest? ask):
        {
            bool decision = PromptOperator(ask);   // Console.ReadLine in the LAB
            ExternalResponse response = req.Request.CreateResponse(
                new HumanApprovalResponse(decision, decision ? "approved" : "rejected"));
            await run.SendResponseAsync(response);
            break;
        }

        case WorkflowOutputEvent done:
            Console.WriteLine($"Final → {done.Data}");
            break;

        case ExecutorFailedEvent fail:
            Console.Error.WriteLine($"{fail.ExecutorId} failed: {fail.Data}");
            break;
    }
}

// Resume — fresh run, same checkpoint manager, no sessionId:
await using StreamingRun resumed = await InProcessExecution.ResumeStreamingAsync(
    workflow, lastCheckpoint!, manager, CancellationToken.None);
```

Notes:
- The streaming methods are `RunStreamingAsync` / `ResumeStreamingAsync` (no `StreamAsync` / `ResumeStreamAsync`); `Checkpointed<TRun>` doesn't exist — the call returns a bare `StreamingRun` (gotcha #2 in the C# SKILL).
- Always handle `ExecutorFailedEvent` and `WorkflowErrorEvent` — exceptions thrown inside executors arrive as events, they don't bubble out of the iterator.
- `request.TryGetDataAs<T>(out T)` is the only supported payload accessor; build the response via `request.CreateResponse(value)` (gotcha #4).

### Step 4 — Every executor reads through `ZavaData`

```csharp
internal sealed class IntakeExecutor() : Executor<string, OrderRecord>("intake")
{
    public override ValueTask<OrderRecord> HandleAsync(
        string orderId, IWorkflowContext ctx, CancellationToken ct = default)
    {
        JsonNode? order = ZavaData.FindOrder(orderId)
            ?? throw new InvalidOperationException($"Unknown order {orderId}");
        return ValueTask.FromResult(OrderRecord.FromJson(order));
    }
}

internal sealed class ShippingQuoteExecutor() : Executor<OrderRecord, LegResult>("shipping_quote")
{
    public override ValueTask<LegResult> HandleAsync(
        OrderRecord order, IWorkflowContext ctx, CancellationToken ct = default)
    {
        var quotes = ZavaData.LoadCarriers()
            .Select(c => new CarrierQuote(
                CarrierId: c["carrier_id"]!.GetValue<string>(),
                EtaDays:   c["avg_lead_time_days"]!.GetValue<int>(),
                PriceUsd:  Pricing.Quote(order, c)))
            .OrderBy(q => q.PriceUsd)
            .ToList();
        return ValueTask.FromResult(new LegResult(Kind: "shipping_quote", Quotes: quotes));
    }
}
```

No inline carrier table — every fact comes from `carriers.json` / `orders.json` / `inventory.json` via `ZavaData.LoadCarriers()` / `ZavaData.FindOrder(id)` / `ZavaData.FindStock(sku, warehouse)`, so the carrier ids match `FEDEX` / `DHL` / `USPS` / `ARAMEX` / `SFEXPRESS` exactly. Stock decrement happens once, in `DispatchExecutor`, after approval.

### Step 5 — Expose the whole workflow as one `AIAgent`

```csharp
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;

AIAgent fulfillmentAgent = workflow.AsAIAgent(
    id:          "zava-fulfillment",
    name:        "ZavaFulfillment",
    description: "Drives a ZavaShop order from intake to finance, with HITL approval above $1000.",
    includeWorkflowOutputsInResponse: true);

AgentRunResponse response = await fulfillmentAgent.RunAsync("ORD-20260524-001");
Console.WriteLine(response.Text);
```

The extension method is **`AsAIAgent`** (not `AsAgent`) — gotcha #5 in the C# SKILL. Unlike Python, the start executor doesn't need to accept `list<ChatMessage>` — the `string` order id signature works. The wrapped agent is now droppable into LAB 5 as a sub-agent.

### Step 6 — Run

```bash
# Auto-approval path (under $1,000 — runs straight through, no console prompt)
dotnet run --project workshop/LAB04-fulfillment-workflow/FulfillmentWorkflow -- ORD-20260524-001

# HITL path (over $1,000 — prompts on the console)
dotnet run --project workshop/LAB04-fulfillment-workflow/FulfillmentWorkflow -- ORD-20260524-002

# Default mode = both scenarios + a resume-from-checkpoint demo, no prompts
LAB04_AUTO_APPROVE=yes dotnet run --project workshop/LAB04-fulfillment-workflow/FulfillmentWorkflow
```

`LAB04_AUTO_APPROVE=yes` bypasses `Console.ReadLine` and decides "approve" automatically — use it for CI / quick reruns. The default run (no argument) walks Scenario A, then Scenario B, then drives the resume path using the **last `SuperStepCompletedEvent.CompletionInfo.Checkpoint`** captured during Scenario B. Each run writes a JSON file per super-step plus a line to `_checkpoints/index.jsonl`; cleaning that directory resets all state. The acceptance bullets above apply unchanged — the .NET track satisfies each one through the executors and event loop shown here.

### Workshop-verified gotchas (.NET)

- **Fan-in barrier delivers messages individually**, not as `List<TOut>`, for typed (non-agent) executors → use the instance-buffer + null-sentinel + conditional-edge pattern shown in Step 2.
- **Multi-output executors must override `ConfigureProtocol`** to declare both `SendsMessageType` *and* `YieldsOutputType`; the attribute form only registers a single behavior.
- **The wrap-as-agent extension is `AsAIAgent`** (not `AsAgent`).
- **`ResumeStreamingAsync` takes four arguments** — `(workflow, checkpoint, manager, ct)` — no `sessionId`. `Checkpointed<TRun>` does not exist.
- **Durable checkpoints** are `FileSystemJsonCheckpointStore` + `CheckpointManager.CreateJson(store, null)` — there is no `FileCheckpointStorage` in .NET.
- **`<NoWarn>$(NoWarn);NU1604;NU1902;MAAI001</NoWarn>`** belongs in the `.csproj` because the wildcard-versioned prerelease packages trigger all three diagnostics.

The full list (with code snippets) lives at the bottom of [`agent-framework-workflows-csharp/SKILL.md`](../../.github/skills/agent-framework-workflows-csharp/SKILL.md#workshop-verified-gotchas-lab-04).

---

## Story handoff

The 5 ZavaShop agents are playing in tune — but the COO slams the table:

> *"All of this lives in your engineers' Python consoles — the operations managers can't see anything! I want a **control tower** — a web page that shows in real time what every agent is doing, lets you expand any node to see its inputs and outputs, lets a supervisor click ✓ to approve in the UI, and lets us push a message into an agent in an emergency."*

— That kicks off [LAB 5](../LAB05-control-tower-agui/README.md).
