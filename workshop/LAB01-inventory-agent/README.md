# LAB 1 — Warehouse Assistant Zara: Single Agent + Function Tools + MCP

> **Powered by SKILL**: [`agent-framework-azure-ai-py`](../../.github/skills/agent-framework-azure-ai-py/SKILL.md)
> **Foundry model**: `gpt-5.5`
> **Chinese edition**: [README.zh.md](./README.zh.md)

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

## Microsoft Learn alignment

Agent Framework topics involved in this LAB (Copilot already has them offline in the SKILL; the corresponding Learn chapters are listed here for follow-up reading):

- *Agent Framework — Quickstart for Azure AI Foundry Agents*: [learn.microsoft.com/agent-framework/quickstart-azure-ai](https://learn.microsoft.com/agent-framework/)
- *Function Calling & Tools*
- *Hosted Tools — Code Interpreter / File Search / Web Search*
- *Model Context Protocol (MCP) integration*

> SKILL references: [references/tools.md](../../.github/skills/agent-framework-azure-ai-py/references/tools.md), [references/mcp.md](../../.github/skills/agent-framework-azure-ai-py/references/mcp.md), [references/threads.md](../../.github/skills/agent-framework-azure-ai-py/references/threads.md)

---

## Tasks

### Step 1 — Invoke the ZavaShop Coding Agent

In Copilot Chat (Agent mode), type:

```
@zavashop-coding-agent I'm doing LAB 1 — build the ZavaShop inventory agent Zara.
```

The Coding Agent will follow its 7-step loop automatically:

1. `read_file` [`.github/skills/agent-framework-azure-ai-py/SKILL.md`](../../.github/skills/agent-framework-azure-ai-py/SKILL.md), then load [`references/tools.md`](../../.github/skills/agent-framework-azure-ai-py/references/tools.md), [`references/mcp.md`](../../.github/skills/agent-framework-azure-ai-py/references/mcp.md), [`references/threads.md`](../../.github/skills/agent-framework-azure-ai-py/references/threads.md) as needed.
2. `read_file` this LAB README.
3. Create `zara_agent.py` under [`workshop/LAB01-inventory-agent/`](.):
   - `AzureAIAgentsProvider` + `AzureCliCredential` + `async with`
   - Model name read from `os.environ["FOUNDRY_MODEL"]` (already set to `gpt-5.5`)
   - Tools: `get_stock(sku, warehouse)` + `get_po_status(po_number)` + `HostedMCPTool` pointed at `https://learn.microsoft.com/api/mcp`
   - `AgentThread` running a 3-turn conversation
4. `get_errors` for syntax, then `runCommands` to smoke-test `python zara_agent.py`.
5. Tick off each acceptance criterion below.

> If you'd rather write the code by hand, you can — and then ask `@zavashop-coding-agent` to run steps 6 and 7 only.

### Step 2 — Implement the two function tools

In `zara_agent.py`, implement (Copilot should follow the SKILL and produce typed signatures + docstrings):

```python
import sys
from pathlib import Path

# Make the shared fixtures importable
sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "data"))
from zava_data import find_stock, find_po


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
```

> **Do not inline mock dicts.** Every lookup comes from the shared fixtures under [`workshop/data/`](../data/README.md), so every LAB agrees on what `SKU-7421 @ SEA-01` means. To extend the dataset, edit those JSON files — not the Python.

The fixtures already cover `SKU-7421`, `SKU-3055`, `PO-20260518-001`, `PO-20260519-007` (the IDs Mei asks about), so you can run the conversation in Step 4 against real data right away.

### Step 3 — Create the agent and mount the MCP tool

```python
from agent_framework import HostedMCPTool, ChatAgent
from agent_framework.azure import AzureAIAgentsProvider
from azure.identity.aio import AzureCliCredential

async with (
    AzureCliCredential() as credential,
    AzureAIAgentsProvider(async_credential=credential) as provider,
):
    async with provider.create_agent(
        name="Zara",
        instructions=(
            "You are Zara, the warehouse assistant for ZavaShop's Seattle fulfillment center. "
            "Always answer using real data from the tools — never make up stock numbers."
        ),
        tools=[
            get_stock,
            get_po_status,
            HostedMCPTool(
                name="learn-mcp",
                url="https://learn.microsoft.com/api/mcp",
            ),
        ],
    ) as agent:
        ...
```

### Step 4 — Run a 3-turn conversation on a Thread

Mimic Mei's real-world flow (the questions must be asked in order to verify the agent keeps context):

```python
thread = agent.get_new_thread()

# Turn 1: ask about stock directly
await agent.run("How many SKU-7421 do we have left at SEA-01?", thread=thread)

# Turn 2: follow-up that does NOT repeat the SKU (must inherit it from context)
await agent.run("What is the most recent PO for that SKU that hasn't arrived yet?", thread=thread)

# Turn 3: call the MCP
await agent.run("Search the Microsoft Learn MCP for best practices on Azure AI Foundry.", thread=thread)
```

### Step 5 — Run it

```bash
cd workshop/LAB01-inventory-agent
python zara_agent.py
```

---

## Acceptance criteria

- [ ] The console shows three replies, and Turn 2 succeeds without you repeating the SKU.
- [ ] The stock and PO numbers in Turns 1 and 2 match the fixtures (`SKU-7421 @ SEA-01 = 312 on-hand`; `PO-20260518-001` is `in_transit` with ETA `2026-05-26`) — this proves the tools were really called and that no agent hallucinated the numbers.
- [ ] The Turn 3 reply clearly contains Microsoft Learn content (proves the MCP was called).
- [ ] No manual `close()` anywhere — everything goes through `async with`.
- [ ] `zara_agent.py` contains **no inline mock dict** — stock / PO data is read through `zava_data.find_stock` / `zava_data.find_po`.

---

## Story handoff

After a day of using Zara, Mei's question count drops from 60 to 4. She drops a 🎉 in the internal Teams channel — and immediately files a new request:

> *"Pierre on the procurement team has it way worse than me. He doesn't check stock — he checks shipping schedules across 8 suppliers, and the tools are everywhere, with token swaps every time he logs in. Can you build him one too?"*

— That kicks off [LAB 2](../LAB02-procurement-toolbox/README.md).
