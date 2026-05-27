# LAB 4 — 订单履约调度：多 Agent Workflow + HITL + Checkpoint

> **由 SKILL 协助**（任选一个赛道）：
> - Python：[`agent-framework-workflows-py`](../../.github/skills/agent-framework-workflows-py/SKILL.md)
> - .NET（C#）：[`agent-framework-workflows-csharp`](../../.github/skills/agent-framework-workflows-csharp/SKILL.md)
>
> **Foundry 模型**：`gpt-5.5`

---

## 选择你的技术栈

| 赛道 | 交付物 | 需要加载的 Skill | 数据 helper |
|------|--------|---------------------|---------------|
| 🐍 **Python** | `fulfillment_workflow.py` | [`agent-framework-workflows-py/SKILL.md`](../../.github/skills/agent-framework-workflows-py/SKILL.md) | [`zava_data.py`](../data/zava_data.py) |
| 🟦 **.NET（C#）** | `FulfillmentWorkflow/` | [`agent-framework-workflows-csharp/SKILL.md`](../../.github/skills/agent-framework-workflows-csharp/SKILL.md) | [`ZavaData.cs`](../data/ZavaData.cs) |

Python 赛道在 [§任务清单](#任务清单)；.NET 赛道在 [§.NET 实现赛道](#net-实现赛道)。同一份 fixture（`orders.json` / `inventory.json` / `carriers.json` / `warehouses.json`）、同一个 $1000 的审批阈值、同一组场景 A（`ORD-20260524-001`, $196.50）与 B（`ORD-20260524-002`, $1500）。

---

## 故事

ZavaShop 的「下单到出仓」流程长这样：

```
intake ──┬─► stock_check ──┐
         └─► shipping_quote ┴─► allocator ─► approval (HITL ≥ $1000) ─► dispatch ─► finance
```

7 个节点全是确定性的 Python executor（`@executor` 或 `Executor` 子类）—— **不是** 聊天 Agent。整张图最后通过 `workflow.as_agent("ZavaFulfillment")` 包成一个 Agent，供 LAB 5 的指挥中心调用。要求：

1. **确定性编排**：用 `WorkflowBuilder` 把 7 个 executor 串成一张图，事件可追踪。
2. **fan-out / fan-in**：`stock_check` + `shipping_quote` 通过 `.add_fan_out_edges(intake, [stock_check, shipping_quote])` **并发**，再用 `.add_fan_in_edges([stock_check, shipping_quote], allocator)` 在 `allocator` 处汇合。（**不用** `ConcurrentBuilder`，那个 builder 是把 user prompt 分发给聊天 Agent；这里要把一个 **类型化** 的 `OrderRecord` 扇给两个 executor。）
3. **HITL**：金额 ≥ $1000 时 `approval` executor 调用 `ctx.request_info(...)`，工作流停在 `WorkflowRunState.IDLE_WITH_PENDING_REQUESTS`。
4. **Checkpoint**：每个 super-step 在 `.checkpoints/` 下写一个文件。另一个进程通过 `workflow.run(checkpoint_id=..., responses={...})` 恢复。
5. **可包装**：`fulfillment_agent = workflow.as_agent("ZavaFulfillment")` 把整张图暴露成一个 `Agent`，给 LAB 5 用。

### 本 LAB 读取哪些数据

五个 executor 都使用 [`workshop/data/`](../data/README.zh.md) 下的共享 fixture：

- [`orders.json`](../data/orders.json) — 两个演示订单：
  - `ORD-20260524-001`（STD_445 Liu Wei，总额 **$196.50**）— 低于 $1000 → 场景 A，不走 HITL。
  - `ORD-20260524-002`（VIP_003 Aisha Mohammed，总额 **$1500.00**）— 高于 HITL 阈值 → 场景 B。
- [`inventory.json`](../data/inventory.json) — `stock_agent.get_stock` 读取的库存（复用 LAB 1 包装过的 `find_stock`）。
- [`carriers.json`](../data/carriers.json) — `shipping_agent.quote_freight` 返回的 3 个承运商报价从这里来，包含 `lanes` + `base_usd` + `per_kg_usd` + `transit_days_typical`，报价可复现。
- [`warehouses.json`](../data/warehouses.json) — `dispatch` 用它把订单的 `fulfillment_center` 映射到负责人（如 `SEA-01 → Mei Tanaka`）。

---

## 学习目标

- 用 `Executor` + `@handler` 写类型化的自定义节点；用 `@executor` 写一次性的函数节点。
- 用 `WorkflowBuilder(start_executor=, name=, output_from=, checkpoint_storage=)` + `.add_fan_out_edges` / `.add_fan_in_edges` / `.add_edge` 构图。
- 通过从 `intake` 显式 fan-out，让 *库存检查* 与 *物流报价* 在 `allocator` 处 fan-in，实现 **并行**。
- 用 `ctx.request_info(request_data=ApprovalRequest, response_type=ApprovalResponse)` 做 HITL；用 `@response_handler` 恢复。
- 用 `FileCheckpointStorage(storage_path=..., allowed_checkpoint_types=[...])` 让用户 dataclass 能被序列化与反序列化。
- 用 `workflow.as_agent("ZavaFulfillment")` 把整个 workflow 暴露成 `Agent` 接口。
- 流式消费 `executor_invoked / executor_completed / request_info / superstep_completed / output` 事件 —— 只能用 `event.executor_id`；`event.source_executor_id` 只在 `event.type == "request_info"` 时才安全。

---

## Microsoft Learn 参考

- [Agent Framework — Workflows overview](https://learn.microsoft.com/en-us/agent-framework/workflows/index)
- [Workflows — Executors](https://learn.microsoft.com/en-us/agent-framework/workflows/executors)
- [Workflows — Edges（fan-out / fan-in / 条件）](https://learn.microsoft.com/en-us/agent-framework/workflows/edges)
- [Workflows — WorkflowBuilder & execution（events / streaming / checkpoint / request_info）](https://learn.microsoft.com/en-us/agent-framework/workflows/workflows)
- [Foundry — Agent development lifecycle](https://learn.microsoft.com/en-us/azure/foundry/agents/concepts/development-lifecycle)

> SKILL 入口：[`agent-framework-workflows-py/SKILL.md`](../../.github/skills/agent-framework-workflows-py/SKILL.md)

---

## 任务清单

### Step 1 — 在 Agent Mode 里选中 ZavaShop Coding Agent

在 VS Code Copilot Chat 切到 **Agent Mode**，打开 Agent 选择器，选中 **`zavashop-coding-agent`**，然后发送一条同时点明 LAB 编号和 **使用的编程语言** 的消息：

```
I'm doing LAB 4 in Python — build the ZavaShop fulfillment workflow with concurrent stock+shipping, HITL approval and checkpoint resume.
```

> 不要再用 `@zavashop-coding-agent` 这种写法 —— Coding Agent 是从下拉里选的，对话框里只写任务描述（含 LAB 号 + 语言）。

Coding Agent 会：

1. 加载 [`.github/skills/agent-framework-workflows-py/SKILL.md`](../../.github/skills/agent-framework-workflows-py/SKILL.md) 以及 `references/parallelism.md` / `references/human_in_the_loop.md` / `references/checkpointing.md` / `references/composition.md` 这几个子页。
2. 加载本 LAB README，以及 SKILL 里 [§Workshop‑verified gotchas](../../.github/skills/agent-framework-workflows-py/SKILL.md#workshop-verified-gotchas-lab-04) 那一段。
3. 在 [`workshop/LAB04-fulfillment-workflow/`](.) 下创建 `fulfillment_workflow.py`：
   ```
   intake ─┬► stock_check ─┐
           └► shipping_quote ┴► allocator ► approval (HITL) ► dispatch ► finance
   ```
   - 7 个节点全是确定性 Python executor —— `intake / approval` 是 `class … (Executor)` 子类，用 `@handler` + `@response_handler`；`stock_check / shipping_quote / allocator / dispatch / finance` 是 `@executor` 函数节点。
   - 该文件 **不要** 写 `from __future__ import annotations` —— 它会让 `@response_handler` 校验器读到 `WorkflowContext[...]` 的字符串注解，校验直接 raise。
   - `IntakeExecutor` 要暴露 **两个** `@handler`（`str` 和 `list[Message]`），这样同一个节点既能被 `workflow.run("ORD-…")` 直接调用，也能被 `workflow.as_agent(…).run("ORD-…")` 调用。
   - 并发用 `.add_fan_out_edges(intake, [stock_check, shipping_quote])` + `.add_fan_in_edges([stock_check, shipping_quote], allocator)` —— **不用** `ConcurrentBuilder`（那是给聊天 Agent 用的）。
   - `approval` 在总额 ≥ $1000 时调用 `ctx.request_info(request_data=ApprovalRequest, response_type=ApprovalResponse)`；用 `@response_handler` 恢复。
   - `FileCheckpointStorage(storage_path=".checkpoints", allowed_checkpoint_types=[...])` —— 把每个用户 dataclass 都按 `module:QualName` 形式列进 allow-list（`__main__` 和 `fulfillment_workflow` 两个 module 都要列），否则 resume 无法反序列化。
   - 文件末尾 `fulfillment_agent = workflow.as_agent("ZavaFulfillment")` 供 LAB 5 用。
4. 跑两个场景：< $1000 端到端跑完（场景 A）；≥ $1000 在 `approval` 暂停，用 **新建的 workflow 实例** 加 `responses={...}` 从 checkpoint 恢复（场景 B）。Coding Agent 不会跳过 `ctx.request_info`，也不会复用同一个已暂停的 `Workflow` 实例 —— 见下方 gotcha 列表。

> Coding Agent 不会跳过 `ctx.request_info` 或手火提交废除检查点 —— 这是 LAB 的核心考点。

### Step 2 — 定义 7 个节点

边上跑的是类型化 dataclass，不是裸 `str`。完整源码在 [`fulfillment_workflow.py`](./fulfillment_workflow.py)，下面只放骨架。

```python
# 重要：这个文件不要写 `from __future__ import annotations`。
# `@response_handler` 校验器会读 WorkflowContext[...] 的原始注解字符串；
# 延迟注解（deferred annotations）会让它解析不到泛型，直接 raise。

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

# … StockReport / FreightQuote / AllocationPlan / ApprovalRequest / ApprovalResponse / DispatchResult …


# 1. intake —— 类节点，暴露两个 @handler，让同一个节点同时支持
#    workflow.run("ORD-…") 和 workflow.as_agent().run("ORD-…")
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
        # as_agent() 永远派发 list[Message]，取最后一条 user 消息的文本即可
        await self._emit((messages[-1].text or "").strip(), ctx)


# 2 & 3. stock_check / shipping_quote —— 纯 @executor 函数；本 LAB 不需要
#         走 LLM，inventory.json + carriers.json 都是确定性的
@executor(id="stock_check")
async def stock_check(order: OrderRecord, ctx: WorkflowContext[LegResult]) -> None:
    rows = [find_stock(ln["sku"], order.ship_to_warehouse) for ln in order.lines]
    # …生成 StockReport，再 send_message(LegResult(kind="stock", …))
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


# 4. allocator —— fan-in,收到 list[LegResult]
@executor(id="allocator")
async def allocator(legs: list[LegResult], ctx: WorkflowContext[AllocationPlan]) -> None:
    stock_leg   = next(l for l in legs if l.kind == "stock")
    freight_leg = next(l for l in legs if l.kind == "freight")
    plan = AllocationPlan(order=stock_leg.order, stock=stock_leg.stock,
                          freight=freight_leg.freight,
                          total_usd=round(stock_leg.order.total_usd
                                          + freight_leg.freight.cheapest.price_usd, 2))
    await ctx.send_message(plan)


# 5. approval —— HITL 闸门;self._pending 通过 on_checkpoint_save/restore 持久化
class ApprovalExecutor(Executor):
    def __init__(self, id: str = "approval") -> None:
        super().__init__(id=id)
        self._pending: dict[str, Any] | None = None  # AllocationPlan 的 JSON-able 形式

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


# 6. dispatch + 7. finance —— @executor，发出仓单/财务凭证
@executor(id="dispatch")
async def dispatch(plan: AllocationPlan, ctx: WorkflowContext[DispatchResult]) -> None: ...

@executor(id="finance")
async def finance(result: DispatchResult,
                  ctx: WorkflowContext[Never, dict[str, Any]]) -> None:
    await ctx.yield_output({"status": "shipped", "order_id": result.order_id, ...})
```

> **查表，不 parse。** `intake` 拿订单号去 [`orders.json`](../data/orders.json) 查，不去 parse 客户的自然语言，下游节点才能保持确定性，同一份 fixture 也能服务 LAB 5。`from_messages` 这个 handler 只是为了让 `workflow.as_agent()`（它永远派发 `list[Message]`）也能走到同一个 `_emit`。

### Step 3 — 组装 + checkpoint + as_agent

```python
def build_workflow():
    CHECKPOINT_DIR.mkdir(parents=True, exist_ok=True)

    # FileCheckpointStorage 反序列化时做 type allow-list 校验。
    # 每个用户 dataclass 都得在 __main__（CLI 模式）和 'fulfillment_workflow'
    # （LAB 5 import 这个模块时）下都登记一遍。
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
            output_from=[finance, approval],  # finance 出货成功;approval 拒绝时
        )
        .add_fan_out_edges(intake, [stock_check, shipping_quote])  # 并行
        .add_fan_in_edges([stock_check, shipping_quote], allocator)  # 合流
        .add_edge(allocator, approval)
        .add_edge(approval, dispatch)
        .add_edge(dispatch, finance)
        .build()
    )
    return wf, storage


workflow, checkpoint_storage = build_workflow()
fulfillment_agent = workflow.as_agent("ZavaFulfillment")  # 给 LAB 5 用
```

### Step 4 — 跑两次：一次 < $1000，一次 ≥ $1000 触发 HITL

```python
# 场景 A —— ORD-20260524-001（$196.50）：不会出 request_info，finance 直接落地
async for event in workflow.run("ORD-20260524-001", stream=True, include_status_events=False):
    if event.type == "executor_invoked":
        print("  ►", event.executor_id)
    elif event.type == "output":
        print("  ★", event.data)        # {"status": "shipped", ...}

# 场景 B —— ORD-20260524-002（$1500 货 + $534.50 运费 = $2034.50）：在 approval 暂停
pending: dict[str, ApprovalRequest] = {}
async for event in workflow.run("ORD-20260524-002", stream=True, include_status_events=False):
    if event.type == "request_info":
        pending[event.request_id] = event.data
    # 重要：不要在这里 break。checkpoint 在每个 super-step 结束后才写盘，
    # 提前 break 会丢掉那个携带 pending 请求的 checkpoint —— 后续 resume 会报
    # "No pending requests found in workflow context"。
    # workflow 已经暂停，让 async for 自然走到尽头即可。
```

> 只用 `event.executor_id`，不要用 `event.source_executor_id` —— 后者是个 property，对除了 `"request_info"` 之外的事件类型都会直接抛 `RuntimeError`。

### Step 5 — 从 checkpoint 续跑

场景 B 的 stream 走完之后，**当前进程或者一个全新进程** 都可以新建一个 workflow 实例，把 `checkpoint_id` 和 `responses` 一起塞进 `run(...)`：

```python
latest = await checkpoint_storage.get_latest(workflow_name=workflow.name)
fresh_wf, _ = build_workflow()              # 必须是新的 Workflow 实例
responses = {
    req_id: ApprovalResponse(approved=True, reason="主管：放行")
    for req_id in pending
}
async for event in fresh_wf.run(checkpoint_id=latest.checkpoint_id,
                                responses=responses,
                                stream=True,
                                include_status_events=False):
    if event.type == "output":
        print("voucher:", event.data)       # → {"status": "shipped", ...}
```

> 原来那个 `workflow` 在 `request_info` 暂停之后仍然 `_is_running=True`，再次 `workflow.run(...)` 会 raise `Workflow is already running. Concurrent executions are not allowed.`。新建一个 `Workflow`（共用同样的 `.checkpoints/` 路径和 allow-list）就相当于一个新进程接管那份暂停状态。

### Workshop 验证过的 gotcha（写代码前必读）

下面 7 条都是在 LAB 4 的可工作实现里实测过的；环境是 `agent-framework 1.0.0rc3`：

1. **不要写 `from __future__ import annotations`。** `@response_handler` 的校验器（`_request_info_mixin.py`）会读 `WorkflowContext[X, Y]` 的原始注解，如果是字符串就 raise。这个文件请保持「注解立即解析」。
2. **`workflow.as_agent()` 永远派发 `list[Message]`** 到 start executor —— 它会 assert `is_type_compatible(list[Message], start.input_types)`。如果你真正的 intake 想接 `str`，就暴露 **两个** `@handler`（`str` + `list[Message]`），让它们共用一个私有 `_emit`。
3. **`FileCheckpointStorage` 是类型 allow-list 制。** 每个用户 dataclass 都要在 `allowed_checkpoint_types=["__main__:OrderRecord", "fulfillment_workflow:OrderRecord", …]` 里登记，否则 resume 会报 `Checkpoint deserialization blocked for type '…'`。
4. **`request_info` 之后把 stream 跑完。** checkpoint 在 super-step 结束时才会落盘。如果你一看到 `request_info` 就 `break`，那个携带 pending 请求的 checkpoint 还没写到磁盘 —— resume 会报 `No pending requests found in workflow context`。让 `async for` 自然结束就行，workflow 已经停下了。
5. **暂停中的 workflow 实例不能再 run。** 在 `request_info` 暂停后，原 `Workflow` 仍然 `_is_running=True`。要恢复的话，用 `build_workflow()` 新建一个实例，调用 `fresh_wf.run(checkpoint_id=..., responses=...)`。
6. **`event.source_executor_id` 对非 `request_info` 事件会 raise** —— 全程统一用 `event.executor_id`。
7. **不要用 `ConcurrentBuilder` 包类型化 executor。** `ConcurrentBuilder` 把 user prompt 分发给聊天 Agent 并聚合 `list[Message]`；LAB 4 是把类型化的 `OrderRecord` 扇给两个 executor，再以 `list[LegResult]` 合流 —— 用 `.add_fan_out_edges` + `.add_fan_in_edges` 即可。

---

## 验收标准

- [ ] 控制台事件流里能看到 stock_check / shipping_quote **同时** 进入 `executor_invoked`（说明并发）。
- [ ] 场景 A（`ORD-20260524-001`, $196.50）一气跑完，全程不出现 `request_info`。
- [ ] 场景 B（`ORD-20260524-002`, $1500）一定会暂停在 `approval`，事件类型为 `request_info`。
- [ ] 审批回填后，dispatch + finance 继续执行，最终 `yield_output` 出 `{"status": "shipped", ...}`。
- [ ] checkpoint 目录下生成多个 superstep 文件；断点续跑能从中点恢复，没有重复扣库存。
- [ ] `fulfillment_agent = workflow.as_agent()` 可被外部代码当作普通 Agent 调用（拿到一段总结性回复）。
- [ ] `intake` 通过 `find_order` 解析订单（不做自由文本 parse）；`quote_freight` 返回的行由 `carriers.json` 生成，承运商 ID 能在 `FEDEX` / `DHL` / `USPS` / `ARAMEX` / `SFEXPRESS` 中抓到。

---

## .NET 实现赛道

同一个 DAG、同一个 HITL 闸门、同一个 checkpoint 断点续跑。

### Step 1 — 在 Agent Mode 里选中 ZavaShop Coding Agent（C#）

在 VS Code Copilot Chat → **Agent Mode** → Agent 选择器 → **`zavashop-coding-agent`**，然后发送：

```
I'm doing LAB 4 in C# — build the order-fulfillment workflow with HITL approval and checkpoint resume.
```

会在 [`workshop/LAB04-fulfillment-workflow/`](.) 下创建 `FulfillmentWorkflow/`，link `..\..\data\ZavaData.cs`，依赖（全部使用 `Version="*-*"`）：`Microsoft.Agents.AI`、`Microsoft.Agents.AI.Workflows`、`Microsoft.Extensions.AI`、`Azure.Identity`。`.csproj` 里还要加 `<NoWarn>$(NoWarn);NU1604;NU1902;MAAI001</NoWarn>`，否则预览版的通配符依赖和 `[Experimental]` 标记会把编译警告卡死。和 LAB 1 – LAB 3 不同，本 LAB **不调用任何 Foundry Agent** —— 七个节点都是确定性 .NET executor —— 所以不需要 `Microsoft.Agents.AI.Foundry` 与 `Azure.AI.Projects`。

> .NET 赛道当前安装的是 **`Microsoft.Agents.AI.Workflows` 1.7.0**（2025 年 11 月通过 `Version="*-*"` 解析的版本）。以下 API 已在该版本验证。如果你的环境探测出的结果与本文不符，以探测为准，并阅读 [`.github/skills/agent-framework-workflows-csharp/SKILL.md`](../../.github/skills/agent-framework-workflows-csharp/SKILL.md) 底部的 **Workshop-verified gotchas (LAB 04)** 一节。

### Step 2 — 用 `WorkflowBuilder` 搭强类型 DAG

每一个节点都是独立的 `Executor<TIn, TOut>`（或者只走 `IWorkflowContext` 的 `Executor<TIn>`），用 fan-out / fan-in barrier / 条件边拼起来：

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
    // 条件边：≥ $1000 走 HITL，< $1000 直接分单。
    .AddEdge<AllocationPlan?>(allocator, approvalBuilder,
        condition: msg => msg is AllocationPlan plan && plan.TotalUsd >= 1000m)
    .AddEdge<AllocationPlan?>(allocator, dispatch,
        condition: msg => msg is AllocationPlan plan && plan.TotalUsd <  1000m)
    .AddEdge(approvalBuilder, approvalPort)
    .AddEdge(approvalPort,    approvalResume)
    .AddEdge<AllocationPlan>(approvalResume, dispatch)   // 已审批 → 继续派单
    .AddEdge(dispatch, finance)
    .WithOutputFrom(finance, approvalResume)             // 发货 / 拒绝两条出口
    .WithName("ZavaFulfillment")
    .WithDescription("Order intake → stock + freight → HITL gate → dispatch → finance.")
    .Build();
```

这一段里有三个 SKILL 主文档里没有强调的 .NET 专属坑（详见 C# SKILL 底部的 gotchas 章节）：

- **fan-in barrier 是逐条投递**，不会自动打包成 `List<LegResult>` —— 见 C# SKILL 的 gotcha #1。`AllocatorExecutor` 用实例字段 buffer，凑齐两条腿前一直返回 `null`；下游再用条件边 `msg is AllocationPlan plan` 过滤掉 `null` 哨兵。
- **HITL 用 `RequestPort`**，不是不存在的 `ctx.RequestInfoAsync(...)` 调用。`ApprovalRequestBuilderExecutor` 把计划写进 shared state（scope `"Approval"`、key `"pending_plan"`），再把 `HumanApprovalRequest` 转发给 port；resume executor 在审批回来后从 shared state 读回计划。
- **`ApprovalResumeExecutor` 是 `Executor<HumanApprovalResponse>`（没有 `TOut`）** —— 既要 `SendMessageAsync(plan)` 给派单（通过），也要 `YieldOutputAsync(rejected)` 给外层（拒绝）。两种行为必须在 `ConfigureProtocol` 里同时声明（C# SKILL 的 gotcha #3）。

### Step 3 — 接持久化 checkpoint + HITL 事件循环

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
            bool decision = PromptOperator(ask);   // LAB 里就是 Console.ReadLine
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

// 续跑 —— 全新 run、同一个 manager、没有 sessionId：
await using StreamingRun resumed = await InProcessExecution.ResumeStreamingAsync(
    workflow, lastCheckpoint!, manager, CancellationToken.None);
```

提醒：
- 流式 API 是 `RunStreamingAsync` / `ResumeStreamingAsync`（不是 `StreamAsync` / `ResumeStreamAsync`）；`Checkpointed<TRun>` 不存在 —— 调用返回的是裸 `StreamingRun`（C# SKILL gotcha #2）。
- 一定要处理 `ExecutorFailedEvent` 和 `WorkflowErrorEvent`：executor 抛出的异常会变成事件，不会从 `WatchStreamAsync` 抛出来。
- 取出 request payload 只能用 `request.TryGetDataAs<T>(out T)`；回执用 `request.CreateResponse(value)` 构造（gotcha #4）。

### Step 4 — 每个 executor 都走 `ZavaData`

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

不允许内联 carrier 表 —— 所有数据都必须通过 `ZavaData.LoadCarriers()` / `ZavaData.FindOrder(id)` / `ZavaData.FindStock(sku, warehouse)` 来自 `carriers.json` / `orders.json` / `inventory.json`，这样 carrier id 才能跟 `FEDEX` / `DHL` / `USPS` / `ARAMEX` / `SFEXPRESS` 精确对上。库存只在 `DispatchExecutor`（审批通过之后）扣减一次。

### Step 5 — 把整个 workflow 包成一个 `AIAgent`

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

扩展方法是 **`AsAIAgent`**（不是 `AsAgent`）—— C# SKILL gotcha #5。和 Python 不同，.NET 这边不要求 start executor 接 `list<ChatMessage>` —— `string` 订单号就够了。这个包装后的 `AIAgent` 可以直接喂给 LAB 5 当子 Agent。

### Step 6 — 运行

```bash
# 自动审批路径（< $1,000，全自动跑完，不弹控制台）
dotnet run --project workshop/LAB04-fulfillment-workflow/FulfillmentWorkflow -- ORD-20260524-001

# HITL 路径（> $1,000，会在控制台问你 approve / reject）
dotnet run --project workshop/LAB04-fulfillment-workflow/FulfillmentWorkflow -- ORD-20260524-002

# 默认模式 = 跑完两个场景 + 一次断点续跑演示，且不需要交互
LAB04_AUTO_APPROVE=yes dotnet run --project workshop/LAB04-fulfillment-workflow/FulfillmentWorkflow
```

`LAB04_AUTO_APPROVE=yes` 会跳过 `Console.ReadLine`，自动回 "approve"，适合 CI / 快速复跑。无参数的默认运行会依次跑场景 A → 场景 B → 用场景 B 期间捕获的 **最后一个 `SuperStepCompletedEvent.CompletionInfo.Checkpoint`** 驱动续跑。每次跑都会在 `_checkpoints/` 目录下按 super-step 写 JSON 文件，并在 `_checkpoints/index.jsonl` 追加一行；清空该目录即可重置状态。验收标准与上文保持一致 —— .NET 赛道全部七条验收都靠本节中的 executor 与事件循环完成。

### Workshop-verified gotchas（.NET）

- **fan-in barrier 是逐条投递**，并不会自动打包成 `List<TOut>`（对强类型 executor）—— 用 Step 2 里的"实例 buffer + null 哨兵 + 条件边"组合。
- **多出口 executor 必须 override `ConfigureProtocol`**，同时声明 `SendsMessageType` + `YieldsOutputType`；只贴 attribute 只能注册一种行为。
- **把 workflow 包装成 agent 的扩展方法是 `AsAIAgent`**（不是 `AsAgent`）。
- **`ResumeStreamingAsync` 是四参数** —— `(workflow, checkpoint, manager, ct)` —— 没有 `sessionId`。`Checkpointed<TRun>` 不存在。
- **持久化 checkpoint** 用 `FileSystemJsonCheckpointStore` + `CheckpointManager.CreateJson(store, null)` —— .NET 里没有 `FileCheckpointStorage` 这个类型（那是 Python 的 API）。
- **`<NoWarn>$(NoWarn);NU1604;NU1902;MAAI001</NoWarn>`** 必须加进 `.csproj`，否则通配符版本号 + `[Experimental]` 标记会把这三条警告全部点亮。

完整列表（带代码片段）见 [`agent-framework-workflows-csharp/SKILL.md`](../../.github/skills/agent-framework-workflows-csharp/SKILL.md#workshop-verified-gotchas-lab-04) 底部。

---

## 故事收尾

ZavaShop 内部五个 Agent 已经合奏完毕，但 COO 拍桌：

> *"这些都在你们工程师的 Python 控制台里，运营经理看不见！我要一个 **指挥中心** —— 网页里实时看 Agent 在做什么，每个节点能展开看输入输出，主管在 UI 上点 ✓ 就放行，紧急情况下能塞一段话给 Agent。"*

—— 这是 [LAB 5](../LAB05-control-tower-agui/README.md) 的开始。
