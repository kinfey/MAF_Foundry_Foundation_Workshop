# LAB 5 — Supply-Chain Control Tower: AG-UI Frontend + Shared State + Generative UI

> **Powered by SKILL** (pick one track):
> - Python: [`agent-framework-agui-py`](../../.github/skills/agent-framework-agui-py/SKILL.md)
> - .NET (C#): [`agent-framework-agui-csharp`](../../.github/skills/agent-framework-agui-csharp/SKILL.md)
>
> **Foundry model**: `gpt-5.5`
> **Chinese edition**: [README.zh.md](./README.zh.md)

---

## Choose your stack

| Track | Build artefacts | Skill files | Data helper |
|-------|-----------------|-------------|-------------|
| 🐍 **Python** | `server.py` + `client_smoketest.py` | [`agent-framework-agui-py/SKILL.md`](../../.github/skills/agent-framework-agui-py/SKILL.md) | [`zava_data.py`](../data/zava_data.py) |
| 🟦 **.NET (C#)** | `ControlTower/` (ASP.NET Core) + `ControlTowerSmoke/` | [`agent-framework-agui-csharp/SKILL.md`](../../.github/skills/agent-framework-agui-csharp/SKILL.md) | [`ZavaData.cs`](../data/ZavaData.cs) |

Python track is documented in [§Tasks](#tasks); .NET track is documented in [§.NET implementation path](#net-implementation-path). Both tracks listen on `http://127.0.0.1:5100/`, use `X-API-Key: zava-control-tower-demo-key`, and back the same `exceptions.json` / `carriers.json` / `orders.json` fixtures.

---

## Story

Wrap LAB 4's `ZavaFulfillment` workflow as an **AG-UI HTTP service** so the operations manager can drive it from a browser in real time:

| Capability | Where it shows up in the control tower |
|------------|----------------------------------------|
| **Backend chat** | The manager chats directly with the fulfillment agent: "Where is ORD-002 right now?" |
| **Backend tool rendering** | Every tool call (stock / freight / finance) expands into a card in the UI |
| **HITL** | LAB 4's `request_info("approval")` becomes ✓ / ✗ buttons in the UI |
| **Generative UI** | The "exception orders" list streams into the right column as shared state |
| **Tool-based UI** | The freight-quote tool returns JSON via `state_update(...)`; the frontend renders a comparison card |
| **Client-side tools** | The frontend owns `play_alert_sound` and `open_order_drawer`, triggered by the agent |
| **Predictive state** | "Today's fulfillment KPIs" tick over with the event stream — no polling |

### Data this LAB consumes

The control tower binds to the same fixtures the rest of the workshop shares ([`workshop/data/`](../data/README.md)):

- [`exceptions.json`](../data/exceptions.json) — 4 live exceptions. `list_exceptions` must return these rows verbatim. The high-severity one (`EXC-20260525-A` — `ORD-20260525-009` for VIP_002 Hiroshi Tanaka, `OUT_OF_STOCK`) is what triggers `play_alert_sound`.
- [`carriers.json`](../data/carriers.json) — same 5 carrier rows LAB 4 used; `quote_freight` builds the `FreightCompareCard` payload from these (carrier IDs must match `FEDEX` / `DHL` / `USPS` / `ARAMEX` / `SFEXPRESS`).
- [`orders.json`](../data/orders.json) — `ORD-20260525-009` (the exception order driving the alert) plus `ORD-20260524-002` (HITL replay from LAB 4).

---

## Learning goals

- Use `add_agent_framework_fastapi_endpoint(app, target, "/")` to expose a `Workflow` / `Agent` as an AG-UI SSE endpoint.
- Write a Python-side smoketest client with `AGUIChatClient`.
- Implement **hybrid tools**: server-side (fulfillment) + client-side (sound / drawer).
- Use `state_update(text=..., tool_result=..., state=...)` so one tool feeds the model AND renders the UI AND writes shared state, all at once.
- Use `AgentFrameworkAgent` + `state_schema` + `predict_state_config` for shared state and predictive updates.
- Add minimum security to the AG-UI endpoint with `X-API-Key` + FastAPI `Depends(...)`.
- Stitch LAB 1–4 together for the last mile of the ZavaShop end-to-end demo.

---

## Microsoft Learn references

- [Agent Framework — AG-UI integration overview (all 7 protocol features)](https://learn.microsoft.com/en-us/agent-framework/integrations/ag-ui)
- [Agent Framework — Integrations index (UI frameworks)](https://learn.microsoft.com/en-us/agent-framework/integrations/index)
- [Agent Framework — Workflows overview (wrap a workflow as an AG-UI endpoint)](https://learn.microsoft.com/en-us/agent-framework/workflows/index)
- [Foundry — Agent development lifecycle (publish & monitor)](https://learn.microsoft.com/en-us/azure/foundry/agents/concepts/development-lifecycle)

> SKILL entry: [`agent-framework-agui-py/SKILL.md`](../../.github/skills/agent-framework-agui-py/SKILL.md)

---

## Tasks

### Step 1 — Pick the ZavaShop Coding Agent in Agent Mode

In VS Code Copilot Chat, switch to **Agent Mode**, open the agent picker, select **`zavashop-coding-agent`**, and send a prompt that names the LAB **and** the language:

```
I'm doing LAB 5 in Python — wrap the LAB 4 fulfillment workflow as an AG-UI endpoint and build the Control Tower smoketest client.
```

> Do not prefix with `@zavashop-coding-agent`. The agent is chosen from the dropdown; the chat text is plain task description (always state LAB number + language).

The Coding Agent will:

1. Load [`.github/skills/agent-framework-agui-py/SKILL.md`](../../.github/skills/agent-framework-agui-py/SKILL.md), focusing on *Core Workflow* and *The seven AG-UI features*.
2. Load this LAB README + `search` whether LAB 4's `fulfillment_agent` can be `import`ed directly.
3. Create the following under [`workshop/LAB05-control-tower-agui/`](.):
   - `server.py`: FastAPI + `add_agent_framework_fastapi_endpoint(app, AgentFrameworkAgent(control_tower), "/")` + an `X-API-Key` dependency + **3 server-side tools** (`list_exceptions` / `quote_freight` / `fulfill_order` — `fulfill_order` delegates to LAB 4's `fulfillment_agent` for under-threshold orders and returns an `ApprovalDialog` `tool_result` for over-threshold ones).
   - `client_smoketest.py`: `AGUIChatClient` runs **5 turns** covering all 7 AG-UI features (exceptions + client-side `play_alert_sound` → freight quote → auto fulfillment → HITL approval → HITL resolved).
   - `frontend/README.md`: a mapping table from the 7 AG-UI features to 7 React components.
4. `runCommands` to start `uvicorn server:app --host 127.0.0.1 --port 5100`, then in another process run `client_smoketest.py` and paste the output.

> The Coding Agent will not write React (out of SKILL scope) but will deliver the backend, the interface contract, and the complete 7-feature mapping document.

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
# Server-side tools (every payload routes through zava_data — no inline mocks)
# ---------------------------------------------------------------------------

@tool
async def list_exceptions() -> Any:
    """List today's open fulfillment exceptions."""
    rows = list(load_exceptions())
    high = [r for r in rows if r.get("severity") == "high"]
    return state_update(
        text=f"{len(rows)} open exceptions — {len(high)} high-severity.",
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
    """Quote freight between two regions (US / EU / APAC / META).

    Carrier IDs come straight from carriers.json (FEDEX / DHL / USPS / ARAMEX / SFEXPRESS).
    """
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
        text=f"Found {len(quotes)} freight quotes for {origin.upper()}→{destination.upper()}.",
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
    """Drive the LAB 4 workflow. Over $1000 = HITL ApprovalDialog; retry with supervisor_approval=True."""
    order = find_order(order_id)
    if order is None:
        return state_update(
            text=f"Unknown order {order_id}.",
            tool_result={"component": "FulfillmentResult", "status": "unknown", "order_id": order_id},
            state={"last_fulfillment": {"order_id": order_id, "status": "unknown"}},
        )
    total_usd = float(order.get("total_usd", 0.0))
    if total_usd >= HITL_THRESHOLD_USD and not supervisor_approval:
        return state_update(
            text=(
                f"Order {order_id} totals ${total_usd:,.2f} — over the "
                f"${HITL_THRESHOLD_USD:,.0f} HITL threshold. Awaiting supervisor approval."
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
        summary = f"{order_id} approved by supervisor; voucher issued."
    else:
        result = await fulfillment_agent.run(order_id)
        summary = getattr(result, "text", "") or f"{order_id} dispatched."
    voucher = {"status": "shipped", "order_id": order_id, "summary": summary}
    return state_update(
        text=f"Fulfilled {order_id}: shipped.",
        tool_result={"component": "FulfillmentResult", **voucher},
        state={
            "last_fulfillment": voucher,
            "today_kpi": {"orders_dispatched_today": 1, "last_order": order_id},
        },
    )


# ---------------------------------------------------------------------------
# AG-UI app — wrap a Foundry-backed control-tower Agent (NOT the LAB 4 agent)
# ---------------------------------------------------------------------------

CONTROL_TOWER_INSTRUCTIONS = (
    "You are ZavaControlTower, the supply-chain operations console for ZavaShop. "
    "Operations managers chat with you to monitor exceptions, quote freight, and "
    "dispatch orders. Always answer using the tools — never invent data. When "
    "list_exceptions returns any high-severity row, call the client-side "
    "play_alert_sound tool with level='high'. For fulfill_order on totals at or "
    "above $1000, surface the ApprovalDialog and only retry with "
    "supervisor_approval=True once the operator confirms."
)

API_KEY_HEADER = APIKeyHeader(name="X-API-Key", auto_error=False)

async def verify_api_key(api_key: str | None = Security(API_KEY_HEADER)) -> None:
    expected = os.environ.get("AG_UI_API_KEY")
    if not expected:
        return  # dev-mode: warn-only
    if not api_key:
        raise HTTPException(status_code=401, detail="Missing API key.")
    if api_key != expected:
        raise HTTPException(status_code=403, detail="Invalid API key.")

# AzureCliCredential is long-lived (it shells out to ``az`` per token request),
# so build the agent once at module import instead of per-request.
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
    description="ZavaShop supply-chain control tower (AG-UI)",
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

> **Key shape decisions** (verified against the installed `agent-framework` prerelease):
> - **Do NOT wrap `fulfillment_agent` directly** — the AG-UI agent is a new `ZavaControlTower` `Agent` whose `fulfill_order` tool delegates to `fulfillment_agent.run(order_id)` for under-threshold orders and short-circuits with `supervisor_approval=True` for the HITL-approved path. Driving the LAB 4 workflow past its `ctx.request_info` gate from inside a tool handler would require `Workflow.run(responses=...)` plumbing that is out of scope for a smoketest.
> - **No inline mocks.** `list_exceptions` / `quote_freight` / `fulfill_order` all go through `zava_data` (`load_exceptions` / `load_carriers` / `find_order`).
> - **`carriers.lanes` is a list of *region pair* codes**: `"US-EU"`, `"US-domestic"`, `"EU-META"`, etc. — NOT bare country codes. The `_lane_match` helper handles both directions plus the `domestic` shortcut.
> - **`exceptions.json` rows have `severity`** (`high` / `medium` / `low`) — not `priority` / `status`.

Start it:

```bash
export AG_UI_API_KEY=zava-control-tower-demo-key
uvicorn server:app --host 127.0.0.1 --port 5100
```

### Step 3 — `client_smoketest.py`

The installed `AGUIChatClient` does **not** accept a `headers=` kwarg. To send `X-API-Key`, wrap an `httpx.AsyncClient(headers=...)` and pass it as `http_client=`. Multi-turn state comes from `agent.create_session()` (not `agent.get_new_thread()`, which isn't exported by the installed prerelease).

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
    """Client-side tool: the real frontend would buzz a wall display; here we just print."""
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
                instructions="You are the operations manager driving the ZavaShop control tower.",
                client=remote,
                tools=[play_alert_sound],
            ) as agent:
                session = agent.create_session()
                # 1 — exceptions + client-side alert sound
                await _converse(agent, session,
                    "What exception orders are open right now? If any are high severity, "
                    "trigger play_alert_sound with level='high'.",
                    "1 list_exceptions + play_alert_sound")
                # 2 — freight quotes (tool-based UI + shared state)
                await _converse(agent, session,
                    "Get me freight quotes from US to EU for a 5 kg parcel.",
                    "2 quote_freight")
                # 3 — under-threshold fulfillment (drives LAB 4 workflow end-to-end)
                await _converse(agent, session,
                    "Please fulfill ORD-20260524-001 — well under the approval threshold.",
                    "3 fulfill_order (auto)")
                # 4 — HITL: over-threshold fulfillment
                await _converse(agent, session,
                    "Now please fulfill ORD-20260524-002 for VIP_003 — it was flagged.",
                    "4 fulfill_order (HITL: awaiting approval)")
                # 5 — operator approves; retry with supervisor_approval=True
                await _converse(agent, session,
                    "Approved by operations manager. Please re-run fulfill_order on "
                    "ORD-20260524-002 with supervisor_approval=True.",
                    "5 fulfill_order (HITL resolved)")

asyncio.run(main())
```

### Step 4 — `frontend/README.md`: the index for the React side

Write a mapping table from the 7 AG-UI features to 7 UI components, e.g.:

| AG-UI feature | What the backend does | React component |
|---------------|----------------------|-----------------|
| 1. Chat | Default SSE stream | `<ChatStream/>` |
| 2. Backend tool rendering | Tool runs on the server | `<ToolCallCard/>` |
| 3. HITL | `ctx.request_info` | `<ApprovalDialog/>` |
| 4. Generative UI | Tool streams incremental `Content` | `<StreamingMarkdown/>` |
| 5. Tool-based UI | `state_update(tool_result={"component": ...})` | `<DynamicComponent/>` |
| 6. Shared state | `state_update(state={...})` | `<StatePanel/>` |
| 7. Predictive state | `predict_state_config` | `<KpiTicker/>` |

> Writing real React is out of scope (not covered by the SKILL); just make sure `frontend/README.md` has the mapping cleanly aligned.

---

## Acceptance criteria

- [ ] After `uvicorn` starts, `curl -H "X-API-Key: zava-control-tower-demo-key" http://127.0.0.1:5100/health` returns `200 OK`; missing key → `401`; wrong key → `403`.
- [ ] Turn 1 of `client_smoketest.py` returns **4 exceptions** straight from `exceptions.json` (incl. `EXC-20260525-A` for `ORD-20260525-009`), and **the console prints 🚨🚨🚨 `[client.play_alert_sound] level=high`** (the server-side LLM is instructed to trigger the client-side tool whenever a high-severity row is present).
- [ ] Turn 2 (`quote_freight` US→EU @ 5 kg) **simultaneously**: gives the model a "Found N freight quotes for US→EU" summary, writes a `last_freight_quote` array into shared state, and ships a `tool_result.component == "FreightCompareCard"` payload. Carriers must come from `carriers.json` (`FEDEX` / `DHL` / `USPS` / `ARAMEX` / `SFEXPRESS`).
- [ ] Turn 3 (`ORD-20260524-001`, $196.50, under threshold) drives the LAB 4 workflow end-to-end — server logs show `[intake]` → `[stock_check]` → `[shipping_quote]` → `[allocator]` → `[approval] auto-approved` → `[dispatch]` → `[finance]` and the agent reports `shipped`.
- [ ] Turn 4 (`ORD-20260524-002`, $1500) returns a `tool_result.component == "ApprovalDialog"` envelope and parks `pending_approval` in shared state — fulfillment does **not** proceed.
- [ ] Turn 5 re-runs `fulfill_order` with `supervisor_approval=True` and clears the pending state; `today_kpi.last_order` ticks over to `ORD-20260524-002`.
- [ ] `frontend/README.md` contains the complete 7-feature → 7-component mapping.
- [ ] `server.py` contains **no inline mocks** — every business datum routes through `zava_data` (`load_exceptions` / `load_carriers` / `find_order`).

---

## .NET implementation path

Same seven AG-UI features, same shared state, same API key, same client smoke test.

### Step 1 — Pick the ZavaShop Coding Agent in Agent Mode (C#)

In VS Code Copilot Chat → **Agent Mode** → agent picker → **`zavashop-coding-agent`**, then send:

```
I'm doing LAB 5 in C# — expose the LAB 4 fulfillment workflow over AG-UI so the control tower can drive it.
```

It will create two projects under [`workshop/LAB05-control-tower-agui/`](.):

- [`ControlTower/`](./ControlTower/) — `Microsoft.NET.Sdk.Web` ASP.NET Core host. Hosts a Foundry-backed `ZavaControlTower` `AIAgent` (3 tools: `list_exceptions` / `quote_freight` / `fulfill_order`) over an `X-API-Key`–gated AG-UI endpoint at `/`. Re-uses LAB 4's `WorkflowFactory.Build(...)` for under-threshold fulfilment by **linking** LAB 4's `Program.cs` into this assembly.
- [`ControlTowerSmoke/`](./ControlTowerSmoke/) — `Microsoft.NET.Sdk` console smoke client built on `AGUIChatClient`. Registers a client-side `play_alert_sound` tool and runs the same 5-turn script as the Python `client_smoketest.py`.

Both csproj files link [`..\..\data\ZavaData.cs`](../data/ZavaData.cs); `ControlTower.csproj` *also* links LAB 4's [`Program.cs`](../LAB04-fulfillment-workflow/FulfillmentWorkflow/Program.cs).

### Step 2 — Reuse LAB 4 by linking source + a conditional constant

LAB 4 is one monolithic file: workflow records + executors + `WorkflowFactory.Build` + a CLI `Main`. To avoid having two `Main` methods, LAB 4 wraps **only the CLI block** with a preprocessor guard:

```csharp
// workshop/LAB04-fulfillment-workflow/FulfillmentWorkflow/Program.cs
#if !LAB05_AGUI_HOST
internal static class Program { /* CLI entry point */ }
#endif
```

LAB 5's `ControlTower.csproj` defines the constant and links the file:

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

Result: LAB 4 still builds standalone (`dotnet run` walks the HITL demo), and LAB 5 gets `WorkflowFactory.Build`, `ShippedVoucher`, every executor record — all in the same assembly. No separate class library, no NuGet packaging.

### Step 3 — `ControlTower/Program.cs` (Foundry agent + AG-UI host)

The agent is plain `AIProjectClient.AsAIAgent(...)` with three function tools. AG-UI hosting is one extension call:

```csharp
using Azure.AI.Projects;
using Azure.Identity;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Hosting;
using Microsoft.Agents.AI.Hosting.AGUI.AspNetCore;
using Microsoft.Extensions.AI;
using ZavaShop.LAB04.FulfillmentWorkflow;     // WorkflowFactory + ShippedVoucher (linked source)
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
            "You are ZavaControlTower, the supply-chain operations console for ZavaShop. " +
            "Always answer using the tools — never invent data. When list_exceptions returns " +
            "any high-severity row, call the client-side play_alert_sound tool with level=\"high\". " +
            "For fulfill_order on totals at or above $1000, surface the ApprovalDialog payload and " +
            "only retry with supervisor_approval=true once the operator confirms.",
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

> **Shape decisions that matter** (verified against the installed `Microsoft.Agents.AI.*` prereleases):
> - **No `IAGUIContext`, no `ToolResult`, no `ctx.UpdateStateAsync`.** Those are imaginary APIs. In .NET, the AG-UI bridge transports plain `AIFunction` return values — generative-UI and tool-based-UI payloads are just typed records with a `Component` discriminator (e.g. `ExceptionsResult { Component = "ExceptionsList", … }`, `FreightQuoteResult { Component = "FreightCompareCard", … }`). The client sees them as `FunctionResultContent`.
> - **Frontend tools fire via system-prompt instruction.** There is no `ctx.InvokeClientToolAsync` — the model decides to call `play_alert_sound` because the instructions told it to whenever `list_exceptions` returns a `severity: "high"` row.
> - **HITL lives in the tool, not in LAB 4's `RequestPort`.** Driving LAB 4's `RequestPort` from inside a function tool would require pumping `WorkflowEvent` while suspended on a Foundry response — out of scope. The simpler shape: `fulfill_order` checks `total_usd >= $1000` and returns an `ApprovalDialogResult` payload; the client retries with `supervisor_approval=true` and the tool synthesizes a `FulfillmentResult` voucher locally (carrier `MANUAL`, supervisor looked up from the warehouse map).
> - **Under-threshold fulfilment runs the real LAB 4 workflow.** `fulfill_order` calls `WorkflowFactory.Build(Path.Combine(Path.GetTempPath(), $"zava-lab05-{Guid.NewGuid():N}"))`, then `InProcessExecution.RunStreamingAsync(workflow, orderId, checkpoints, sessionId: …)`, and watches `WorkflowOutputEvent.Data is ShippedVoucher`. Each tool call gets its own checkpoint directory (cleaned up best-effort).
> - **`AGUIChatClient` takes a `string`, not a `Uri`.** `new AGUIChatClient(http, urlString)`. The corresponding agent is `chatClient.AsAIAgent(name, description, tools)` → an `AIAgent`.
> - **`Microsoft.Agents.AI.Foundry` is in the prerelease feed and matches `Version="*-*"`.** Add `MAAI001` + `NU1604` + `NU1902` to `<NoWarn>` to silence experimental-type and wildcard-version warnings.

### Step 4 — `ApiKeyMiddleware` (dev-warn-only, prod-strict)

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

### Step 5 — `ControlTowerSmoke/Program.cs` (5-turn client, **fresh session per turn**)

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

AGUIChatClient chatClient = new(http, serverUrl);     // string URL, NOT Uri
AIAgent agent = chatClient.AsAIAgent(
    name: "control-tower-smoke",
    description: "Operations console smoketest client.",
    tools: [playAlertSound]);

foreach ((string label, string prompt) in turns)
{
    // ⚠ Fresh AgentSession per turn — see note below.
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

> **⚠ Fresh `AgentSession` per turn — required when Foundry + AG-UI client tools meet.** AG-UI executes client tools (here, `play_alert_sound`) locally in the .NET SDK and ships the result back over SSE, but the AG-UI ASP.NET Core bridge **does not** persist that result into the Foundry-side conversation thread. After turn 1 fires a client tool, the Foundry thread retains an unmatched `function_call` and the next user message on the same session 400s with `"No tool output found for function call call_…"`. Each LAB 5 prompt is self-contained (it names the order id / region / parcel weight it cares about), so opening a fresh `AgentSession` per turn is functionally equivalent to a real UI's page transition.

### Step 6 — Run + smoke

```bash
# terminal 1 — start the AG-UI server
export AG_UI_API_KEY=zava-control-tower-demo-key
dotnet run --project workshop/LAB05-control-tower-agui/ControlTower
# [server] ZavaControlTower AG-UI endpoint listening on http://127.0.0.1:5100/

# terminal 2 — drive the 5-turn smoke
export AG_UI_API_KEY=zava-control-tower-demo-key
dotnet run --project workshop/LAB05-control-tower-agui/ControlTowerSmoke
```

Expected run highlights (verified live against `gpt-5.5`):

| Turn | Tool sequence | What you should see |
|------|---------------|---------------------|
| 1 | `list_exceptions` → `play_alert_sound(level=high)` | 4 exceptions, 2 high-severity; 🚨🚨🚨 prints **on the client console**. |
| 2 | `quote_freight(origin=US, destination=EU, weightKg=5)` | FEDEX $34.50 / 2 days; DHL $39.50 / 3 days. |
| 3 | `fulfill_order(orderId=ORD-20260524-001)` | Real LAB 4 workflow ships from SEA-01, supervisor Mei Tanaka, USPS, 5d. |
| 4 | `fulfill_order(orderId=ORD-20260524-002)` | `ApprovalDialog` for $1,500 (over $1,000 threshold). |
| 5 | `fulfill_order(orderId=ORD-20260524-002, supervisorApproval=true)` | Synthesized voucher (supervisor Mei Tanaka, carrier `MANUAL`). |

### .NET acceptance criteria

- [ ] `dotnet build` of both projects succeeds with 0 warnings / 0 errors (`NU1604;NU1902;MAAI001` suppressed).
- [ ] `curl -H "X-API-Key: zava-control-tower-demo-key" http://127.0.0.1:5100/health` returns `200 OK`; missing key → `401`; wrong key → `403`.
- [ ] Turn 1 prints **`🚨🚨🚨 [client.play_alert_sound] level=high`** on the client console (proves the AG-UI server → client frontend-tool dispatch works end-to-end).
- [ ] Turn 3 actually drives `WorkflowFactory.Build` → `InProcessExecution.RunStreamingAsync` → `ShippedVoucher` from the linked LAB 4 source — not a synthesized stub.
- [ ] Turn 4 returns a `FunctionResultContent` whose payload includes `"component": "ApprovalDialog"` and `"reason": "amount_over_threshold"`; turn 5 returns `"component": "FulfillmentResult"` with `"status": "shipped"`.
- [ ] LAB 4 still builds standalone after the `#if !LAB05_AGUI_HOST` guard (`dotnet build workshop/LAB04-fulfillment-workflow/FulfillmentWorkflow` → 0/0).
- [ ] No inline mocks — `ControlTower/Program.cs` reads all data via `ZavaData.LoadExceptions()` / `ZavaData.LoadCarriers()` / `ZavaData.FindOrder(...)`.

---

## Grand finale

Four weeks later, ZavaShop's CEO is standing in front of the control-tower wall display:

- **Zara** (LAB 1) has been running at Mei's desk for 28 days; tickets are down 92%.
- **Pierre** (LAB 2) has saved procurement 14% of contract-review time without a single misfired PO.
- **Aria** (LAB 3) lifted NPS from 3.9 → 4.7 and stabilized red-team ASR at 6%.
- The **fulfillment workflow** (LAB 4) cut average dispatch time from 38 minutes to 11.
- The **control tower** (LAB 5) launched, and the day it went live the operations manager said, for the first time: *"I can finally see clearly what automation is doing to my business."*

The CEO turns to the CTO:

> *"Next quarter, point this same architecture at ZavaShop's **logistics route optimization** and **seasonal-promotion pricing**."*

— That's the next season's workshop. This season ends here. 🎉

---

## One-click back to the top

[← Back to the workshop overview](../../README.md)
