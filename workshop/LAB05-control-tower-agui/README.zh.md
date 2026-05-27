# LAB 5 — 供应链指挥中心：AG-UI 前端 + 共享状态 + 生成式 UI

> **由 SKILL 协助**（任选一个赛道）：
> - Python：[`agent-framework-agui-py`](../../.github/skills/agent-framework-agui-py/SKILL.md)
> - .NET（C#）：[`agent-framework-agui-csharp`](../../.github/skills/agent-framework-agui-csharp/SKILL.md)
>
> **Foundry 模型**：`gpt-5.5`

---

## 选择你的技术栈

| 赛道 | 交付物 | 需要加载的 Skill | 数据 helper |
|------|--------|---------------------|---------------|
| 🐍 **Python** | `server.py` + `client_smoketest.py` | [`agent-framework-agui-py/SKILL.md`](../../.github/skills/agent-framework-agui-py/SKILL.md) | [`zava_data.py`](../data/zava_data.py) |
| 🟦 **.NET（C#）** | `ControlTower/`（ASP.NET Core） + `ControlTowerSmoke/` | [`agent-framework-agui-csharp/SKILL.md`](../../.github/skills/agent-framework-agui-csharp/SKILL.md) | [`ZavaData.cs`](../data/ZavaData.cs) |

Python 赛道在 [§任务清单](#任务清单)；.NET 赛道在 [§.NET 实现赛道](#net-实现赛道)。两个赛道都监听 `http://127.0.0.1:5100/`、都用 `X-API-Key: zava-control-tower-demo-key`、背后都是同一份 `exceptions.json` / `carriers.json` / `orders.json`。

---

## 故事

把 LAB 4 的 `ZavaFulfillment` workflow 包成 **AG-UI HTTP 服务**，运营经理通过浏览器实时操控：

| 能力 | 体现在指挥中心的哪里 |
|------|----------------------|
| **后端聊天** | 经理直接和履约 Agent 对话："ORD-002 现在到哪一步了？" |
| **后端工具渲染** | 每次工具调用（库存 / 物流 / 财务）在 UI 上展开成卡片 |
| **HITL** | LAB 4 的 `request_info("审批")` 自动变成 UI 上的 ✓/✗ 按钮 |
| **生成式 UI** | "异常订单"列表在右栏作为共享状态实时更新 |
| **工具型 UI** | 物流报价工具用 `state_update(...)` 返回 JSON，前端渲染成对比卡片 |
| **客户端工具** | 前端自带 `play_alert_sound`、`open_order_drawer`，由 Agent 触发 |
| **预测式状态** | "今日履约 KPI" 数字随事件流刷新，不需 polling |

### 本 LAB 读取哪些数据

指挥中心复用整个 workshop 的共享 fixture（[`workshop/data/`](../data/README.zh.md)）：

- [`exceptions.json`](../data/exceptions.json) — 4 条现场异常。`list_exceptions` 要原样返回这些行。高优先的那条（`EXC-20260525-A` — VIP_002 Hiroshi Tanaka 的 `ORD-20260525-009`，`OUT_OF_STOCK`）是触发 `play_alert_sound` 的那一条。
- [`carriers.json`](../data/carriers.json) — 与 LAB 4 共用的 5 家承运商；`quote_freight` 从这里拼 `FreightCompareCard` 负载，承运商 ID 需能在 `FEDEX` / `DHL` / `USPS` / `ARAMEX` / `SFEXPRESS` 中抓到。
- [`orders.json`](../data/orders.json) — `ORD-20260525-009`（报警那个异常订单）、`ORD-20260524-002`（复用 LAB 4 的 HITL 路径）。

---

## 学习目标

- 用 `add_agent_framework_fastapi_endpoint(app, target, "/")` 把 `Workflow` / `Agent` 暴露成 AG-UI SSE 端点。
- 用 `AGUIChatClient` 写一个 Python 端的客户端做冒烟测试。
- 实现 **混合工具**：服务端工具（履约相关）+ 客户端工具（声音/抽屉）。
- 用 `state_update(text=..., tool_result=..., state=...)` 让一个工具同时喂模型 / 渲染 UI / 写共享状态。
- 用 `AgentFrameworkAgent` + `state_schema` + `predict_state_config` 做共享状态 + 预测式更新。
- 用 `X-API-Key` + FastAPI `Depends(...)` 给 AG-UI 端点加最低安全。
- 把 LAB 1~4 拼成 ZavaShop 完整 demo 的最后一公里。

---

## Microsoft Learn 参考

- [Agent Framework — AG-UI integration（7 个协议特性）](https://learn.microsoft.com/en-us/agent-framework/integrations/ag-ui)
- [Agent Framework — Integrations 索引（UI frameworks）](https://learn.microsoft.com/en-us/agent-framework/integrations/index)
- [Agent Framework — Workflows overview（把 workflow 包成 AG-UI 端点）](https://learn.microsoft.com/en-us/agent-framework/workflows/index)
- [Foundry — Agent development lifecycle（发布与监控）](https://learn.microsoft.com/en-us/azure/foundry/agents/concepts/development-lifecycle)

> SKILL 入口：[`agent-framework-agui-py/SKILL.md`](../../.github/skills/agent-framework-agui-py/SKILL.md)

---

## 任务清单

### Step 1 — 在 Agent Mode 里选中 ZavaShop Coding Agent

在 VS Code Copilot Chat 切到 **Agent Mode**，打开 Agent 选择器，选中 **`zavashop-coding-agent`**，然后发送一条同时点明 LAB 编号和 **使用的编程语言** 的消息：

```
I'm doing LAB 5 in Python — wrap the LAB 4 fulfillment workflow as an AG-UI endpoint and build the Control Tower smoketest client.
```

> 不要再用 `@zavashop-coding-agent` 这种写法 —— Coding Agent 是从下拉里选的，对话框里只写任务描述（含 LAB 号 + 语言）。

Coding Agent 会：

1. 加载 [`.github/skills/agent-framework-agui-py/SKILL.md`](../../.github/skills/agent-framework-agui-py/SKILL.md)，重点 "Core Workflow" + "The seven AG-UI features"。
2. 加载本 LAB README + `search` LAB 4 的 `fulfillment_agent` 是否可直接 `import`。
3. 在 [`workshop/LAB05-control-tower-agui/`](.) 下创建：
   - `server.py`：FastAPI + `add_agent_framework_fastapi_endpoint(app, AgentFrameworkAgent(control_tower), "/")` + `X-API-Key` 依赖 + **3 个服务端工具**（`list_exceptions` / `quote_freight` / `fulfill_order` —— `fulfill_order` 对低于阈值的订单委托给 LAB 4 的 `fulfillment_agent`，对超阈值订单返回 `ApprovalDialog` `tool_result`）。
   - `client_smoketest.py`：`AGUIChatClient` 跑 **5 段对话**，覆盖全部 7 个 AG-UI 特性（异常清单 + 客户端 `play_alert_sound` → 运费报价 → 自动履约 → HITL 待审批 → HITL 通过）。
   - `frontend/README.md`：7 个 AG-UI 特性 → 7 个 React 组件的映射表。
4. `runCommands` 起 `uvicorn server:app --host 127.0.0.1 --port 5100`，另开一个进程跑 `client_smoketest.py`，贴输出。

> Coding Agent 不会看你寫 React（不在 SKILL 范围），但会把后端与接口及完整的 7 特性映射文档交付完毕。

### Step 2 — `server.py`

```python
import os
import sys
from pathlib import Path
from typing import Any

from agent_framework import Agent, tool
from agent_framework.ag_ui import (
    AgentFrameworkAgent,
    add_agent_framework_fastapi_endpoint,
    state_update,
)
from agent_framework.foundry import FoundryChatClient
from azure.identity.aio import AzureCliCredential
from fastapi import Depends, FastAPI, HTTPException, Security
from fastapi.security import APIKeyHeader

_HERE = Path(__file__).resolve().parent
_WORKSHOP = _HERE.parent
sys.path.insert(0, str(_WORKSHOP / "data"))
sys.path.insert(0, str(_WORKSHOP / "LAB04-fulfillment-workflow"))

from zava_data import find_order, load_carriers, load_exceptions  # noqa: E402
from fulfillment_workflow import fulfillment_agent                 # noqa: E402

HITL_THRESHOLD_USD = 1000.0

# ---------------------------------------------------------------------------
# 服务端工具（业务数据全部走 zava_data —— 禁止内联 mock）
# ---------------------------------------------------------------------------

@tool
async def list_exceptions() -> Any:
    """列出今天的异常订单。"""
    rows = list(load_exceptions())
    high = [r for r in rows if r.get("severity") == "high"]
    return state_update(
        text=f"共 {len(rows)} 条异常，其中 {len(high)} 条高优先级。",
        tool_result={"component": "ExceptionsPanel", "exceptions": rows},
        state={"exceptions": rows, "high_severity_open": len(high)},
    )


def _lane_match(carrier: dict, origin: str, destination: str) -> bool:
    lanes = carrier.get("lanes", [])
    o, d = origin.upper(), destination.upper()
    if o == d:
        return f"{o}-domestic" in lanes
    return f"{o}-{d}" in lanes or f"{d}-{o}" in lanes


@tool
async def quote_freight(origin: str, destination: str, weight_kg: float) -> Any:
    """按区域对（US / EU / APAC / META）给出运费报价。carrier_id 直接从 carriers.json 取。"""
    quotes = [
        {
            "carrier": c["carrier_id"],
            "name": c["name"],
            "price_usd": round(c["base_usd"] + c["per_kg_usd"] * weight_kg, 2),
            "transit_days": c["transit_days_typical"],
        }
        for c in load_carriers()
        if _lane_match(c, origin, destination)
    ]
    quotes.sort(key=lambda q: q["price_usd"])
    quotes = quotes[:3]
    return state_update(
        text=f"已为 {origin.upper()}→{destination.upper()} 找到 {len(quotes)} 个报价。",
        tool_result={
            "component": "FreightCompareCard",
            "origin": origin.upper(),
            "destination": destination.upper(),
            "quotes": quotes,
        },
        state={"last_freight_quote": quotes},
    )


@tool
async def fulfill_order(order_id: str, supervisor_approval: bool = False) -> Any:
    """驱动 LAB 4 履约 workflow。$1000 以上 = HITL，回填 supervisor_approval=True 解除。"""
    order = find_order(order_id)
    if order is None:
        return state_update(
            text=f"未知订单 {order_id}。",
            tool_result={"component": "FulfillmentResult", "status": "unknown", "order_id": order_id},
            state={"last_fulfillment": {"order_id": order_id, "status": "unknown"}},
        )
    total_usd = float(order.get("total_usd", 0.0))
    if total_usd >= HITL_THRESHOLD_USD and not supervisor_approval:
        return state_update(
            text=(
                f"订单 {order_id} 总额 ${total_usd:,.2f}，超过 "
                f"${HITL_THRESHOLD_USD:,.0f} 的 HITL 阈值，等待主管审批。"
            ),
            tool_result={
                "component": "ApprovalDialog",
                "order_id": order_id,
                "total_usd": total_usd,
                "reason": "amount_over_threshold",
            },
            state={"pending_approval": {"order_id": order_id, "total_usd": total_usd}},
        )
    if supervisor_approval:
        summary = f"{order_id} 已获主管审批，凭证已开具。"
    else:
        result = await fulfillment_agent.run(order_id)
        summary = getattr(result, "text", "") or f"{order_id} 已发运。"
    voucher = {"status": "shipped", "order_id": order_id, "summary": summary}
    return state_update(
        text=f"已完成 {order_id}：shipped。",
        tool_result={"component": "FulfillmentResult", **voucher},
        state={
            "last_fulfillment": voucher,
            "today_kpi": {"orders_dispatched_today": 1, "last_order": order_id},
        },
    )


# ---------------------------------------------------------------------------
# AG-UI 应用 —— 包装的是新的 Foundry 后端 Agent，不是 LAB 4 的 fulfillment_agent
# ---------------------------------------------------------------------------

CONTROL_TOWER_INSTRUCTIONS = (
    "你是 ZavaControlTower，ZavaShop 供应链运营控制台。运营经理通过你监控异常、"
    "查询运费、调度订单。只能用工具回答，禁止编造数据。当 list_exceptions 返回"
    "任何 high-severity 行时，立即调用客户端工具 play_alert_sound(level='high')。"
    "对 fulfill_order，订单总额 ≥ $1000 时先弹出 ApprovalDialog，得到操作员确认"
    "后再用 supervisor_approval=True 重试。"
)

API_KEY_HEADER = APIKeyHeader(name="X-API-Key", auto_error=False)

async def verify_api_key(api_key: str | None = Security(API_KEY_HEADER)) -> None:
    expected = os.environ.get("AG_UI_API_KEY")
    if not expected:
        return  # dev 模式：警告但不拦截
    if not api_key:
        raise HTTPException(status_code=401, detail="Missing API key.")
    if api_key != expected:
        raise HTTPException(status_code=403, detail="Invalid API key.")

# AzureCliCredential 是长生命周期（每次 token 时才 shell 调 az），
# 在模块加载时构造一次即可，无需绑到请求级 async-with。
_credential = AzureCliCredential()
control_tower = Agent(
    client=FoundryChatClient(
        project_endpoint=os.environ["FOUNDRY_PROJECT_ENDPOINT"],
        model=os.environ["FOUNDRY_MODEL"],
        credential=_credential,
    ),
    name="ZavaControlTower",
    instructions=CONTROL_TOWER_INSTRUCTIONS,
    tools=[list_exceptions, quote_freight, fulfill_order],
)

af_agent = AgentFrameworkAgent(
    agent=control_tower,
    name="ZavaControlTower",
    description="ZavaShop 供应链指挥中心 (AG-UI)",
    state_schema={
        "type": "object",
        "properties": {
            "today_kpi": {"type": "object"},
            "last_freight_quote": {"type": "array"},
            "last_fulfillment": {"type": "object"},
            "pending_approval": {"type": "object"},
            "exceptions": {"type": "array"},
            "high_severity_open": {"type": "integer"},
        },
    },
    predict_state_config={
        "today_kpi": {"tool": "fulfill_order", "tool_argument": "order_id"},
    },
)

app = FastAPI(title="ZavaShop Control Tower")
add_agent_framework_fastapi_endpoint(
    app, af_agent, "/", dependencies=[Depends(verify_api_key)],
)
```

> **关键设计决定**（已对照已安装的 `agent-framework` 预发布版验证）：
> - **不要直接把 `fulfillment_agent` 包成 AG-UI Agent** —— AG-UI Agent 是新建的 `ZavaControlTower`，其 `fulfill_order` 工具对低阈值订单委托给 `fulfillment_agent.run(order_id)`，对超阈值订单走 `supervisor_approval=True` 的本地短路。要把 LAB 4 workflow 推过 `ctx.request_info` 网关需要 `Workflow.run(responses=...)` 串线，超出冒烟测试范围。
> - **禁止内联 mock。** `list_exceptions` / `quote_freight` / `fulfill_order` 全部走 `zava_data`（`load_exceptions` / `load_carriers` / `find_order`）。
> - **`carriers.lanes` 是「区域对」代码列表**：`"US-EU"` / `"US-domestic"` / `"EU-META"` 之类 —— 不是裸国家代码。`_lane_match` 处理双向匹配 + `domestic` 简写。
> - **`exceptions.json` 字段是 `severity`**（`high` / `medium` / `low`）—— 不是 `priority` / `status`。

启动：

```bash
export AG_UI_API_KEY=zava-control-tower-demo-key
uvicorn server:app --host 127.0.0.1 --port 5100
```

### Step 3 — `client_smoketest.py`

已安装的 `AGUIChatClient` **没有** `headers=` 参数。要带 `X-API-Key`，把 `httpx.AsyncClient(headers=...)` 当作 `http_client=` 注进去。多轮上下文走 `agent.create_session()`（已安装的预发布版没有 `agent.get_new_thread()`）。

```python
import asyncio
import os
import httpx
from agent_framework import Agent, tool
from agent_framework.ag_ui import AGUIChatClient

SERVER_URL = os.environ.get("AGUI_SERVER_URL", "http://127.0.0.1:5100/")
API_KEY = os.environ.get("AG_UI_API_KEY", "zava-control-tower-demo-key")


@tool
def play_alert_sound(level: str = "info") -> str:
    """客户端工具：真前端会触发声音/弹屉，这里只打印。"""
    bell = "🚨🚨🚨" if level == "high" else "🔔"
    print(f"\n  {bell} [client.play_alert_sound] level={level}")
    return f"alert_played:{level}"


async def _converse(agent: Agent, session, prompt: str, label: str) -> None:
    print(f"\n=== Turn {label} ===\nOperator: {prompt}\nControl Tower: ", end="", flush=True)
    async for chunk in agent.run(prompt, stream=True, session=session):
        if chunk.text:
            print(chunk.text, end="", flush=True)
    print()


async def main() -> None:
    async with httpx.AsyncClient(headers={"X-API-Key": API_KEY}, timeout=120.0) as http_client:
        async with AGUIChatClient(endpoint=SERVER_URL, http_client=http_client) as remote:
            async with Agent(
                name="control-tower-smoke",
                instructions="你是驱动 ZavaShop 指挥中心的运营经理。",
                client=remote,
                tools=[play_alert_sound],
            ) as agent:
                session = agent.create_session()
                # 1 — 异常清单 + 客户端报警
                await _converse(agent, session,
                    "当前有哪些异常订单？发现 high severity 的请用 level='high' 调 play_alert_sound。",
                    "1 list_exceptions + play_alert_sound")
                # 2 — 运费报价（tool-based UI + 共享状态）
                await _converse(agent, session,
                    "帮我报 US→EU 5kg 包裹的运费。",
                    "2 quote_freight")
                # 3 — 低阈值履约（驱动整条 LAB 4 workflow）
                await _converse(agent, session,
                    "请走履约：ORD-20260524-001（远低于审批阈值）。",
                    "3 fulfill_order (auto)")
                # 4 — HITL：超阈值履约
                await _converse(agent, session,
                    "现在请走 ORD-20260524-002（VIP_003，已被打标）。",
                    "4 fulfill_order (HITL: 等待审批)")
                # 5 — 主管已批准，supervisor_approval=True 重试
                await _converse(agent, session,
                    "主管已批准。请用 supervisor_approval=True 重新执行 ORD-20260524-002。",
                    "5 fulfill_order (HITL 解除)")

asyncio.run(main())
```

### Step 4 — `frontend/README.md`：对接 React 的索引

写一份 7 个 AG-UI 特性 → 7 个 UI 组件的映射表，例如：

| AG-UI 特性 | 后端做了什么 | 前端 React 组件 |
|-----------|--------------|-----------------|
| 1. Chat | 默认 SSE 流 | `<ChatStream/>` |
| 2. Backend tool rendering | tool 在 server 跑 | `<ToolCallCard/>` |
| 3. HITL | `ctx.request_info` | `<ApprovalDialog/>` |
| 4. Generative UI | tool 流式增量 `Content` | `<StreamingMarkdown/>` |
| 5. Tool-based UI | `state_update(tool_result={"component": ...})` | `<DynamicComponent/>` |
| 6. Shared state | `state_update(state={...})` | `<StatePanel/>` |
| 7. Predictive state | `predict_state_config` | `<KpiTicker/>` |

> 此处不要求真写 React 代码（不在 SKILL 范围），但要在 `frontend/README.md` 里清晰对齐。

---

## 验收标准

- [ ] `uvicorn` 启动后，`curl -H "X-API-Key: zava-control-tower-demo-key" http://127.0.0.1:5100/health` 返回 `200 OK`；缺 key → `401`；错 key → `403`。
- [ ] `client_smoketest.py` 第 1 轮拿到 **4 条异常**，原样来自 `exceptions.json`（含 `EXC-20260525-A` 对应 `ORD-20260525-009`），且 **控制台打印 🚨🚨🚨 `[client.play_alert_sound] level=high`**（服务端 LLM 检测到 high severity 后自动触发了客户端工具）。
- [ ] 第 2 轮（`quote_freight` US→EU @ 5kg）一次调用 **同时**：给模型一条「Found N freight quotes for US→EU」的摘要、shared state 写入 `last_freight_quote`、前端拿到 `tool_result.component == "FreightCompareCard"`。`carrier` 必须落在 `carriers.json` 的 `FEDEX` / `DHL` / `USPS` / `ARAMEX` / `SFEXPRESS` 之中。
- [ ] 第 3 轮（`ORD-20260524-001`，$196.50，低于阈值）整条 LAB 4 workflow 跑通 —— 服务端日志按 `[intake]` → `[stock_check]` → `[shipping_quote]` → `[allocator]` → `[approval] auto-approved` → `[dispatch]` → `[finance]` 打印，Agent 报 `shipped`。
- [ ] 第 4 轮（`ORD-20260524-002`，$1500）返回 `tool_result.component == "ApprovalDialog"`，shared state 出现 `pending_approval`，履约**不**继续。
- [ ] 第 5 轮带 `supervisor_approval=True` 重跑 `fulfill_order`，pending 状态清空，`today_kpi.last_order` 翻到 `ORD-20260524-002`。
- [ ] `frontend/README.md` 含完整的 7 特性 → 7 组件映射表。
- [ ] `server.py` 中 **没有任何内联 mock** —— 业务数据全部走 `zava_data`（`load_exceptions` / `load_carriers` / `find_order`）。

---

## .NET 实现赛道

同样七个 AG-UI 特性、同样的共享状态、同样的 API Key、同样的客户端烟雾脚本。

### Step 1 — 在 Agent Mode 里选中 ZavaShop Coding Agent（C#）

在 VS Code Copilot Chat → **Agent Mode** → Agent 选择器 → **`zavashop-coding-agent`**，然后发送：

```
I'm doing LAB 5 in C# — expose the LAB 4 fulfillment workflow over AG-UI so the control tower can drive it.
```

会在 [`workshop/LAB05-control-tower-agui/`](.) 下创建两个项目：

- [`ControlTower/`](./ControlTower/)：`Microsoft.NET.Sdk.Web` 的 ASP.NET Core 进程。承载一个 Foundry 后端的 `ZavaControlTower` `AIAgent`（3 个工具：`list_exceptions` / `quote_freight` / `fulfill_order`），通过 `X-API-Key` 中间件保护的 AG-UI 端点 `/` 暴露。**通过把 LAB 4 的 `Program.cs` 直接 link 进本程序集** 复用 LAB 4 的 `WorkflowFactory.Build(...)` 跑低阈值履约。
- [`ControlTowerSmoke/`](./ControlTowerSmoke/)：`Microsoft.NET.Sdk` 控制台，基于 `AGUIChatClient`。注册一个客户端 `play_alert_sound` 工具，跑与 Python `client_smoketest.py` 相同的 5 轮脚本。

两个 csproj 都 link [`..\..\data\ZavaData.cs`](../data/ZavaData.cs)；`ControlTower.csproj` **额外** link LAB 4 的 [`Program.cs`](../LAB04-fulfillment-workflow/FulfillmentWorkflow/Program.cs)。

### Step 2 — 通过 link 源文件 + 条件常量复用 LAB 4

LAB 4 是单文件结构：workflow 记录 + executor + `WorkflowFactory.Build` + CLI `Main` 全部在一个文件里。为了让 LAB 5 不出现两个 `Main`，LAB 4 **只把 CLI 块** 用预处理常量包起来：

```csharp
// workshop/LAB04-fulfillment-workflow/FulfillmentWorkflow/Program.cs
#if !LAB05_AGUI_HOST
internal static class Program { /* CLI 入口 */ }
#endif
```

LAB 5 的 `ControlTower.csproj` 定义该常量并 link 文件：

```xml
<Project Sdk="Microsoft.NET.Sdk.Web">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <RootNamespace>ZavaShop.LAB05.ControlTower</RootNamespace>
    <AssemblyName>ControlTower</AssemblyName>
    <DefineConstants>$(DefineConstants);LAB05_AGUI_HOST</DefineConstants>
    <NoWarn>$(NoWarn);NU1604;NU1902;MAAI001</NoWarn>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.Agents.AI" Version="*-*" />
    <PackageReference Include="Microsoft.Agents.AI.Foundry" Version="*-*" />
    <PackageReference Include="Microsoft.Agents.AI.Workflows" Version="*-*" />
    <PackageReference Include="Microsoft.Agents.AI.Hosting" Version="*-*" />
    <PackageReference Include="Microsoft.Agents.AI.Hosting.AGUI.AspNetCore" Version="*-*" />
    <PackageReference Include="Microsoft.Extensions.AI" Version="*-*" />
    <PackageReference Include="Azure.AI.Projects" Version="*-*" />
    <PackageReference Include="Azure.Identity" Version="*" />
  </ItemGroup>

  <ItemGroup>
    <Compile Include="..\..\data\ZavaData.cs" Link="ZavaData.cs" />
    <Compile Include="..\..\LAB04-fulfillment-workflow\FulfillmentWorkflow\Program.cs" Link="Lab04Workflow.cs" />
  </ItemGroup>
</Project>
```

效果：LAB 4 单独 `dotnet run` 仍然能跑 HITL demo，LAB 5 拿到 `WorkflowFactory.Build` / `ShippedVoucher` / 每一个 executor 记录 —— 全在同一个程序集里。无需另立类库、无需打包发包。

### Step 3 — `ControlTower/Program.cs`（Foundry agent + AG-UI host）

Agent 直接走 `AIProjectClient.AsAIAgent(...)`，3 个 function tool；AG-UI hosting 一行扩展方法搞定：

```csharp
using Azure.AI.Projects;
using Azure.Identity;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Hosting;
using Microsoft.Agents.AI.Hosting.AGUI.AspNetCore;
using Microsoft.Extensions.AI;
using ZavaShop.LAB04.FulfillmentWorkflow;     // WorkflowFactory + ShippedVoucher (linked 源)
using ZavaShop.Workshop.Data;                  // ZavaData

const string AgentName = "ZavaControlTower";

AIProjectClient project = new(new Uri(Environment.GetEnvironmentVariable("FOUNDRY_PROJECT_ENDPOINT")!),
                              new AzureCliCredential());

AIAgent controlTower = project.AsAIAgent(new ChatClientAgentOptions
{
    Name = AgentName,
    ChatOptions = new()
    {
        ModelId = Environment.GetEnvironmentVariable("FOUNDRY_MODEL")!,
        Instructions =
            "你是 ZavaControlTower，ZavaShop 供应链运营控制台。只能用工具回答，禁止编造。" +
            "当 list_exceptions 返回任何 high-severity 行时，立即调用客户端工具 " +
            "play_alert_sound(level=\"high\")。对 fulfill_order，订单总额 ≥ $1000 时先弹出 " +
            "ApprovalDialog payload，得到操作员确认后再用 supervisor_approval=true 重试。",
        Tools =
        [
            AIFunctionFactory.Create((Func<ExceptionsResult>)ListExceptions,
                name: "list_exceptions",
                description: "List today's open fulfillment exceptions from exceptions.json."),
            AIFunctionFactory.Create((Func<string, string, double, FreightQuoteResult>)QuoteFreight,
                name: "quote_freight",
                description: "Quote freight between two regions (US / EU / APAC / META) for a given weight in kg."),
            AIFunctionFactory.Create((Func<string, bool, Task<object>>)FulfillOrder,
                name: "fulfill_order",
                description: "Drive the LAB 4 fulfillment workflow. ≥$1000 requires supervisor_approval=true."),
        ],
    },
});

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
builder.Services.AddHttpClient().AddLogging();
builder.Services.AddAGUI();
builder.AddAIAgent(AgentName, (_, _) => controlTower).WithInMemorySessionStore();

WebApplication app = builder.Build();
app.UseMiddleware<ApiKeyMiddleware>();
app.MapGet("/health", () => Results.Ok(new { status = "ok", agent = AgentName }));
app.MapAGUI(AgentName, "/");
await app.RunAsync("http://127.0.0.1:5100");
```

> **关键设计决定**（已对照已安装的 `Microsoft.Agents.AI.*` 预发布版验证）：
> - **不存在 `IAGUIContext`、`ToolResult`、`ctx.UpdateStateAsync` 这些 API。** 都是想象出来的。在 .NET 这边，AG-UI bridge 直接传输 `AIFunction` 的返回值 —— Generative UI / Tool-based UI 的 payload 就是带 `Component` 字段的强类型 record（例如 `ExceptionsResult { Component = "ExceptionsList", … }`、`FreightQuoteResult { Component = "FreightCompareCard", … }`）。客户端拿到的就是 `FunctionResultContent`。
> - **客户端工具靠 system prompt 触发。** 没有 `ctx.InvokeClientToolAsync`：模型看到指令里说"出现 high severity 时调用 play_alert_sound"，再加上 `list_exceptions` 返回值里有 `severity: "high"`，于是主动调用客户端工具。
> - **HITL 写在工具里，而不是 LAB 4 的 `RequestPort`。** 想从 function tool 内部驱动 LAB 4 的 `RequestPort` 需要在 Foundry response 挂起时持续 pump `WorkflowEvent`，超出本 LAB 范围。更简单的形状是：`fulfill_order` 检查 `total_usd >= $1000` 直接返回 `ApprovalDialogResult`；客户端用 `supervisor_approval=true` 重试时，工具本地合成 `FulfillmentResult` 凭证（carrier `MANUAL`、supervisor 从仓库 map 里查）。
> - **低阈值订单确实跑完整的 LAB 4 workflow。** `fulfill_order` 通过 `WorkflowFactory.Build(Path.Combine(Path.GetTempPath(), $"zava-lab05-{Guid.NewGuid():N}"))` 构建，再用 `InProcessExecution.RunStreamingAsync(workflow, orderId, checkpoints, sessionId: …)` 跑起来，监听 `WorkflowOutputEvent.Data is ShippedVoucher`。每次工具调用都有独立 checkpoint 目录（用完尽量清理）。
> - **`AGUIChatClient` 第二个参数是 `string` 不是 `Uri`。** `new AGUIChatClient(http, urlString)`。配套的 agent 是 `chatClient.AsAIAgent(name, description, tools)` → `AIAgent`。
> - **`Microsoft.Agents.AI.Foundry` 在预发布 feed 里，`Version="*-*"` 能命中。** 把 `MAAI001`、`NU1604`、`NU1902` 加进 `<NoWarn>`，抑制实验性 API 与通配符版本警告。

### Step 4 — `ApiKeyMiddleware`（dev 警告、prod 严格）

```csharp
internal sealed class ApiKeyMiddleware(RequestDelegate next, ILogger<ApiKeyMiddleware> logger)
{
    private static int _warned;

    public async Task InvokeAsync(HttpContext ctx)
    {
        if (ctx.Request.Path == "/health") { await next(ctx); return; }

        string? expected = Environment.GetEnvironmentVariable("AG_UI_API_KEY");
        if (string.IsNullOrEmpty(expected))
        {
            if (Interlocked.Exchange(ref _warned, 1) == 0)
                logger.LogWarning("AG_UI_API_KEY is not set — running in dev mode without auth.");
            await next(ctx); return;
        }

        if (!ctx.Request.Headers.TryGetValue("X-API-Key", out var got))
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized; return;
        }
        if (!StringValues.Equals(got, expected))
        {
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden; return;
        }

        await next(ctx);
    }
}
```

### Step 5 — `ControlTowerSmoke/Program.cs`（5 轮客户端，**每轮一个新 session**）

```csharp
using HttpClient http = new() { Timeout = TimeSpan.FromSeconds(120) };
http.DefaultRequestHeaders.Add("X-API-Key", apiKey);
http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

AIFunction playAlertSound = AIFunctionFactory.Create(
    (string level) =>
    {
        string bell = level == "high" ? "🚨🚨🚨" : "🔔";
        Console.WriteLine($"\n  {bell} [client.play_alert_sound] level={level}");
        return $"alert_played:{level}";
    },
    name: "play_alert_sound",
    description: "Play a UI alert sound. Use level=\"high\" for severe operational events.");

AGUIChatClient chatClient = new(http, serverUrl);     // URL 用 string，不是 Uri
AIAgent agent = chatClient.AsAIAgent(
    name: "control-tower-smoke",
    description: "Operations console smoketest client.",
    tools: [playAlertSound]);

foreach ((string label, string prompt) in turns)
{
    // ⚠ 每轮新建 AgentSession —— 见下方说明
    AgentSession session = await agent.CreateSessionAsync();
    List<ChatMessage> messages = [new ChatMessage(ChatRole.User, prompt)];

    await foreach (AgentResponseUpdate update in agent.RunStreamingAsync(messages, session))
    {
        foreach (AIContent content in update.Contents)
        {
            switch (content)
            {
                case TextContent t when !string.IsNullOrEmpty(t.Text): Console.Write(t.Text); break;
                case FunctionCallContent fc:   Console.WriteLine($"\n  → tool call: {fc.Name}(…)"); break;
                case FunctionResultContent fr: Console.WriteLine($"\n  ← tool result: {fr.Result}"); break;
                case ErrorContent err:         Console.Error.WriteLine($"\n  !! {err.Message}"); break;
            }
        }
    }
}
```

> **⚠ 当 Foundry 与 AG-UI 客户端工具组合在一起时，必须每轮换一个 `AgentSession`。** AG-UI 把客户端工具（这里就是 `play_alert_sound`）放在 .NET SDK 本地执行并通过 SSE 回传结果，但 AG-UI ASP.NET Core bridge **不会** 把这个结果落回 Foundry 对话线程。第 1 轮调过客户端工具之后，Foundry 线程上残留了一个无对应输出的 `function_call`，第 2 轮发新消息时 400 报 `"No tool output found for function call call_…"`。本 LAB 的 5 个 prompt 每个都自带订单号 / 区域 / 包裹重量等所有必要上下文，所以"每轮新 session"在功能上等价于真实 UI 的页面跳转。

### Step 6 — 启动 + 烟雾

```bash
# terminal 1 — 启动 AG-UI 服务端
export AG_UI_API_KEY=zava-control-tower-demo-key
dotnet run --project workshop/LAB05-control-tower-agui/ControlTower
# [server] ZavaControlTower AG-UI endpoint listening on http://127.0.0.1:5100/

# terminal 2 — 跑 5 轮烟雾测试
export AG_UI_API_KEY=zava-control-tower-demo-key
dotnet run --project workshop/LAB05-control-tower-agui/ControlTowerSmoke
```

预期输出高亮（已对 `gpt-5.5` 实际验证过）：

| 轮次 | 工具序列 | 应当看到 |
|------|---------|---------|
| 1 | `list_exceptions` → `play_alert_sound(level=high)` | 4 条异常，2 条 high-severity；**客户端控制台**打印 🚨🚨🚨。 |
| 2 | `quote_freight(origin=US, destination=EU, weightKg=5)` | FEDEX $34.50 / 2 天；DHL $39.50 / 3 天。 |
| 3 | `fulfill_order(orderId=ORD-20260524-001)` | 实际跑完 LAB 4 workflow：SEA-01 出仓、supervisor Mei Tanaka、USPS、5 天。 |
| 4 | `fulfill_order(orderId=ORD-20260524-002)` | $1,500 触发 `ApprovalDialog`（超 $1,000 阈值）。 |
| 5 | `fulfill_order(orderId=ORD-20260524-002, supervisorApproval=true)` | 本地合成凭证（supervisor Mei Tanaka、carrier `MANUAL`）。 |

### .NET 验收标准

- [ ] 两个项目 `dotnet build` 都 0 warning 0 error（`NU1604;NU1902;MAAI001` 已抑制）。
- [ ] `curl -H "X-API-Key: zava-control-tower-demo-key" http://127.0.0.1:5100/health` 返回 `200 OK`；缺 key → `401`；错 key → `403`。
- [ ] 第 1 轮**在客户端控制台**打印 `🚨🚨🚨 [client.play_alert_sound] level=high`（证明 AG-UI 服务端 → 客户端的 frontend-tool 分派打通）。
- [ ] 第 3 轮真实驱动 link 进来的 LAB 4 源：`WorkflowFactory.Build` → `InProcessExecution.RunStreamingAsync` → `ShippedVoucher` —— 不是合成的。
- [ ] 第 4 轮 `FunctionResultContent` payload 含 `"component": "ApprovalDialog"` 和 `"reason": "amount_over_threshold"`；第 5 轮含 `"component": "FulfillmentResult"` 和 `"status": "shipped"`。
- [ ] LAB 4 在 `#if !LAB05_AGUI_HOST` 之后仍能单独构建（`dotnet build workshop/LAB04-fulfillment-workflow/FulfillmentWorkflow` → 0/0）。
- [ ] 没有内联 mock —— `ControlTower/Program.cs` 业务数据全部走 `ZavaData.LoadExceptions()` / `ZavaData.LoadCarriers()` / `ZavaData.FindOrder(...)`。

---

## 故事大结局

四周之后，ZavaShop CEO 站在指挥中心大屏前：

- **Zara**（LAB 1）在 Mei 的工位上跑了 28 天，工单减少 92%。
- **Pierre**（LAB 2）替采购部省了 14% 的合同审核时间，没出一次 PO 误提。
- **Aria**（LAB 3）NPS 从 3.9 → 4.7，红队 ASR 稳定在 6%。
- **履约工作流**（LAB 4）平均出仓时间从 38 分钟 → 11 分钟。
- **指挥中心**（LAB 5）上线那天，运营经理第一次说："我能看清楚业务在被自动化做什么了。"

CEO 转向 CTO：

> *"下一个季度，我们让这套架构去管 ZavaShop 的 **物流路径优化** 和 **季节性促销定价**。"*

—— 那是下一季 Workshop 的故事。本季 Workshop 在此完结。 🎉

---

## 一键回到首页

[← 返回 Workshop 总览](../../README.md)
