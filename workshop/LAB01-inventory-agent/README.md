# LAB 1 — Warehouse Assistant Zara: Single Agent + Function Tools + MCP

> **Powered by SKILL** (pick one track):
> - Python: [`agent-framework-azure-ai-py`](../../.github/skills/agent-framework-azure-ai-py/SKILL.md)
> - .NET (C#): [`agent-framework-azure-ai-csharp`](../../.github/skills/agent-framework-azure-ai-csharp/SKILL.md)
>
> **Foundry model**: `gpt-5.5`
> **Chinese edition**: [README.zh.md](./README.zh.md)

---

## Choose your stack

This LAB ships **two equivalent implementations**. Same story, same fixtures, same acceptance criteria — pick one:

| Track | Build artefact | Skill files to load | Data helper |
|-------|----------------|---------------------|-------------|
| 🐍 **Python** | `zara_agent.py` | [`agent-framework-azure-ai-py/SKILL.md`](../../.github/skills/agent-framework-azure-ai-py/SKILL.md) + [`references/tools.md`](../../.github/skills/agent-framework-azure-ai-py/references/tools.md) + [`references/mcp.md`](../../.github/skills/agent-framework-azure-ai-py/references/mcp.md) + [`references/threads.md`](../../.github/skills/agent-framework-azure-ai-py/references/threads.md) | [`workshop/data/zava_data.py`](../data/zava_data.py) |
| 🟦 **.NET (C#)** | `ZaraAgent/` console app | [`agent-framework-azure-ai-csharp/SKILL.md`](../../.github/skills/agent-framework-azure-ai-csharp/SKILL.md) + [`references/tools.md`](../../.github/skills/agent-framework-azure-ai-csharp/references/tools.md) + [`references/mcp.md`](../../.github/skills/agent-framework-azure-ai-csharp/references/mcp.md) + [`references/threads.md`](../../.github/skills/agent-framework-azure-ai-csharp/references/threads.md) | [`workshop/data/ZavaData.cs`](../data/ZavaData.cs) |

The Python track is documented in [§Tasks](#tasks); the .NET track is documented in [§.NET implementation path](#net-implementation-path). The acceptance criteria are identical.

---

## Story

Mei, the warehouse supervisor at ZavaShop's Seattle fulfillment center, complains:

> *"In a single morning I get hit on WeChat, email and phone 60 times with 'how many SKU-7421 do we have?', 'has the batch of pots from Yiwu landed yet?'. I keep flipping between the WMS, the TMS and an Excel file."*

The CTO decides: build a warehouse assistant called **Zara** first, so Mei can just ask questions in natural language. Requirements:

1. Look up real-time stock for a SKU in any warehouse (custom function tool).
2. Look up the inbound status of any Purchase Order (custom function tool).
3. Use an **MCP server** to plug into ZavaShop's existing *logistics-tracking MCP* (we mock this with the Microsoft Learn MCP server `https://learn.microsoft.com/api/mcp` for now).
4. Always use the **GPT-5.5 model deployed in the Foundry project**.

> This is kilometer zero of ZavaShop's AI rollout — get one agent running first.

### Data this LAB consumes

All real numbers are loaded from [`workshop/data/`](../data/README.md). For LAB 1 the relevant fixtures are:

- [`warehouses.json`](../data/warehouses.json) — 5 fulfillment centers; this LAB defaults to `SEA-01` (Seattle).
- [`skus.json`](../data/skus.json) — 10 SKUs incl. `SKU-7421` (Nordic Linen Duvet Set) and `SKU-3055` (Terracotta Garden Pot).
- [`inventory.json`](../data/inventory.json) — on-hand / reserved / reorder point per (SKU, warehouse). `SKU-7421 @ SEA-01 = 312 on-hand` is what the agent should quote.
- [`purchase_orders.json`](../data/purchase_orders.json) — 6 POs incl. `PO-20260518-001` (in_transit, ETA 2026-05-26) and `PO-20260519-007` (customs_clearing, ETA 2026-05-27).

---

## Learning goals

By the end of this LAB you will:

- Use `AzureAIAgentsProvider` to create a **persistent (server-side) Agent**.
- Write both **sync and async** `function tool`s and hand them to the agent.
- Use `HostedMCPTool` to wire in a tool set served by an **MCP server**.
- Use `async with` + `AzureCliCredential` to manage the lifecycle of credential / client / agent.
- Use `AgentThread` to keep multi-turn context.

---

## Microsoft Learn references

Agent Framework topics involved in this LAB (Copilot already has them offline in the SKILL; the corresponding Learn chapters are listed here for follow-up reading):

- [Agent Framework — overview & quick start](https://learn.microsoft.com/en-us/agent-framework/overview/agent-framework-overview/) · [Agent Framework — tools overview](https://learn.microsoft.com/en-us/agent-framework/agents/tools/index)
- [Function tools (`AIFunctionFactory.Create` / `@tool`)](https://learn.microsoft.com/en-us/agent-framework/agents/tools/function-tools)
- [Local MCP tools (`McpClient`, `ListToolsAsync`)](https://learn.microsoft.com/en-us/agent-framework/agents/tools/local-mcp-tools)
- [Microsoft Foundry provider — hosted agents & threads](https://learn.microsoft.com/en-us/agent-framework/agents/providers/microsoft-foundry)
- [Foundry agents — Function calling](https://learn.microsoft.com/en-us/azure/foundry/agents/how-to/tools/function-calling) · [Foundry agents — Connect to MCP servers](https://learn.microsoft.com/en-us/azure/foundry/agents/how-to/tools/model-context-protocol)

> SKILL references: [references/tools.md](../../.github/skills/agent-framework-azure-ai-py/references/tools.md), [references/mcp.md](../../.github/skills/agent-framework-azure-ai-py/references/mcp.md), [references/threads.md](../../.github/skills/agent-framework-azure-ai-py/references/threads.md)

---

## Tasks

### Step 1 — Pick the ZavaShop Coding Agent in Agent Mode

In VS Code Copilot Chat, switch to **Agent Mode**, open the agent picker, and select **`zavashop-coding-agent`**. Then send a prompt that names the LAB **and** the language you want:

```
I'm doing LAB 1 in Python — build the ZavaShop inventory agent Zara.
```

> Do not prefix the message with `@zavashop-coding-agent`. The agent is chosen from the Agent Mode dropdown; the chat text is just your task description.

The Coding Agent will follow its 7-step loop automatically:

1. `read_file` [`.github/skills/agent-framework-azure-ai-py/SKILL.md`](../../.github/skills/agent-framework-azure-ai-py/SKILL.md), then load [`references/tools.md`](../../.github/skills/agent-framework-azure-ai-py/references/tools.md), [`references/mcp.md`](../../.github/skills/agent-framework-azure-ai-py/references/mcp.md), [`references/threads.md`](../../.github/skills/agent-framework-azure-ai-py/references/threads.md) as needed.
2. `read_file` this LAB README.
3. Create `zara_agent.py` under [`workshop/LAB01-inventory-agent/`](.):
   - `Agent` + `FoundryChatClient` + `AzureCliCredential` + `async with` (the supported pattern in the installed `agent-framework`; `AzureAIAgentsProvider` is documented in the SKILL but not exported in the current prerelease build).
   - Endpoint + model name loaded from [`workshop/.env`](../.env) via a small `load_env()` helper, then read from `os.environ["FOUNDRY_PROJECT_ENDPOINT"]` / `os.environ["FOUNDRY_MODEL"]`.
   - Tools: `get_stock(sku, warehouse)` + `get_po_status(po_number)` + `find_open_pos_by_sku(sku, warehouse=None)` + an `MCPStreamableHTTPTool` pointed at `https://learn.microsoft.com/api/mcp`.
   - An `AgentSession` (via `agent.create_session()`) running a 3-turn conversation.
4. `get_errors` for syntax, then `runCommands` to smoke-test `python zara_agent.py`.
5. Tick off each acceptance criterion below.

> If you'd rather write the code by hand, you can — and then re-select `zavashop-coding-agent` in Agent Mode to run steps 6 and 7 only.

### Step 2 — Implement the function tools + a small `load_env()` helper

In `zara_agent.py`, implement the data-backed tools and a helper that reads [`workshop/.env`](../.env) into `os.environ` so the script picks up `FOUNDRY_PROJECT_ENDPOINT` and `FOUNDRY_MODEL` without you having to `export` them in every shell:

```python
import os
import sys
from pathlib import Path

# Make the shared fixtures importable
sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "data"))
from zava_data import find_po, find_stock, load_purchase_orders


def load_env() -> None:
    """Load workshop/.env into process environment when available."""
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
    """List open (not-yet-delivered) POs for a SKU, newest ETA first."""
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

> **Do not inline mock dicts.** Every lookup comes from the shared fixtures under [`workshop/data/`](../data/README.md), so every LAB agrees on what `SKU-7421 @ SEA-01` means. To extend the dataset, edit those JSON files — not the Python.

> **Why a third tool?** Turn 2 asks for the most recent open PO for the SKU mentioned in Turn 1. `get_po_status` only resolves a *known* PO number, so without `find_open_pos_by_sku` the model has no way to enumerate open POs. Adding it keeps Zara honest to the rule "answer using real data from the tools".

The fixtures already cover `SKU-7421`, `SKU-3055`, `PO-20260518-001`, `PO-20260519-007` (the IDs Mei asks about), so you can run the conversation in Step 4 against real data right away.

### Step 3 — Create the agent and mount the MCP tool

The Microsoft Agent Framework SDK currently shipped on PyPI exposes `Agent` + `FoundryChatClient` (from `agent_framework.foundry`) and `MCPStreamableHTTPTool` (from `agent_framework`). Use those as the primary path — the `AzureAIAgentsProvider` / `HostedMCPTool` snippets in the SKILL are kept for reference but are not exported in the current prerelease.

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
            "You are Zara, the warehouse assistant for ZavaShop's Seattle fulfillment center. "
            "Always answer using real data from the tools and never invent stock or PO numbers. "
            "To find the latest unarrived PO for a SKU, call find_open_pos_by_sku first, then "
            "get_po_status for fuller detail."
        ),
        tools=[get_stock, get_po_status, find_open_pos_by_sku, learn_mcp],
    ) as agent,
):
    ...
```

### Step 4 — Run a 3-turn conversation on an `AgentSession`

Mimic Mei's real-world flow (the questions must be asked in order to verify the agent keeps context):

```python
session = agent.create_session()

# Turn 1: ask about stock directly
await agent.run("How many SKU-7421 do we have left at SEA-01?", session=session)

# Turn 2: follow-up that does NOT repeat the SKU (must inherit it from context)
await agent.run("What is the most recent PO for that SKU that hasn't arrived yet?", session=session)

# Turn 3: call the MCP
await agent.run("Search the Microsoft Learn MCP for best practices on Azure AI Foundry.", session=session)
```

> The session object replaces the older `AgentThread`/`get_new_thread()` API mentioned in the SKILL — both serve the same role (server-side conversation persistence).

### Step 5 — Run it

```bash
cd workshop/LAB01-inventory-agent
python zara_agent.py
```

---

## Acceptance criteria

- [ ] The console shows three replies, and Turn 2 succeeds without you repeating the SKU (the `AgentSession` carries the context).
- [ ] The stock and PO numbers in Turns 1 and 2 match the fixtures (`SKU-7421 @ SEA-01 = 312 on-hand`; `PO-20260518-001` is `in_transit` with ETA `2026-05-26`) — this proves the tools were really called and that no agent hallucinated the numbers.
- [ ] The Turn 3 reply clearly contains Microsoft Learn content (proves the MCP was called).
- [ ] `FOUNDRY_PROJECT_ENDPOINT` / `FOUNDRY_MODEL` are read from `os.environ` after `load_env()`; nothing is hardcoded.
- [ ] No manual `close()` anywhere — credential, MCP tool and agent all go through `async with`.
- [ ] `zara_agent.py` contains **no inline mock dict** — stock / PO data is read through `zava_data.find_stock` / `zava_data.find_po` / `zava_data.load_purchase_orders`.

---

## .NET implementation path

For learners on the .NET track. Same story, same fixtures, same acceptance criteria.

### Step 1 — Pick the ZavaShop Coding Agent in Agent Mode (C#)

In VS Code Copilot Chat → **Agent Mode** → agent picker → **`zavashop-coding-agent`**, then send:

```
I'm doing LAB 1 in C# — build the ZavaShop inventory agent Zara.
```

> Do not prefix with `@zavashop-coding-agent` — the language is what tells the agent which SKILL to load.

The Coding Agent will:

1. `read_file` [`.github/skills/agent-framework-azure-ai-csharp/SKILL.md`](../../.github/skills/agent-framework-azure-ai-csharp/SKILL.md) and pull [`references/tools.md`](../../.github/skills/agent-framework-azure-ai-csharp/references/tools.md), [`references/mcp.md`](../../.github/skills/agent-framework-azure-ai-csharp/references/mcp.md), [`references/threads.md`](../../.github/skills/agent-framework-azure-ai-csharp/references/threads.md).
2. `read_file` this LAB README.
3. Create `ZaraAgent/` under [`workshop/LAB01-inventory-agent/`](.): a `Microsoft.NET.Sdk` console project targeting `net10.0`, with `..\..\data\ZavaData.cs` linked.
4. Tools: `GetStock(sku, warehouse)` + `GetPoStatus(po_number)` + `FindOpenPosBySku(sku, warehouse?)` via `AIFunctionFactory.Create(...)`, plus a **local MCP** tool (`McpClient` + `mcpTools.Cast<AITool>()`) pointed at `https://learn.microsoft.com/api/mcp`. Hosted MCP via `ResponseTool.CreateMcpTool(...)` is **not** usable with the stateless `AsAIAgent(model, ..., tools: [...])` overload — see [references/mcp.md](../../.github/skills/agent-framework-azure-ai-csharp/references/mcp.md).
5. Endpoint + model are loaded from [`workshop/.env`](../.env) via a small `LoadEnv()` helper, then read from `Environment.GetEnvironmentVariable("FOUNDRY_PROJECT_ENDPOINT")` / `("FOUNDRY_MODEL")`.
6. Run a 3-turn `AgentSession` conversation.
7. `get_errors` → `dotnet build` → `dotnet run`.

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

> Use `ModelContextProtocol` (the local-MCP client) instead of `OpenAI` here. The hosted MCP wrapper in `OpenAI.Responses` does not satisfy the `AITool` constraint of the stateless `AsAIAgent(...)` overload — see Step 4.

### Step 3 — Implement the function tools + a small `LoadEnv()` helper (C#)

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

[Description("List open (not-yet-delivered) Purchase Orders for a SKU, newest ETA first.")]
static string FindOpenPosBySku(
    [Description("SKU id, e.g. SKU-7421.")] string sku,
    [Description("Optional warehouse id; pass an empty string to ignore.")] string warehouse = "")
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

> **Do not inline mock dicts.** Same rule as Python: every lookup comes through [`workshop/data/ZavaData.cs`](../data/ZavaData.cs) so all LABs agree on what `SKU-7421 @ SEA-01` means.

> **Why a third tool?** Same reason as the Python track — Turn 2 asks for the most recent open PO for the SKU mentioned in Turn 1. `GetPoStatus` only resolves a *known* PO number; `FindOpenPosBySku` is what lets Zara enumerate. Without it, Turn 2 will hallucinate.

### Step 4 — Create the agent and mount the MCP tool (C#)

The stateless `AIProjectClient.AsAIAgent(model, instructions, name, tools: [...])` overload accepts anything that implements `Microsoft.Extensions.AI.AITool`. Local MCP via `McpClient` returns `McpClientTool` instances that satisfy that contract; the hosted MCP wrapper `ProjectsAgentTool.AsProjectTool(ResponseTool.CreateMcpTool(...))` does **not**, so we use the local path here (mirrors the Python track's `MCPStreamableHTTPTool`):

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
        "You are Zara, the warehouse assistant for ZavaShop's Seattle fulfillment center. " +
        "Always answer using real data from the tools and never invent stock or PO numbers. " +
        "To find the latest unarrived PO for a SKU, call FindOpenPosBySku first, then " +
        "GetPoStatus for fuller detail.",
    name: "Zara",
    tools: tools);
```

### Step 5 — Run a 3-turn conversation on an AgentSession

```csharp
AgentSession session = await agent.CreateSessionAsync();

Console.WriteLine(await agent.RunAsync(
    "How many SKU-7421 do we have left at SEA-01?", session));

Console.WriteLine(await agent.RunAsync(
    "What is the most recent PO for that SKU that hasn't arrived yet?", session));

Console.WriteLine(await agent.RunAsync(
    "Search the Microsoft Learn MCP for best practices on Azure AI Foundry.", session));
```

### Step 6 — Run it

```bash
cd workshop/LAB01-inventory-agent/ZaraAgent
dotnet run
```

The acceptance criteria above apply unchanged — same `on_hand=312`, same `PO-20260518-001` in_transit ETA `2026-05-26`, same Learn-MCP content in turn 3, same "no inline mock dict" rule (everything goes through `ZavaData`).

---

## Story handoff

After a day of using Zara, Mei's question count drops from 60 to 4. She drops a 🎉 in the internal Teams channel — and immediately files a new request:

> *"Pierre on the procurement team has it way worse than me. He doesn't check stock — he checks shipping schedules across 8 suppliers, and the tools are everywhere, with token swaps every time he logs in. Can you build him one too?"*

— That kicks off [LAB 2](../LAB02-procurement-toolbox/README.md).
