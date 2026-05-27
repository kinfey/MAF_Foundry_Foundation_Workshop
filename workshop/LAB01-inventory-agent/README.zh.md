# LAB 1 — 仓储助手 Zara：单 Agent + Function Tools + MCP

> **由 SKILL 协助**（任选一个赛道）：
> - Python：[`agent-framework-azure-ai-py`](../../.github/skills/agent-framework-azure-ai-py/SKILL.md)
> - .NET（C#）：[`agent-framework-azure-ai-csharp`](../../.github/skills/agent-framework-azure-ai-csharp/SKILL.md)
>
> **Foundry 模型**：`gpt-5.5`

---

## 选择你的技术栈

本 LAB 同时提供两个等效的实现。**同一个故事、同一份 fixture、同一套验收标准**，选一走：

| 赛道 | 交付物 | 需要加载的 Skill | 数据 helper |
|------|--------|---------------------|---------------|
| 🐍 **Python** | `zara_agent.py` | [`agent-framework-azure-ai-py/SKILL.md`](../../.github/skills/agent-framework-azure-ai-py/SKILL.md) + [`references/tools.md`](../../.github/skills/agent-framework-azure-ai-py/references/tools.md) + [`references/mcp.md`](../../.github/skills/agent-framework-azure-ai-py/references/mcp.md) + [`references/threads.md`](../../.github/skills/agent-framework-azure-ai-py/references/threads.md) | [`workshop/data/zava_data.py`](../data/zava_data.py) |
| 🟦 **.NET（C#）** | `ZaraAgent/` 控制台项目 | [`agent-framework-azure-ai-csharp/SKILL.md`](../../.github/skills/agent-framework-azure-ai-csharp/SKILL.md) + [`references/tools.md`](../../.github/skills/agent-framework-azure-ai-csharp/references/tools.md) + [`references/mcp.md`](../../.github/skills/agent-framework-azure-ai-csharp/references/mcp.md) + [`references/threads.md`](../../.github/skills/agent-framework-azure-ai-csharp/references/threads.md) | [`workshop/data/ZavaData.cs`](../data/ZavaData.cs) |

Python 赛道在 [§任务清单](#任务清单)；.NET 赛道在 [§.NET 实现赛道](#net-实现赛道)。验收标准两者完全一致。

---

## 故事

ZavaShop 西雅图配送中心的仓储主管 Mei 抱怨：

> *"我一上午被微信、邮件、电话问了 60 次「SKU-7421 还有多少？」、「上周从义乌发来的那批花盆到货没？」，我得不停在 WMS、TMS、Excel 之间切。"*

CTO 拍板：先做一个叫 **Zara** 的仓储助手，让 Mei 直接用自然语言问就能拿到答案。要求：

1. 能查询 SKU 在各仓库的实时库存（自定义 Function Tool）。
2. 能查询任意采购订单 PO 的到货状态（自定义 Function Tool）。
3. 能用 **MCP 服务器**对接 ZavaShop 已经搭好的 *物流追踪 MCP*（暂时用 Microsoft Learn MCP server `https://learn.microsoft.com/api/mcp` 模拟）。
4. 全程必须使用 **Foundry 项目里部署的 GPT-5.5** 模型。

> 这是 ZavaShop 智能化的第 0 公里 —— 让一个 Agent 跑起来。

### 本 LAB 读取哪些数据

所有真实数字都从 [`workshop/data/`](../data/README.zh.md) 加载。LAB 1 依赖的 fixture：

- [`warehouses.json`](../data/warehouses.json) — 5 个履约中心；LAB 1 默认查 `SEA-01`（西雅图）。
- [`skus.json`](../data/skus.json) — 10 个 SKU，含 `SKU-7421`（北欧亚麻被套）与 `SKU-3055`（陶土花盆）。
- [`inventory.json`](../data/inventory.json) — (SKU, 仓库) 维度的在手/预占/补货点；Agent 应该报出 `SKU-7421 @ SEA-01 = 312 件在手`。
- [`purchase_orders.json`](../data/purchase_orders.json) — 6 个 PO，含 `PO-20260518-001`（in_transit，ETA 2026-05-26）与 `PO-20260519-007`（customs_clearing，ETA 2026-05-27）。

---

## 学习目标

完成本 LAB 后你会：

- 用 `AzureAIAgentsProvider` 创建一个 **持久化 (server-side) Agent**。
- 写 **同步 + 异步** 的 `function tool`，并交给 Agent 使用。
- 用 `HostedMCPTool` 接入一个 **MCP 服务器** 提供的工具集。
- 用 `async with` + `AzureCliCredential` 管理客户端 / 凭据 / Agent 的生命周期。
- 用 `AgentThread` 让对话保持上下文。

---

## Microsoft Learn 参考

本 LAB 涵盖的 Agent Framework 知识点（Copilot 在 SKILL 里已经离线写好；以下是对应的 Learn 章节，建议事后阅读巩固）：

- [Agent Framework — overview & quick start](https://learn.microsoft.com/en-us/agent-framework/overview/agent-framework-overview/) · [Agent Framework — tools overview](https://learn.microsoft.com/en-us/agent-framework/agents/tools/index)
- [Function tools（`AIFunctionFactory.Create` / `@tool`）](https://learn.microsoft.com/en-us/agent-framework/agents/tools/function-tools)
- [Local MCP tools（`McpClient`、`ListToolsAsync`）](https://learn.microsoft.com/en-us/agent-framework/agents/tools/local-mcp-tools)
- [Microsoft Foundry provider — hosted agents & threads](https://learn.microsoft.com/en-us/agent-framework/agents/providers/microsoft-foundry)
- [Foundry agents — Function calling](https://learn.microsoft.com/en-us/azure/foundry/agents/how-to/tools/function-calling) · [Foundry agents — Connect to MCP servers](https://learn.microsoft.com/en-us/azure/foundry/agents/how-to/tools/model-context-protocol)

> SKILL 内部参考：[references/tools.md](../../.github/skills/agent-framework-azure-ai-py/references/tools.md)、[references/mcp.md](../../.github/skills/agent-framework-azure-ai-py/references/mcp.md)、[references/threads.md](../../.github/skills/agent-framework-azure-ai-py/references/threads.md)

---

## 任务清单

### Step 1 — 在 Agent Mode 里选中 ZavaShop Coding Agent

在 VS Code Copilot Chat 切换到 **Agent Mode**，打开 Agent 选择器，选中 **`zavashop-coding-agent`**，然后发送一条同时点明 LAB 编号和 **使用的编程语言** 的消息：

```
I'm doing LAB 1 in Python — build the ZavaShop inventory agent Zara.
```

> 不要再用 `@zavashop-coding-agent` 这种写法 —— Coding Agent 是从 Agent Mode 下拉里选的，对话框里只写任务描述（含语言）。

Coding Agent 会自动按它的 7 步循环走：

1. `read_file` 加载 [`.github/skills/agent-framework-azure-ai-py/SKILL.md`](../../.github/skills/agent-framework-azure-ai-py/SKILL.md)，需要时额外加载 [`references/tools.md`](../../.github/skills/agent-framework-azure-ai-py/references/tools.md)、[`references/mcp.md`](../../.github/skills/agent-framework-azure-ai-py/references/mcp.md)、[`references/threads.md`](../../.github/skills/agent-framework-azure-ai-py/references/threads.md)。
2. `read_file` 本 LAB README。
3. 在 [`workshop/LAB01-inventory-agent/`](.) 下创建 `zara_agent.py`：
   - `Agent` + `FoundryChatClient` + `AzureCliCredential` + `async with`（当前 `agent-framework` 预发布包真正导出的组合；SKILL 里写的 `AzureAIAgentsProvider` 在该版本里没有导出，所以以 `FoundryChatClient` 为准）。
   - endpoint 与 model 从 [`workshop/.env`](../.env) 通过一个小 `load_env()` 函数加载到 `os.environ["FOUNDRY_PROJECT_ENDPOINT"]` / `os.environ["FOUNDRY_MODEL"]`。
   - 工具：`get_stock(sku, warehouse)` + `get_po_status(po_number)` + `find_open_pos_by_sku(sku, warehouse=None)` + 指向 `https://learn.microsoft.com/api/mcp` 的 `MCPStreamableHTTPTool`。
   - 通过 `agent.create_session()` 拿到一个 `AgentSession` 跑 3 轮对话。
4. `get_errors` 校验语法，然后 `runCommands` 跑 `python zara_agent.py` 烟测。
5. 按下面验收标准逐条勾选。

> 如果你想手动写代码也可以，跑完后再在 Agent Mode 里选回 `zavashop-coding-agent`，只让它跑 step 6+7。

### Step 2 — 编写 function tool 与 `load_env()` 辅助函数

在 `zara_agent.py` 里同时写好两个数据工具、一个 SKU→ 未到货 PO 的辅助工具，以及一个把 [`workshop/.env`](../.env) 注入 `os.environ` 的 `load_env()`（这样不用每次开 shell 都 `export`）：

```python
import os
import sys
from pathlib import Path

# 导入共享 fixture
sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "data"))
from zava_data import find_po, find_stock, load_purchase_orders


def load_env() -> None:
    """把 workshop/.env 读到进程环境变量里。"""
    env_path = Path(__file__).resolve().parents[1] / ".env"
    if not env_path.exists():
        return
    for line in env_path.read_text(encoding="utf-8").splitlines():
        line = line.strip()
        if not line or line.startswith("#") or "=" not in line:
            continue
        key, value = line.split("=", 1)
        key = key.strip()
        value = value.strip().strip('"').strip("'")
        if key and key not in os.environ:
            os.environ[key] = value


def get_stock(sku: str, warehouse: str = "SEA-01") -> str:
    """Get current on-hand stock of a SKU in a warehouse."""
    row = find_stock(sku, warehouse)
    if row is None:
        return f"{sku} is not tracked at warehouse {warehouse}."
    return (
        f"{sku} @ {warehouse}: on_hand={row['on_hand']}, "
        f"reserved={row['reserved']}, reorder_point={row['reorder_point']}"
    )


async def get_po_status(po_number: str) -> dict:
    """Query the status of an inbound Purchase Order."""
    po = find_po(po_number)
    if po is None:
        return {"po_number": po_number, "status": "unknown"}
    return po


def find_open_pos_by_sku(sku: str, warehouse: str | None = None) -> list[dict]:
    """返回某个 SKU 下尚未到货的 PO，按 ETA 倒序。"""
    open_pos = [
        po
        for po in load_purchase_orders()
        if po["sku"] == sku
        and po["status"] != "delivered"
        and (warehouse is None or po["destination_warehouse"] == warehouse)
    ]
    open_pos.sort(key=lambda po: po["eta"], reverse=True)
    return open_pos
```

> **不要在脚本里写 mock dict。** 所有查找都走 [`workshop/data/`](../data/README.zh.md) 下的共享 fixture，这样所有 LAB 对 `SKU-7421 @ SEA-01` 的认知会保持一致。需要扩充数据时去改 JSON，不要改 Python。

> **为什么加第三个工具？** 第 2 轮问的是“上一轮那个 SKU 最近未到货的 PO”。`get_po_status` 只能查已知 PO 号码，没有 `find_open_pos_by_sku` Agent 就拿不到可枚举的 PO集，也就会走现。

Fixture 已经覆盖了 Mei 会问的 `SKU-7421`、`SKU-3055`、`PO-20260518-001`、`PO-20260519-007`，下一步直接用真数据跑。

### Step 3 — 创建 Agent 并挂载 MCP

当前 PyPI 上的 Microsoft Agent Framework 预发布包导出的是 `Agent` + `FoundryChatClient`（`agent_framework.foundry`）以及 `MCPStreamableHTTPTool`（`agent_framework`）。首选这一组 —— SKILL 里的 `AzureAIAgentsProvider` / `HostedMCPTool` 作为参考保留，在当前版本里并没有导出。

```python
from agent_framework import Agent, MCPStreamableHTTPTool
from agent_framework.foundry import FoundryChatClient
from azure.identity.aio import AzureCliCredential

load_env()
endpoint = os.environ["FOUNDRY_PROJECT_ENDPOINT"]
model    = os.environ["FOUNDRY_MODEL"]

async with (
    AzureCliCredential() as credential,
    MCPStreamableHTTPTool(
        name="learn-mcp",
        url="https://learn.microsoft.com/api/mcp",
    ) as learn_mcp,
    Agent(
        client=FoundryChatClient(
            project_endpoint=endpoint,
            model=model,
            credential=credential,
        ),
        name="Zara",
        instructions=(
            "你是 ZavaShop 西雅图配送中心的仓储助手 Zara。"
            "回答时永远用工具拿到的真实数据，不要编造库存或 PO 数字。"
            "某个 SKU 最近一张未到货的 PO，先调 find_open_pos_by_sku，再用 get_po_status 深查。"
        ),
        tools=[get_stock, get_po_status, find_open_pos_by_sku, learn_mcp],
    ) as agent,
):
    ...
```

### Step 4 — 在 `AgentSession` 上跑 3 轮对话

模拟 Mei 的真实场景（必须按顺序问，验证 Agent 记住上下文）：

```python
session = agent.create_session()

# 第 1 轮：直接问库存
await agent.run("SKU-7421 在 SEA-01 还有多少？", session=session)

# 第 2 轮：基于上一轮的 SKU 追问（不再重复 SKU）
await agent.run("那这个 SKU 最近一张未到货的 PO 是哪张？", session=session)

# 第 3 轮：调 MCP
await agent.run("帮我从 Microsoft Learn MCP 搜一下 Azure AI Foundry 的最佳实践", session=session)
```

> `AgentSession` 在当前 SDK 里取代了旧文档里的 `AgentThread`/`get_new_thread()`，职责完全一样—— 服务器端多轮对话上下文。

### Step 5 — 跑通

```bash
cd workshop/LAB01-inventory-agent
python zara_agent.py
```

---

## 验收标准

- [ ] 控制台能看到三轮的 Agent 回复，第二轮没有重新问 SKU 也能继续（`AgentSession` 带住了上下文）。
- [ ] 第一轮 / 第二轮的库存与 PO 数字与 fixture 一致（`SKU-7421 @ SEA-01 = 312 件在手`；`PO-20260518-001` 状态为 `in_transit`，ETA `2026-05-26`），说明工具被真的调用了且没有幻觉数字。
- [ ] 第三轮的回复明显带有 Microsoft Learn 内容（说明 MCP 被调用）。
- [ ] `FOUNDRY_PROJECT_ENDPOINT` / `FOUNDRY_MODEL` 是 `load_env()` 之后从 `os.environ` 里读的，没有硬编码。
- [ ] 整个脚本没有手动 `close()`，credential / MCP 工具 / Agent 都走 `async with`。
- [ ] `zara_agent.py` 中 **没有任何 mock dict** — 库存 / PO 数据全部通过 `zava_data.find_stock` / `zava_data.find_po` / `zava_data.load_purchase_orders` 读取。

---

## .NET 实现赛道

面向选择 .NET 赛道的学员。同一个故事、同一份 fixture、同一套验收标准。

### Step 1 — 在 Agent Mode 里选中 ZavaShop Coding Agent（C#）

在 VS Code Copilot Chat → **Agent Mode** → Agent 选择器 → **`zavashop-coding-agent`**，然后发送：

```
I'm doing LAB 1 in C# — build the ZavaShop inventory agent Zara.
```

> 同样不要加 `@zavashop-coding-agent` 前缀 —— 语言是告诉 Agent 加载哪套 SKILL 的关键词。

Coding Agent 会：

1. `read_file` [`.github/skills/agent-framework-azure-ai-csharp/SKILL.md`](../../.github/skills/agent-framework-azure-ai-csharp/SKILL.md)，并加载 [`references/tools.md`](../../.github/skills/agent-framework-azure-ai-csharp/references/tools.md)、[`references/mcp.md`](../../.github/skills/agent-framework-azure-ai-csharp/references/mcp.md)、[`references/threads.md`](../../.github/skills/agent-framework-azure-ai-csharp/references/threads.md)。
2. `read_file` 本 LAB README。
3. 在 [`workshop/LAB01-inventory-agent/`](.) 下创建 `ZaraAgent/`：`Microsoft.NET.Sdk` 控制台项目，面向 `net10.0`，并以 link 方式引入 `..\..\data\ZavaData.cs`。
4. 工具：`GetStock(sku, warehouse)` + `GetPoStatus(po_number)` + `FindOpenPosBySku(sku, warehouse?)` 由 `AIFunctionFactory.Create(...)` 包装；再加一个 **本地 MCP** 工具（`McpClient` + `mcpTools.Cast<AITool>()`）指向 `https://learn.microsoft.com/api/mcp`。不能用 hosted MCP (`ResponseTool.CreateMcpTool`) —— 它不是 `AITool`，只能在持久化 `CreateAgentVersionAsync` 路径上挂载，详见 [references/mcp.md](../../.github/skills/agent-framework-azure-ai-csharp/references/mcp.md)。
5. endpoint 与 model 由 [`workshop/.env`](../.env) 经一个小 `LoadEnv()` 辅助函数加载后，从 `Environment.GetEnvironmentVariable("FOUNDRY_PROJECT_ENDPOINT")` / `("FOUNDRY_MODEL")` 读取。
6. 用 `AgentSession` 跑 3 轮对话。
7. `get_errors` → `dotnet build` → `dotnet run`。

### Step 2 — `ZaraAgent.csproj`

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.Agents.AI" Version="*-*" />
    <PackageReference Include="Microsoft.Agents.AI.Foundry" Version="*-*" />
    <PackageReference Include="Microsoft.Extensions.AI" Version="*-*" />
    <PackageReference Include="Azure.AI.Projects" Version="*-*" />
    <PackageReference Include="Azure.Identity" Version="*" />
    <PackageReference Include="ModelContextProtocol" Version="*-*" />
  </ItemGroup>
  <ItemGroup>
    <Compile Include="..\..\data\ZavaData.cs" Link="ZavaData.cs" />
  </ItemGroup>
</Project>
```

> 这里用 `ModelContextProtocol`（本地 MCP 客户端）而不是 `OpenAI`。`OpenAI.Responses` 里的 hosted MCP 包装不能填到无状态 `AsAIAgent(...)` 的 `tools:` 参数里——详见 Step 4。

### Step 3 — 实现 function tool 与 `LoadEnv()` 辅助函数（C#）

```csharp
using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using ZavaShop.Workshop.Data;

[Description("Get current on-hand stock of a SKU in a warehouse.")]
static string GetStock(
    [Description("SKU id, e.g. SKU-7421.")] string sku,
    [Description("Warehouse id, e.g. SEA-01.")] string warehouse = "SEA-01")
{
    JsonNode? row = ZavaData.FindStock(sku, warehouse);
    if (row is null) return $"{sku} is not tracked at warehouse {warehouse}.";
    return $"{sku} @ {warehouse}: on_hand={row["on_hand"]}, " +
           $"reserved={row["reserved"]}, reorder_point={row["reorder_point"]}";
}

[Description("Query the status of an inbound Purchase Order by its number.")]
static string GetPoStatus(
    [Description("PO number, e.g. PO-20260518-001.")] string poNumber)
{
    JsonNode? po = ZavaData.FindPo(poNumber);
    return po?.ToJsonString()
        ?? JsonSerializer.Serialize(new { po_number = poNumber, status = "unknown" });
}

[Description("查询某 SKU 的未到货 PO，按 ETA 倒序。")]
static string FindOpenPosBySku(
    [Description("SKU id，例如 SKU-7421。")] string sku,
    [Description("仓库 id；留空表示不过滤。")] string warehouse = "")
{
    JsonArray pos = ZavaData.LoadPurchaseOrders();
    var open = pos
        .Where(p => p is not null
            && p["sku"]?.GetValue<string>() == sku
            && p["status"]?.GetValue<string>() != "delivered"
            && (string.IsNullOrEmpty(warehouse)
                || p["destination_warehouse"]?.GetValue<string>() == warehouse))
        .OrderByDescending(p => p!["eta"]?.GetValue<string>(), StringComparer.Ordinal)
        .Select(p => JsonNode.Parse(p!.ToJsonString())!)
        .ToArray();
    return new JsonArray(open).ToJsonString();
}

static void LoadEnv()
{
    DirectoryInfo? dir = new(AppContext.BaseDirectory);
    string? envPath = null;
    while (dir is not null)
    {
        string candidate = Path.Combine(dir.FullName, "workshop", ".env");
        if (File.Exists(candidate)) { envPath = candidate; break; }
        dir = dir.Parent;
    }
    if (envPath is null) return;

    foreach (string raw in File.ReadAllLines(envPath))
    {
        string line = raw.Trim();
        if (line.Length == 0 || line.StartsWith('#') || !line.Contains('=')) continue;
        int eq = line.IndexOf('=');
        string key = line[..eq].Trim();
        string value = line[(eq + 1)..].Trim().Trim('"').Trim('\'');
        if (key.Length > 0 && Environment.GetEnvironmentVariable(key) is null)
            Environment.SetEnvironmentVariable(key, value);
    }
}
```

> **禁止 inline mock。** 与 Python 赛道一样：所有查询都必须走 [`workshop/data/ZavaData.cs`](../data/ZavaData.cs)。

> **为什么加第三个工具？** 第 2 轮问的是“上一轮那个 SKU 最近未到货的 PO”。`GetPoStatus` 只能查已知 PO 号码，没有 `FindOpenPosBySku` Agent 拿不到可枚举的 PO 集，也就会出现幻觉。

### Step 4 — 创建 Agent，挂载 MCP 工具（C#）

无状态的 `AIProjectClient.AsAIAgent(model, instructions, name, tools: [...])` 只接受实现了 `Microsoft.Extensions.AI.AITool` 的对象。本地 MCP 返回的 `McpClientTool` 是 `AITool`；hosted MCP 的 `ProjectsAgentTool.AsProjectTool(ResponseTool.CreateMcpTool(...))` **不是**。所以这里走本地 MCP（与 Python 赛道的 `MCPStreamableHTTPTool` 一致）：

```csharp
using Azure.AI.Projects;
using Azure.Identity;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;

LoadEnv();
string endpoint = Environment.GetEnvironmentVariable("FOUNDRY_PROJECT_ENDPOINT")!;
string model    = Environment.GetEnvironmentVariable("FOUNDRY_MODEL")!;

AIProjectClient projectClient = new(new Uri(endpoint), new AzureCliCredential());

await using McpClient learnMcp = await McpClient.CreateAsync(new HttpClientTransport(new()
{
    Endpoint = new Uri("https://learn.microsoft.com/api/mcp"),
    Name = "Microsoft Learn MCP",
}));
IList<McpClientTool> mcpTools = await learnMcp.ListToolsAsync();

List<AITool> tools =
[
    AIFunctionFactory.Create(GetStock),
    AIFunctionFactory.Create(GetPoStatus),
    AIFunctionFactory.Create(FindOpenPosBySku),
    .. mcpTools.Cast<AITool>(),
];

AIAgent agent = projectClient.AsAIAgent(
    model,
    instructions:
        "你是 ZavaShop 西雅图配送中心的仓储助手 Zara。" +
        "回答时永远用工具拿到的真实数据，不要编造库存或 PO 数字。" +
        "某个 SKU 最近一张未到货的 PO，先调 FindOpenPosBySku，再用 GetPoStatus 深查。",
    name: "Zara",
    tools: tools);
```

### Step 5 — 用 AgentSession 跑 3 轮对话

```csharp
AgentSession session = await agent.CreateSessionAsync();

Console.WriteLine(await agent.RunAsync(
    "How many SKU-7421 do we have left at SEA-01?", session));

Console.WriteLine(await agent.RunAsync(
    "What is the most recent PO for that SKU that hasn't arrived yet?", session));

Console.WriteLine(await agent.RunAsync(
    "Search the Microsoft Learn MCP for best practices on Azure AI Foundry.", session));
```

### Step 6 — 运行

```bash
cd workshop/LAB01-inventory-agent/ZaraAgent
dotnet run
```

上面的验收标准适用不变 — 同一个 `on_hand=312`、同一个 `PO-20260518-001` in_transit ETA `2026-05-26`、同一个第三轮包含 Microsoft Learn 内容、同一条「不允许内联 mock」的规则（所有查询走 `ZavaData`）。

---

## 故事收尾

Mei 用 Zara 跑了一天，问答次数从 60 次降到 4 次。她在内部 Teams 群里发了个 🎉。但她马上提了一个新需求：

> *"采购的 Pierre 比我惨多了，他要查的不是库存，是 8 家供应商的发货计划，工具一堆，登录还要换 token。能不能也给他做一个？"*

—— 这是 [LAB 2](../LAB02-procurement-toolbox/README.md) 的开始。
