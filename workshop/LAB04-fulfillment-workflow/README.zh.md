# LAB 4 — 订单履约调度：多 Agent Workflow + HITL + Checkpoint

> **由 SKILL 协助**：[`agent-framework-workflows-py`](../../.github/skills/agent-framework-workflows-py/SKILL.md)
> **Foundry 模型**：`gpt-5.5`

---

## 故事

ZavaShop 的「下单到出仓」流程长这样：

```
新订单 ──► 库存检查 ──► 仓位分配 ──► 物流报价 ──► (≥1000$ 需主管放行) ──► 出仓通知 ──► 财务记账
```

5 个角色每个都是一个独立 Agent，跑在 `gpt-5.5` 上。要求：

1. **确定性编排**：用 `WorkflowBuilder` 把 5 个 Agent 串成一张图，事件可追踪。
2. **fan-out / fan-in**：库存检查 + 物流报价可以**并发**跑（用 `ConcurrentBuilder` 或自定义 fan-out）。
3. **HITL**：金额 ≥ 1000 USD 时用 `ctx.request_info(...)` 暂停，等主管回复 yes/no。
4. **Checkpoint**：每个 super-step 自动落 checkpoint，**断电重跑**可以 `workflow.run(checkpoint_id=...)` 恢复。
5. **可包装**：整张工作流最终用 `workflow.as_agent()` 包成一个对外 Agent，供 LAB 5 的指挥中心调用。

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

- 用 `Executor` + `@handler` 写自定义节点；用 `Agent` 直接作为节点（agent-as-executor）。
- 用 `WorkflowBuilder` 构图：`start_executor` + `add_edge` + `output_from`。
- 用 `ConcurrentBuilder` 或显式 fan-out 让 *库存检查* 与 *物流报价* 并行，并在 *仓位分配* 处 fan-in。
- 用 `ctx.request_info(...)` 做 HITL；外部用 `workflow.run(responses={...})` 回填。
- 用 `checkpoint_storage=...` + `@step` 缓存让 workflow **可断点续跑**。
- 用 `workflow.as_agent(...)` 把整个 workflow 暴露成 `Agent` 接口。
- 流式消费 `executor_invoked / executor_completed / request_info / superstep_completed / output` 事件。

---

## 对齐 Microsoft Learn

- *Agent Framework — Workflows: graph & functional authoring*
- *Executors, Handlers, WorkflowContext*
- *Orchestration builders (Sequential / Concurrent / Handoff / GroupChat / Magentic)*
- *Human-in-the-loop with `ctx.request_info`*
- *Checkpointing & resume*
- *`workflow.as_agent()` 与子工作流*

> SKILL 入口：[`agent-framework-workflows-py/SKILL.md`](../../.github/skills/agent-framework-workflows-py/SKILL.md)

---

## 任务清单

### Step 1 — 调用 ZavaShop Coding Agent

```
@zavashop-coding-agent I'm doing LAB 4 — build the ZavaShop fulfillment workflow with concurrent stock+shipping, HITL approval and checkpoint resume.
```

Coding Agent 会：

1. 加载 [`.github/skills/agent-framework-workflows-py/SKILL.md`](../../.github/skills/agent-framework-workflows-py/SKILL.md)。
2. 加载本 LAB README；顺手 `search` 一下 LAB 1 的 `get_stock` 是否可复用。
3. 在 [`workshop/LAB04-fulfillment-workflow/`](.) 下创建 `fulfillment_workflow.py`：
   ```
   intake ─┬► stock_check ─┐
          └► shipping_quote ┴► allocator ► approval (HITL) ► dispatch ► finance
   ```
   - `intake / allocator / dispatch / finance` 用 `@executor` 函数节点
   - `stock_check / shipping_quote / approval` 用 `Agent` (FoundryChatClient + gpt-5.5)
   - `ConcurrentBuilder` 实现 stock+shipping 并行
   - `approval` 节点 ≥ 1000 USD 调用 `ctx.request_info(...)`
   - `FileCheckpointStorage(".checkpoints")`
   - 最后 `workflow.as_agent(name="ZavaFulfillment")` 供 LAB 5 使用
4. 跑两个场景：< 1000 USD 一气呾成；≥ 1000 USD 暂停后手动回填 `responses={...}` 恢复。再补一个 Ctrl+C + `checkpoint_id` 续跑场景。

> Coding Agent 不会跳过 `ctx.request_info` 或手火提交废除检查点 —— 这是 LAB 的核心考点。

### Step 2 — 定义五个节点

```python
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "data"))
from zava_data import find_order, find_stock, load_carriers

# 1. intake：拿订单号，在 orders.json 里查出 OrderDraft
@executor(id="intake")
async def intake(order_id: str, ctx: WorkflowContext[OrderDraft]) -> None:
    raw = find_order(order_id)
    if raw is None:
        raise ValueError(f"Unknown order {order_id}")
    order = OrderDraft(**raw)              # 不要 parse 自由文本 — 走 fixture
    await ctx.send_message(order)

# 2. stock_agent 工具：复用 LAB 1 对 find_stock 的封装
def get_stock(sku: str, warehouse: str) -> str:
    row = find_stock(sku, warehouse)
    return "ok" if row and row["on_hand"] >= 1 else "short"

# 3. shipping_agent 工具：从 carriers.json 拉取承运商
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

# 2 & 3：stock_check / shipping_quote 用 Agent
stock_agent = Agent(
    client=FoundryChatClient(project_endpoint=..., model="gpt-5.5", credential=...),
    name="StockChecker",
    instructions="用 get_stock 工具确认订单中每个 SKU 是否可发；输出 JSON {sku: ok|short}",
    tools=[get_stock],
)
shipping_agent = Agent(
    ...,
    name="ShippingQuoter",
    instructions="用 quote_freight 工具给出 3 个承运商报价，输出 JSON",
    tools=[quote_freight],
)

# 4. allocator：把库存 + 运价 merge 成 AllocationPlan
@executor(id="allocator")
async def allocator(parts: list, ctx: WorkflowContext[AllocationPlan]) -> None:
    plan = merge(parts)
    await ctx.send_message(plan)

# 5. approval：HITL
@executor(id="approval")
async def approval(plan: AllocationPlan, ctx: WorkflowContext[AllocationPlan]) -> None:
    if plan.total_usd >= 1000:
        decision = await ctx.request_info(
            prompt=f"订单总金额 ${plan.total_usd}，是否放行？",
            request_type="supervisor_yes_no",
        )
        if not decision["approved"]:
            await ctx.yield_output({"status": "rejected", "reason": decision.get("reason")})
            return
    await ctx.send_message(plan)

# 6. dispatch + 7. finance：@executor，分别发出仓单 / 财务凭证
```

> **查表，不 parse。** intake 节点只拿订单号去 [`orders.json`](../data/orders.json) 查，不去 parse 客户的自然语言句子，下游节点才能保持确定性，同一份 fixture 也能服务 LAB 5。

### Step 3 — 组装 + checkpoint + as_agent

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

# 暴露成 Agent，给 LAB 5 用
fulfillment_agent = workflow.as_agent(name="ZavaFulfillment")
```

### Step 4 — 跑两次：一次 < 1000，一次 ≥ 1000 触发 HITL

```python
# 场景 A：ORD-20260524-001 总额 $196.50 — 全流程跑完，不走 HITL
await workflow.run("ORD-20260524-001")

# 场景 B：ORD-20260524-002 总额 $1500.00 — 触发 request_info
run = workflow.run_stream("ORD-20260524-002")
async for event in run:
    if event.kind == "request_info":
        request_id, prompt = event.request_id, event.prompt
        print("主管审批：", prompt)
        # 模拟审批通过
        await workflow.run(responses={request_id: {"approved": True}})
```

### Step 5 — 断点续跑

故意 `Ctrl+C` 第二次执行；记录 `checkpoint_id`；再用 `workflow.run(checkpoint_id=...)` 恢复，确认从中断点继续而非从头跑。

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

## 故事收尾

ZavaShop 内部五个 Agent 已经合奏完毕，但 COO 拍桌：

> *"这些都在你们工程师的 Python 控制台里，运营经理看不见！我要一个 **指挥中心** —— 网页里实时看 Agent 在做什么，每个节点能展开看输入输出，主管在 UI 上点 ✓ 就放行，紧急情况下能塞一段话给 Agent。"*

—— 这是 [LAB 5](../LAB05-control-tower-agui/README.md) 的开始。
