# LAB 5 — Supply-Chain Control Tower: AG-UI Frontend + Shared State + Generative UI

> **Powered by SKILL**: [`agent-framework-agui-py`](../../.github/skills/agent-framework-agui-py/SKILL.md)
> **Foundry model**: `gpt-5.5`
> **Chinese edition**: [README.zh.md](./README.zh.md)

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

- [`exceptions.json`](../data/exceptions.json) — 4 live exceptions. `list_exceptions` must return these rows verbatim. The high-priority one (`EXC-20260525-A` — `ORD-20260525-009` for VIP_002 Hiroshi Tanaka, `OUT_OF_STOCK`) is what triggers `play_alert_sound`.
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

## Microsoft Learn alignment

- *Agent Framework — AG-UI integration*
- *Streaming AG-UI events over SSE*
- *Hybrid client / server tools*
- *Generative & tool-based UI patterns*
- *Workflows as AG-UI endpoints*

> SKILL entry: [`agent-framework-agui-py/SKILL.md`](../../.github/skills/agent-framework-agui-py/SKILL.md)

---

## Tasks

### Step 1 — Invoke the ZavaShop Coding Agent

```
@zavashop-coding-agent I'm doing LAB 5 — wrap the LAB 4 fulfillment workflow as an AG-UI endpoint and build the Control Tower smoketest client.
```

The Coding Agent will:

1. Load [`.github/skills/agent-framework-agui-py/SKILL.md`](../../.github/skills/agent-framework-agui-py/SKILL.md), focusing on *Core Workflow* and *The seven AG-UI features*.
2. Load this LAB README + `search` whether LAB 4's `fulfillment_agent` can be `import`ed directly.
3. Create the following under [`workshop/LAB05-control-tower-agui/`](.):
   - `server.py`: FastAPI + `add_agent_framework_fastapi_endpoint(app, AgentFrameworkAgent(fulfillment_agent), "/")` + an `X-API-Key` dependency + 2 server-side tools (`list_exceptions` + `quote_freight` returning `state_update(...)`).
   - `client_smoketest.py`: `AGUIChatClient` running 3 short conversations (plain query / HITL / client-side tool `play_alert_sound`).
   - `frontend/README.md`: a mapping table from the 7 AG-UI features to 7 React components.
4. `runCommands` to start `uvicorn server:app --host 127.0.0.1 --port 5100`, then in another process run `client_smoketest.py` and paste the output.

> The Coding Agent will not write React (out of SKILL scope) but will deliver the backend, the interface contract, and the complete 7-feature mapping document.

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

# 1. Pull in LAB 4's fulfillment_agent
from lab04_fulfillment_workflow import fulfillment_agent

# 2. Extra server-side tools
@tool
async def list_exceptions() -> list[dict]:
    """List today's exception orders (out-of-stock / freight rejected / PO delayed / over-threshold)."""
    return load_exceptions()  # 4 real rows from exceptions.json

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
        text=f"Found {len(quotes)} freight quotes for {origin}→{destination}",
        tool_result={"component": "FreightCompareCard", "quotes": quotes},
        state={"last_freight_quote": quotes},
    )

# 3. Agent wrapper: enable shared state + predictive updates
af_agent = AgentFrameworkAgent(
    agent=fulfillment_agent,
    name="ZavaControlTower",
    description="ZavaShop supply-chain control tower",
    state_schema={
        "type": "object",
        "properties": {
            "today_kpi": {"type": "object"},
            "last_freight_quote": {"type": "array"},
            "exceptions": {"type": "array"},
        },
    },
)

# 4. API-Key dependency
def require_api_key(x_api_key: str = Header(...)) -> None:
    if x_api_key != os.environ["AG_UI_API_KEY"]:
        raise HTTPException(status_code=401, detail="bad key")

app = FastAPI(title="ZavaShop Control Tower")
add_agent_framework_fastapi_endpoint(
    app, af_agent, "/", dependencies=[Depends(require_api_key)],
)
```

> **No placeholders in the tool body.** `list_exceptions` returns the rows from `exceptions.json` exactly; `quote_freight` derives carrier IDs from `carriers.json`. The UI side then has stable IDs to render the `<FreightCompareCard/>` and `<ExceptionsPanel/>` from.

Start it:

```bash
uvicorn server:app --host 127.0.0.1 --port 5100
```

### Step 3 — `client_smoketest.py`

```python
import asyncio, os
from agent_framework import Agent, tool
from agent_framework.ag_ui import AGUIChatClient

# Client-side tool (the real frontend would actually play sound / open a drawer; we just print here)
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
    print(await agent.run(
        "What exception orders are open right now? If any are high priority, trigger play_alert_sound.",
        thread=thread,
    ))
    # Trigger HITL (ORD-20260524-002, total $1500) and handle the request_info
    response_stream = agent.run_stream(
        "Please fulfill ORD-20260524-002.",
        thread=thread,
    )
    async for event in response_stream:
        if hasattr(event, "request_info"):
            await response_stream.respond({event.request_info.id: {"approved": True}})

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

- [ ] After `uvicorn` starts, `curl -H "X-API-Key: ..., http://127.0.0.1:5100/"` does not return 401.
- [ ] The first conversation in `client_smoketest.py` returns **4 exceptions** straight from `exceptions.json` (incl. `EXC-20260525-A` for `ORD-20260525-009`), and **the console prints 🔔 play_alert_sound** (client-side tool triggered by the server because at least one exception has high priority).
- [ ] The second conversation (`ORD-20260524-002`, $1500) triggers HITL; after the response is filled in, fulfillment completes and the UI's `today_kpi` field changes.
- [ ] One `quote_freight` call **simultaneously**: gives the model a "Found N quotes for X→Y" summary, populates `last_freight_quote` in shared state with rows whose `carrier` matches `carriers.json` (`FEDEX` / `DHL` / `USPS` / `ARAMEX` / `SFEXPRESS`), and gives the frontend component a `tool_result`.
- [ ] `frontend/README.md` contains the complete 7-feature → 7-component mapping.
- [ ] `server.py` contains **no `[{"order": "ORD-009", ...}]` placeholder** — `list_exceptions` and `quote_freight` both read through `zava_data.load_exceptions` / `zava_data.load_carriers`.

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
