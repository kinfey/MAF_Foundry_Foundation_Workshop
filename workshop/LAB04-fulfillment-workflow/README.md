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
New order ──► Stock check ──► Allocation ──► Freight quote ──► (supervisor approval if ≥ $1000) ──► Dispatch ──► Finance
```

Each of the 5 roles is an independent agent running on `gpt-5.5`. Requirements:

1. **Deterministic orchestration**: stitch the 5 agents into a graph with `WorkflowBuilder`; every event is traceable.
2. **Fan-out / fan-in**: stock check + freight quote should run **concurrently** (`ConcurrentBuilder` or a custom fan-out).
3. **HITL**: when total ≥ $1000, pause with `ctx.request_info(...)` and wait for a supervisor yes/no.
4. **Checkpointing**: every super-step automatically writes a checkpoint, so an outage can be resumed via `workflow.run(checkpoint_id=...)`.
5. **Packageable**: finally wrap the whole workflow as a single outward-facing agent with `workflow.as_agent()`, so the LAB 5 control tower can call it.

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

- Use `Executor` + `@handler` to write custom nodes; use `Agent`s directly as nodes (agent-as-executor).
- Build a graph with `WorkflowBuilder`: `start_executor` + `add_edge` + `output_from`.
- Use `ConcurrentBuilder` or explicit fan-out to run *stock check* and *freight quote* in parallel, then fan in at *allocation*.
- Use `ctx.request_info(...)` for HITL; on the outside, `workflow.run(responses={...})` to fill the answer back in.
- Use `checkpoint_storage=...` + `@step` caching to make the workflow **resumable from any checkpoint**.
- Use `workflow.as_agent(...)` to expose the entire workflow behind the `Agent` interface.
- Stream and consume `executor_invoked / executor_completed / request_info / superstep_completed / output` events.

---

## Microsoft Learn alignment

- *Agent Framework — Workflows: graph & functional authoring*
- *Executors, Handlers, WorkflowContext*
- *Orchestration builders (Sequential / Concurrent / Handoff / GroupChat / Magentic)*
- *Human-in-the-loop with `ctx.request_info`*
- *Checkpointing & resume*
- *`workflow.as_agent()` and sub-workflows*

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

1. Load [`.github/skills/agent-framework-workflows-py/SKILL.md`](../../.github/skills/agent-framework-workflows-py/SKILL.md).
2. Load this LAB README; `search` whether LAB 1's `get_stock` can be reused.
3. Create `fulfillment_workflow.py` under [`workshop/LAB04-fulfillment-workflow/`](.):
   ```
   intake ─┬► stock_check ─┐
          └► shipping_quote ┴► allocator ► approval (HITL) ► dispatch ► finance
   ```
   - `intake / allocator / dispatch / finance` are `@executor` function nodes
   - `stock_check / shipping_quote / approval` are `Agent`s (FoundryChatClient + gpt-5.5)
   - `ConcurrentBuilder` runs stock + shipping in parallel
   - The `approval` node calls `ctx.request_info(...)` when total ≥ $1000
   - `FileCheckpointStorage(".checkpoints")`
   - End with `workflow.as_agent(name="ZavaFulfillment")` for LAB 5 to consume
4. Run two scenarios: < $1000 completes end-to-end; ≥ $1000 pauses and resumes after you manually feed `responses={...}`. Then add a Ctrl+C + `checkpoint_id` resume scenario.

> The Coding Agent will NOT skip `ctx.request_info` nor force-commit through a discarded checkpoint — that is the core teaching point of this LAB.

### Step 2 — Define the five nodes

```python
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "data"))
from zava_data import find_order, find_stock, load_carriers

# 1. intake: resolve the order id into a typed OrderDraft from orders.json
@executor(id="intake")
async def intake(order_id: str, ctx: WorkflowContext[OrderDraft]) -> None:
    raw = find_order(order_id)
    if raw is None:
        raise ValueError(f"Unknown order {order_id}")
    order = OrderDraft(**raw)              # do NOT parse free-form text — use the fixture
    await ctx.send_message(order)

# 2. stock_agent tool: thin wrapper around find_stock (same shape as LAB 1)
def get_stock(sku: str, warehouse: str) -> str:
    row = find_stock(sku, warehouse)
    return "ok" if row and row["on_hand"] >= 1 else "short"

# 3. shipping_agent tool: pull live carrier rows from carriers.json
def quote_freight(destination_country: str, weight_kg: float) -> list[dict]:
    quotes = []
    for c in load_carriers():
        if destination_country in c["lanes"]:
            price = round(c["base_usd"] + c["per_kg_usd"] * weight_kg, 2)
            quotes.append({
                "carrier": c["carrier_id"],
                "name": c["name"],
                "price_usd": price,
                "transit_days": c["transit_days_typical"],
            })
    return quotes[:3]

# 2 & 3: stock_check / shipping_quote as Agents
stock_agent = Agent(
    client=FoundryChatClient(project_endpoint=..., model="gpt-5.5", credential=...),
    name="StockChecker",
    instructions="Use get_stock to confirm each SKU in the order can ship; output JSON {sku: ok|short}.",
    tools=[get_stock],
)
shipping_agent = Agent(
    ...,
    name="ShippingQuoter",
    instructions="Use quote_freight to return 3 carrier quotes; output JSON.",
    tools=[quote_freight],
)

# 4. allocator: merge stock + freight into an AllocationPlan
@executor(id="allocator")
async def allocator(parts: list, ctx: WorkflowContext[AllocationPlan]) -> None:
    plan = merge(parts)
    await ctx.send_message(plan)

# 5. approval: HITL
@executor(id="approval")
async def approval(plan: AllocationPlan, ctx: WorkflowContext[AllocationPlan]) -> None:
    if plan.total_usd >= 1000:
        decision = await ctx.request_info(
            prompt=f"Order total is ${plan.total_usd}. Approve?",
            request_type="supervisor_yes_no",
        )
        if not decision["approved"]:
            await ctx.yield_output({"status": "rejected", "reason": decision.get("reason")})
            return
    await ctx.send_message(plan)

# 6. dispatch + 7. finance: @executor, emitting the dispatch order / finance voucher
```

> **Resolve, don't parse.** The intake node takes the order id and looks it up in [`orders.json`](../data/orders.json) — it does *not* try to parse the customer's natural-language sentence. That keeps downstream nodes deterministic and lets the same fixtures power LAB 5.

### Step 3 — Assemble + checkpoint + as_agent

```python
from agent_framework import WorkflowBuilder, FileCheckpointStorage
from agent_framework.orchestrations import ConcurrentBuilder

# concurrent fan-out
parallel = ConcurrentBuilder().add(stock_agent).add(shipping_agent).build()

workflow = (
    WorkflowBuilder(start_executor=intake)
    .add_edge(intake, parallel)
    .add_edge(parallel, allocator)
    .add_edge(allocator, approval)
    .add_edge(approval, dispatch)
    .add_edge(dispatch, finance)
    .with_checkpoint_storage(FileCheckpointStorage(".checkpoints"))
    .build()
)

# Expose as an Agent for LAB 5
fulfillment_agent = workflow.as_agent(name="ZavaFulfillment")
```

### Step 4 — Run twice: once under $1000, once ≥ $1000 (HITL)

```python
# Scenario A: ORD-20260524-001 totals $196.50 — end-to-end without HITL
await workflow.run("ORD-20260524-001")

# Scenario B: ORD-20260524-002 totals $1500.00 — triggers request_info
run = workflow.run_stream("ORD-20260524-002")
async for event in run:
    if event.kind == "request_info":
        request_id, prompt = event.request_id, event.prompt
        print("Supervisor approval:", prompt)
        # Simulate approval
        await workflow.run(responses={request_id: {"approved": True}})
```

### Step 5 — Resume from a checkpoint

Deliberately `Ctrl+C` the second run; note the `checkpoint_id`; then call `workflow.run(checkpoint_id=...)` to resume and confirm execution restarts from the breakpoint rather than from scratch.

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

It will create `FulfillmentWorkflow/` under [`workshop/LAB04-fulfillment-workflow/`](.) with `..\..\data\ZavaData.cs` linked and these packages: `Microsoft.Agents.AI`, `Microsoft.Agents.AI.Workflows`, `Microsoft.Agents.AI.Foundry`, `Azure.AI.Projects`, `Azure.Identity`.

### Step 2 — Build the DAG with `WorkflowBuilder`

```csharp
using Microsoft.Agents.AI.Workflows;
using Microsoft.Agents.AI.Workflows.Checkpointing;
using ZavaShop.Workshop.Data;

var workflow = new WorkflowBuilder()
    .AddExecutor<OrderId, OrderRecord>("intake",        IntakeAsync)
    .AddExecutor<OrderRecord, StockResult>("stock_check",  StockCheckAsync)
    .AddExecutor<OrderRecord, QuoteResult>("shipping_quote", ShippingQuoteAsync)
    .AddExecutor<(StockResult, QuoteResult), AllocationPlan>("allocation", AllocateAsync)
    .AddExecutor<AllocationPlan, ApprovalDecision>("approval", ApprovalAsync)
    .AddExecutor<ApprovalDecision, DispatchResult>("dispatch", DispatchAsync)
    .AddExecutor<DispatchResult, FinanceResult>("finance", FinanceAsync)
    .AddEdge("intake", "stock_check")
    .AddEdge("intake", "shipping_quote")               // fan-out
    .AddFanInEdge(["stock_check", "shipping_quote"], "allocation")
    .AddEdge("allocation", "approval")
    .AddEdge("approval",   "dispatch")
    .AddEdge("dispatch",   "finance")
    .SetStartExecutor("intake")
    .WithCheckpointing(new FileCheckpointStorage("./_checkpoints"))
    .Build();
```

### Step 3 — HITL in the `approval` executor

```csharp
static async Task<ApprovalDecision> ApprovalAsync(AllocationPlan plan, IWorkflowContext ctx)
{
    if (plan.TotalUsd < 1000m)
        return new ApprovalDecision(true, "auto-approved (<$1000)");

    var hitl = await ctx.RequestInfoAsync<HumanApprovalRequest, HumanApprovalResponse>(
        new HumanApprovalRequest(plan.OrderId, plan.TotalUsd, plan.Reason));

    return new ApprovalDecision(hitl.Approve, hitl.Comment);
}
```

The outer event loop catches `RequestInfoEvent`, prompts the operator on the console, then calls `workflow.SendResponseAsync(...)` — same shape as the Python `ctx.request_info(...)` flow.

### Step 4 — `intake` and `shipping_quote` go through `ZavaData`

```csharp
static Task<OrderRecord> IntakeAsync(OrderId id, IWorkflowContext ctx)
{
    var order = ZavaData.FindOrder(id.Value)
                ?? throw new InvalidOperationException($"Unknown order {id.Value}");
    return Task.FromResult(OrderRecord.FromJson(order));
}

static Task<QuoteResult> ShippingQuoteAsync(OrderRecord order, IWorkflowContext ctx)
{
    var quotes = ZavaData.LoadCarriers()
        .Select(c => new CarrierQuote(
            CarrierId: c["carrier_id"]!.GetValue<string>(),
            EtaDays:   c["avg_lead_time_days"]!.GetValue<int>(),
            PriceUsd:  Pricing.Quote(order, c)))
        .OrderBy(q => q.PriceUsd)
        .ToList();
    return Task.FromResult(new QuoteResult(quotes));
}
```

No inline carrier table — it must come from `carriers.json` via `ZavaData.LoadCarriers()` so the `FEDEX` / `DHL` / `USPS` / `ARAMEX` / `SFEXPRESS` ids match the acceptance bullet.

### Step 5 — Expose the whole workflow as one agent (C#)

```csharp
AIAgent fulfillmentAgent = workflow.AsAgent(
    name: "FulfillmentOrchestrator",
    description: "Drives a ZavaShop order from intake to finance, with HITL approval above $1000.");
```

`fulfillmentAgent` can now be passed to LAB 5 as a sub-agent.

### Step 6 — Run + resume from checkpoint

```bash
dotnet run --project workshop/LAB04-fulfillment-workflow/FulfillmentWorkflow -- ORD-20260524-001
dotnet run --project workshop/LAB04-fulfillment-workflow/FulfillmentWorkflow -- ORD-20260524-002
```

For Scenario B, `Ctrl+C` mid-approval; note the printed `checkpoint_id`; relaunch with `-- ORD-20260524-002 --resume <id>` and confirm execution resumes from the checkpoint, with **no double stock decrement**. The acceptance bullets above apply unchanged.

---

## Story handoff

The 5 ZavaShop agents are playing in tune — but the COO slams the table:

> *"All of this lives in your engineers' Python consoles — the operations managers can't see anything! I want a **control tower** — a web page that shows in real time what every agent is doing, lets you expand any node to see its inputs and outputs, lets a supervisor click ✓ to approve in the UI, and lets us push a message into an agent in an emergency."*

— That kicks off [LAB 5](../LAB05-control-tower-agui/README.md).
