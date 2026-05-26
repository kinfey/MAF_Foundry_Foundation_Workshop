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

## 对齐 Microsoft Learn

- *Agent Framework — AG-UI integration*
- *Streaming AG-UI events over SSE*
- *Hybrid client / server tools*
- *Generative & tool-based UI patterns*
- *Workflows as AG-UI endpoints*

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
   - `server.py`：FastAPI + `add_agent_framework_fastapi_endpoint(app, AgentFrameworkAgent(fulfillment_agent), "/")` + `X-API-Key` 依赖 + 2 个服务端工具（`list_exceptions` + `quote_freight` 返回 `state_update(...)`）。
   - `client_smoketest.py`：`AGUIChatClient` 跑 3 段对话（普通问询 / HITL / 客户端工具 `play_alert_sound`）。
   - `frontend/README.md`：7 个 AG-UI 特性 → 7 个 React 组件的映射表。
4. `runCommands` 起 `uvicorn server:app --host 127.0.0.1 --port 5100`，另开一个进程跑 `client_smoketest.py`，贴输出。

> Coding Agent 不会看你寫 React（不在 SKILL 范围），但会把后端与接口及完整的 7 特性映射文档交付完毕。

### Step 2 — `server.py`

```python
import os
import sys
from pathlib import Path
from typing import Any
from agent_framework import tool
from agent_framework.ag_ui import (
    AgentFrameworkAgent,
    add_agent_framework_fastapi_endpoint,
    state_update,
)
from fastapi import Depends, FastAPI, HTTPException, Header

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "data"))
from zava_data import load_exceptions, load_carriers

# 1. 引入 LAB 4 的 fulfillment_agent
from lab04_fulfillment_workflow import fulfillment_agent

# 2. 额外服务端工具
@tool
async def list_exceptions() -> list[dict]:
    """列出今天的异常订单（缺货 / 物流被拒 / PO 延误 / 超阈值）。"""
    return load_exceptions()  # 4 行真数据，来自 exceptions.json

@tool
async def quote_freight(origin: str, destination: str, weight_kg: float) -> Any:
    quotes = [
        {
            "carrier": c["carrier_id"],
            "name": c["name"],
            "price_usd": round(c["base_usd"] + c["per_kg_usd"] * weight_kg, 2),
            "transit_days": c["transit_days_typical"],
        }
        for c in load_carriers()
        if destination in c["lanes"]
    ][:3]
    return state_update(
        text=f"已为 {origin}→{destination} 找到 {len(quotes)} 个报价",
        tool_result={"component": "FreightCompareCard", "quotes": quotes},
        state={"last_freight_quote": quotes},
    )

# 3. Agent wrapper：开启共享状态 + 预测式更新
af_agent = AgentFrameworkAgent(
    agent=fulfillment_agent,
    name="ZavaControlTower",
    description="ZavaShop 供应链指挥中心",
    state_schema={
        "type": "object",
        "properties": {
            "today_kpi": {"type": "object"},
            "last_freight_quote": {"type": "array"},
            "exceptions": {"type": "array"},
        },
    },
)

# 4. API-Key Dep
def require_api_key(x_api_key: str = Header(...)) -> None:
    if x_api_key != os.environ["AG_UI_API_KEY"]:
        raise HTTPException(status_code=401, detail="bad key")

app = FastAPI(title="ZavaShop Control Tower")
add_agent_framework_fastapi_endpoint(
    app, af_agent, "/", dependencies=[Depends(require_api_key)],
)
```

> **工具体里不要有占位符。** `list_exceptions` 原样返回 `exceptions.json`；`quote_freight` 的承运商 ID 从 `carriers.json` 拉。这样 UI 才能拿到稳定 ID 去渲染 `<FreightCompareCard/>` 和 `<ExceptionsPanel/>`。

启动：

```bash
uvicorn server:app --host 127.0.0.1 --port 5100
```

### Step 3 — `client_smoketest.py`

```python
import asyncio, os
from agent_framework import Agent, tool
from agent_framework.ag_ui import AGUIChatClient

# 客户端工具（前端版本会真的播音 / 开抽屉，这里只 print）
@tool
def play_alert_sound(level: str = "info") -> str:
    print(f"🔔 [client] play_alert_sound({level})")
    return "played"

client = AGUIChatClient(
    base_url=os.environ.get("AGUI_SERVER_URL", "http://127.0.0.1:5100/"),
    headers={"X-API-Key": os.environ["AG_UI_API_KEY"]},
)

agent = Agent(client=client, tools=[play_alert_sound])

async def main():
    thread = agent.get_new_thread()
    print(await agent.run("当前异常订单有哪些？发现高优先级的请触发 play_alert_sound", thread=thread))
    # 触发 HITL（ORD-20260524-002，金额 $1500），并处理 request_info
    response_stream = agent.run_stream(
        "请走履约：ORD-20260524-002",
        thread=thread,
    )
    async for event in response_stream:
        if hasattr(event, "request_info"):
            await response_stream.respond({event.request_info.id: {"approved": True}})

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

- [ ] `uvicorn` 启动后 `curl -H "X-API-Key: ..." http://127.0.0.1:5100/` 不报 401。
- [ ] `client_smoketest.py` 第一段对话拿到 **4 条异常**，原样来自 `exceptions.json`（含 `EXC-20260525-A` 对应 `ORD-20260525-009`），且 **控制台打印 🔔 play_alert_sound**（服务端检测到高优先级，触发了客户端工具）。
- [ ] 第二段对话（`ORD-20260524-002`，$1500）触发 HITL，回填后履约完成，UI 状态对应 `today_kpi` 字段变化。
- [ ] `quote_freight` 一次调用 **同时**：模型拿到 "已为 X→Y 找到 N 个报价" 摘要、shared state 出现 `last_freight_quote` 且 `carrier` 能在 `carriers.json` 里抓到（`FEDEX` / `DHL` / `USPS` / `ARAMEX` / `SFEXPRESS`）、前端组件能拿到 `tool_result`。
- [ ] `frontend/README.md` 含完整的 7 特性 → 7 组件映射表。
- [ ] `server.py` 中 **没有 `[{"order": "ORD-009", ...}]` 占位符** — `list_exceptions` / `quote_freight` 都走 `zava_data.load_exceptions` / `zava_data.load_carriers`。

---

## .NET 实现赛道

同样七个 AG-UI 特性、同样的共享状态、同样的 API Key、同样的客户端烟雾脚本。

### Step 1 — 在 Agent Mode 里选中 ZavaShop Coding Agent（C#）

在 VS Code Copilot Chat → **Agent Mode** → Agent 选择器 → **`zavashop-coding-agent`**，然后发送：

```
I'm doing LAB 5 in C# — expose the LAB 4 fulfillment workflow over AG-UI so the control tower can drive it.
```

会在 [`workshop/LAB05-control-tower-agui/`](.) 下创建两个项目：

- `ControlTower/`：ASP.NET Core minimal host，引入 LAB 4 的 `fulfillmentAgent`，用 `Microsoft.Agents.AI.Hosting.AGUI.AspNetCore` 里的 `MapAGUI(agent)` 包装，并用 `X-API-Key` 护门。
- `ControlTowerSmoke/`：控制台烟雾客户端，基于 `AGUIChatClient`。

两个 csproj 都要 link `..\..\data\ZavaData.cs`。

### Step 2 — 把 workflow 挂成 AG-UI endpoint（C#）

```csharp
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Hosting.AGUI.AspNetCore;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddAGUI();

var app = builder.Build();

app.Use(async (ctx, next) =>
{
    var expected = Environment.GetEnvironmentVariable("AG_UI_API_KEY")!;
    if (ctx.Request.Headers["X-API-Key"] != expected)
    {
        ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return;
    }
    await next();
});

AIAgent controlTower = FulfillmentWorkflowFactory.BuildAgent();   // 来自 LAB 4
app.MapAGUI("/", controlTower);

app.Run("http://127.0.0.1:5100");
```

### Step 3 — 会写共享状态的服务端工具（C#）

`list_exceptions` 与 `quote_freight` 走 `ZavaData`，并通过 AG-UI 响应流中的 `state_update` 通道更新共享状态：

```csharp
[Description("List today's open fulfillment exceptions.")]
static async Task<ToolResult> ListExceptions(IAGUIContext ctx)
{
    var rows = ZavaData.LoadExceptions()
        .Where(e => e["status"]!.GetValue<string>() == "open")
        .ToList();

    bool hasHigh = rows.Any(e => e["priority"]!.GetValue<string>() == "high");
    if (hasHigh)
        await ctx.InvokeClientToolAsync("play_alert_sound", new { reason = "high_priority_exception" });

    await ctx.UpdateStateAsync(new { open_exceptions = rows });

    return ToolResult.Text(
        $"{rows.Count} open exceptions; {rows.Count(r => r["priority"]!.GetValue<string>() == "high")} high-priority.");
}
```

`quote_freight` 从 `carriers.json` 读（carrier ID 跟验收标准对上），并将 `last_freight_quote` 写入共享状态。

### Step 4 — 7 特性 → 7 组件映射

| AG-UI 特性 | 服务端（.NET） | UI 组件 |
|-------------|------------------|---------|
| 1. Chat | `agent.RunStreamAsync(...)` | `<ChatPanel/>` |
| 2. Backend tool rendering | `AIFunctionFactory.Create(...)` 工具 | `<ToolCallCard/>` |
| 3. Frontend tools | `ctx.InvokeClientToolAsync("play_alert_sound", …)` | `playAlertSound()` |
| 4. HITL | `IWorkflowContext.RequestInfoAsync(...)`（LAB 4） | `<ApprovalDialog/>` |
| 5. Generative UI | `tool_result` 携带类型化 payload | `<DynamicCard/>` |
| 6. Shared state | `ctx.UpdateStateAsync(...)` | `<StatePanel/>` |
| 7. Predictive state | `PredictStateConfig` | `<KpiTicker/>` |

在 `frontend/README.md` 里像 Python 赛道一样记载（真的写 React App 仍不在本 LAB 范围）。

### Step 5 — 运行 + 烟雾

```bash
# terminal 1
export AG_UI_API_KEY=zava-control-tower-demo-key
dotnet run --project workshop/LAB05-control-tower-agui/ControlTower

# terminal 2
export AG_UI_API_KEY=zava-control-tower-demo-key
dotnet run --project workshop/LAB05-control-tower-agui/ControlTowerSmoke
```

验收标准适用不变。「不能写 `[{"order": "ORD-009", …}]` 占位符」这条硜规则在 C# 中同样严格 — 业务数据走 `ZavaData.LoadExceptions()` / `ZavaData.LoadCarriers()`。

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
