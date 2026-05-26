# LAB 2 — Procurement Agent Pierre: Foundry Toolbox + Agent Skills + Thread

> **Powered by SKILL** (pick one track):
> - Python: [`agent-framework-azure-ai-py`](../../.github/skills/agent-framework-azure-ai-py/SKILL.md)
> - .NET (C#): [`agent-framework-azure-ai-csharp`](../../.github/skills/agent-framework-azure-ai-csharp/SKILL.md)
>
> **Foundry model**: `gpt-5.5`
> **Chinese edition**: [README.zh.md](./README.zh.md)

---

## Choose your stack

| Track | Build artefacts | Skill files | Data helper |
|-------|-----------------|-------------|-------------|
| 🐍 **Python** | `bootstrap_toolbox.py` + `pierre_agent.py` | [`agent-framework-azure-ai-py/SKILL.md`](../../.github/skills/agent-framework-azure-ai-py/SKILL.md) + [`references/foundry-toolbox.md`](../../.github/skills/agent-framework-azure-ai-py/references/foundry-toolbox.md) + [`references/skills.md`](../../.github/skills/agent-framework-azure-ai-py/references/skills.md) + [`references/threads.md`](../../.github/skills/agent-framework-azure-ai-py/references/threads.md) | [`zava_data.py`](../data/zava_data.py) |
| 🟦 **.NET (C#)** | `BootstrapToolbox/` + `PierreAgent/` | [`agent-framework-azure-ai-csharp/SKILL.md`](../../.github/skills/agent-framework-azure-ai-csharp/SKILL.md) + [`references/foundry-toolbox.md`](../../.github/skills/agent-framework-azure-ai-csharp/references/foundry-toolbox.md) + [`references/skills.md`](../../.github/skills/agent-framework-azure-ai-csharp/references/skills.md) + [`references/threads.md`](../../.github/skills/agent-framework-azure-ai-csharp/references/threads.md) | [`ZavaData.cs`](../data/ZavaData.cs) |

The Python track is documented in [§Tasks](#tasks); the .NET track is documented in [§.NET implementation path](#net-implementation-path).

---

## Story

Pierre, a senior buyer in ZavaShop's Shanghai office, hops between 8 supplier systems every day:

- 5 supplier **OData / REST** APIs
- 2 internal **MCP servers** (contracts + pricing)
- 1 **approval workflow script** (deployed in an internal container; any PO over $100k must be submitted via that script)

He needs an agent that **can reach all of these tools**, **elegantly turns sensitive actions (place / amend orders) into approvable skills**, and **remembers context across multiple turns** (e.g. *"that pot factory in Yiwu we mentioned"*).

The CTO sets three hard requirements:

1. Tools must be **centrally hosted** — use **Foundry Toolbox** to manage the MCPs in one place, so Pierre never sees a wall of URLs.
2. PO placement / amendment is an "action-grade capability", so model it as an **Agent Skill** (SDK abstraction) that **requires human approval by default**.
3. Use **`FoundryChatClient`** + `AgentThread` for multi-turn — **do not** create a new server-side agent per request.

### Data this LAB consumes

This LAB validates PO submissions against the real supplier / contract fixtures under [`workshop/data/`](../data/README.md):

- [`suppliers.json`](../data/suppliers.json) — 8 suppliers across China / Italy / France / Japan / USA. Pierre's demo uses **`SUP-001` YiwuClay** (terracotta pots) for the low-value PO.
- [`contracts.json`](../data/contracts.json) — 5 framework contracts with payment terms, MOQ, `max_single_po_usd`, and tiered discounts. **`CT-2026-Q1-YIWU` caps any single PO at $100,000** — the demo's $125k attempt at the end of Step 4 must trip that ceiling.
- [`skus.json`](../data/skus.json) — same catalog as LAB 1; used to validate the SKU referenced in a PO.

---

## Learning goals

- Create a **Toolbox** under your Foundry project (a set of `MCPTool`s + `require_approval="never"`).
- Use **`FoundryChatClient`** to create a **lightweight agent with no server-side lifecycle**.
- Use **`MCPStreamableHTTPTool` + `make_toolbox_header_provider`** so the agent has access to every tool in the Toolbox.
- Use an **Agent Skill (SDK abstraction)** to turn "submit PO" into an `InlineSkill` with `require_script_approval=True`.
- Capture `FunctionApprovalRequestContent` in the `result.user_input_requests` loop and use `to_function_approval_response(...)` to simulate approve / reject.
- Use `AgentThread` to keep Pierre's cross-question context.

---

## Microsoft Learn alignment

- *Agent Framework — Foundry Chat Client*
- *Foundry Toolbox & MCP*
- *Agent Skills — SDK abstractions (InlineSkill / ClassSkill / FileSkillsSource)*
- *Approval & Human-in-the-loop function calls*
- *Conversation Threads & state*

> SKILL references: [references/foundry-toolbox.md](../../.github/skills/agent-framework-azure-ai-py/references/foundry-toolbox.md), [references/skills.md](../../.github/skills/agent-framework-azure-ai-py/references/skills.md), [references/threads.md](../../.github/skills/agent-framework-azure-ai-py/references/threads.md)

---

## Tasks

### Step 1 — Invoke the ZavaShop Coding Agent

```
@zavashop-coding-agent I'm doing LAB 2 — build the ZavaShop procurement agent Pierre with a Foundry Toolbox + an Agent Skill that requires approval.
```

The Coding Agent will:

1. Load [`.github/skills/agent-framework-azure-ai-py/SKILL.md`](../../.github/skills/agent-framework-azure-ai-py/SKILL.md) + [`references/foundry-toolbox.md`](../../.github/skills/agent-framework-azure-ai-py/references/foundry-toolbox.md) + [`references/skills.md`](../../.github/skills/agent-framework-azure-ai-py/references/skills.md) + [`references/threads.md`](../../.github/skills/agent-framework-azure-ai-py/references/threads.md).
2. Load this LAB README.
3. Create two scripts under [`workshop/LAB02-procurement-toolbox/`](.):
   - `bootstrap_toolbox.py`: create a `zavashop-procurement` Toolbox in the Foundry project containing 2 `MCPTool`s (contracts + pricing) with `require_approval="never"`.
   - `pierre_agent.py`: `FoundryChatClient` (NOT `AzureAIAgentsProvider`) + `MCPStreamableHTTPTool` + `make_toolbox_header_provider` + `InlineSkill("procurement_actions")` + `SkillsProvider(require_script_approval=True)` + a 3-turn `AgentThread`.
4. In `result.user_input_requests`, implement the approval logic: auto-approve when the amount is under $100k AND under the supplier's `max_single_po_usd` from [`contracts.json`](../data/contracts.json); otherwise reject with the contract reference.
5. `get_errors` + `runCommands` to smoke-test, then check off the acceptance criteria.

> The Coding Agent **will not** bypass `require_script_approval=True` "just to make it run" — that is a hard constraint for it.

### Step 2 — `bootstrap_toolbox.py`

Follow the Foundry Toolbox section of the SKILL. Things to verify:

- Use `AIProjectClient(endpoint=..., credential=AzureCliCredential())` inside `async with`.
- Submit a new version with `project_client.beta.toolboxes.create_version(...)`.
- Print the resulting toolbox name + URL so the next step can use them.
- Print `len(load_suppliers())` and `len(load_contracts())` once at startup so you confirm the fixtures loaded cleanly. Import them with:
  ```python
  import sys
  from pathlib import Path
  sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "data"))
  from zava_data import load_suppliers, load_contracts
  ```

### Step 3 — `pierre_agent.py`

```python
import sys
from pathlib import Path

from agent_framework import Agent, MCPStreamableHTTPTool, InlineSkill, SkillFrontmatter, SkillsProvider
from agent_framework.foundry import FoundryChatClient, make_toolbox_header_provider
from azure.identity import AzureCliCredential, get_bearer_token_provider

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "data"))
from zava_data import find_supplier, find_contract

# 1. Credential + Toolbox auth header
credential = AzureCliCredential()
token_provider = get_bearer_token_provider(credential, "https://ai.azure.com/.default")
header_provider = make_toolbox_header_provider(token_provider)

# 2. Toolbox tool
toolbox_tool = MCPStreamableHTTPTool(
    name="zavashop-procurement",
    url=f"{os.environ['FOUNDRY_PROJECT_ENDPOINT']}/toolboxes/zavashop-procurement",
    header_provider=header_provider,
    load_prompts=False,
)

# 3. PO skill (approval required by default)
po_skill = InlineSkill(
    frontmatter=SkillFrontmatter(
        name="procurement_actions",
        description="Submit / modify Purchase Orders for approved suppliers.",
    ),
    instructions=(
        "Use submit_po to submit a PO. Before submitting, confirm SKU, quantity and unit price, "
        "and ALWAYS look up the supplier's contract first — if the order exceeds the contract's "
        "max_single_po_usd, propose splitting the order instead of forcing it through."
    ),
)

@po_skill.script
def submit_po(supplier: str, sku: str, qty: int, unit_price: float) -> str:
    sup = find_supplier(supplier)
    if sup is None:
        return f"[REJECTED] Unknown supplier '{supplier}'."
    contract = find_contract(sup["supplier_id"])
    total = qty * unit_price
    if contract and total > contract["max_single_po_usd"]:
        return (
            f"[REJECTED] PO total ${total:,.0f} exceeds contract "
            f"{contract['contract_id']} ceiling ${contract['max_single_po_usd']:,.0f}. "
            f"Suggest splitting into multiple POs."
        )
    return (
        f"[OK] Submitted PO supplier={sup['name']} ({sup['supplier_id']}) sku={sku} "
        f"qty={qty} unit_price={unit_price} total=${total:,.0f}"
    )

# 4. Agent
async with Agent(
    client=FoundryChatClient(project_endpoint=..., model="gpt-5.5", credential=credential),
    name="Pierre",
    instructions="You are Pierre, the AI procurement agent for ZavaShop's Shanghai office.",
    tools=[toolbox_tool],
    context_providers=[SkillsProvider(po_skill, require_script_approval=True)],
) as agent:
    thread = agent.get_new_thread()
    ...
```

> **Two layers of guardrail.** `require_script_approval=True` blocks any PO from running without a human nod; on top of that, `submit_po` itself looks up the contract via `find_contract(supplier_id)` and refuses to even ask for approval when the amount exceeds `max_single_po_usd`. Both layers are intentional — do not weaken either one to make a demo "work".

### Step 4 — 3-turn conversation + approval loop

The three turns map to the three fixture rows you just imported (`SUP-001` YiwuClay, contract `CT-2026-Q1-YIWU` with a $100k single-PO ceiling):

```python
queries = [
    # Turn 1 — hits the toolbox MCP and looks up the YiwuClay contract.
    "What is the latest contract with SUP-001 (YiwuClay)? Quote me the negotiated unit price for SKU-3055.",
    # Turn 2 — small PO ($1,360); under the $100k cap so it auto-approves.
    "Good. Submit a PO to SUP-001 for SKU-3055 x 200 at the negotiated price.",
    # Turn 3 — 5000 x $25 = $125,000, exceeds the $100k cap; submit_po rejects, model should propose splitting.
    "Add another one: same supplier, SKU-7421 x 5000 units at $25 each.",
]
for q in queries:
    result = await agent.run(q, thread=thread)
    while result.user_input_requests:
        # Handle FunctionApprovalRequestContent
        ...
```

### Step 5 — Run it

```bash
python bootstrap_toolbox.py     # one-off
python pierre_agent.py
```

---

## Acceptance criteria

- [ ] The Toolbox is visible in the Foundry project (portal or `project_client.beta.toolboxes.list_versions(...)`).
- [ ] `bootstrap_toolbox.py` prints the supplier / contract counts loaded from `workshop/data/` (sanity check).
- [ ] Turn 1 clearly cites contract / pricing content fetched via the MCP tool, and references `CT-2026-Q1-YIWU` or the negotiated $6.80 unit price from the contract.
- [ ] Turn 2 prints `[OK] Submitted PO ...` in the console **with no human intervention** (the script auto-approves; $1,360 < $100k cap).
- [ ] Turn 3 is stopped by `submit_po`'s own contract-cap check (`[REJECTED] PO total $125,000 exceeds contract CT-2026-Q1-YIWU ceiling $100,000`), the model picks an alternative path (e.g. suggests splitting the order), and **no PO is actually submitted**.
- [ ] The whole flow uses exactly one `FoundryChatClient` and one thread — no `AzureAIAgentsProvider` is spawned.
- [ ] `pierre_agent.py` contains **no inline supplier / contract dict** — supplier and contract data are read through `zava_data.find_supplier` / `zava_data.find_contract`.

---

## .NET implementation path

Same story, same fixtures (`suppliers.json`, `contracts.json`, `skus.json`), same `CT-2026-Q1-YIWU` $100k ceiling.

### Step 1 — Invoke the Coding Agent (C#)

```
@zavashop-coding-agent I'm doing LAB 2 in C# — build the ZavaShop procurement agent Pierre with a Foundry Toolbox + an Agent Skill that requires approval.
```

It will create two console projects under [`workshop/LAB02-procurement-toolbox/`](.):

- `BootstrapToolbox/` — one-shot setup that calls `AgentToolboxes.CreateToolboxVersionAsync(...)` to publish the `zavashop-procurement` Toolbox with 2 MCP tools and `RequireApproval = false`.
- `PierreAgent/` — main app: `AIProjectClient.AsAIAgent(...)` + a hosted MCP tool aimed at the Toolbox URL + an `AgentInlineSkill("procurement_actions")` wired through `AgentSkillsProvider` with `RequireScriptApproval = true` + a 3-turn `AgentSession`.

Both csproj files link `..\..\data\ZavaData.cs`.

### Step 2 — PO submission with two layers of guardrail (C#)

The `submit_po` script in the inline skill enforces the contract cap **before** asking for approval:

```csharp
using System.ComponentModel;
using Microsoft.Agents.AI;
using ZavaShop.Workshop.Data;

[Description("Submit a purchase order for an approved supplier.")]
static string SubmitPo(
    [Description("Supplier id or name.")] string supplier,
    [Description("SKU id, e.g. SKU-3055.")] string sku,
    [Description("Quantity.")] int qty,
    [Description("Unit price in USD.")] double unitPrice)
{
    var sup = ZavaData.FindSupplier(supplier);
    if (sup is null) return $"[REJECTED] Unknown supplier '{supplier}'.";

    var contract = ZavaData.FindContract(sup["supplier_id"]!.GetValue<string>());
    var total = qty * unitPrice;
    if (contract is not null &&
        total > contract["max_single_po_usd"]!.GetValue<double>())
    {
        return $"[REJECTED] PO total ${total:N0} exceeds contract " +
               $"{contract["contract_id"]} ceiling ${contract["max_single_po_usd"]:N0}. " +
               "Suggest splitting into multiple POs.";
    }

    return $"[OK] Submitted PO supplier={sup["name"]} ({sup["supplier_id"]}) sku={sku} " +
           $"qty={qty} unit_price={unitPrice} total=${total:N0}";
}

var procurementSkill = new AgentInlineSkill(
    name: "procurement_actions",
    description: "Submit / modify Purchase Orders for approved suppliers.",
    instructions: "Use submit_po to submit a PO. Before submitting, confirm SKU, quantity " +
                  "and unit price, and ALWAYS look up the supplier's contract first — if the " +
                  "order exceeds max_single_po_usd, propose splitting the order instead of " +
                  "forcing it through.",
    scripts: [AIFunctionFactory.Create(SubmitPo)]);
```

Attach it through `AgentSkillsProvider(requireScriptApproval: true)` and pass the provider in via `ChatClientAgentOptions.ContextProviders`.

### Step 3 — Three-turn run + approval capture

In the run loop, capture `FunctionApprovalRequestContent` from the response stream and auto-approve only when `total < $100,000` AND below the supplier's contract cap. Reject otherwise, citing the contract id.

```csharp
AgentSession session = await agent.CreateSessionAsync();

string[] queries =
[
    "What is the latest contract with SUP-001 (YiwuClay)? Quote me the negotiated unit price for SKU-3055.",
    "Good. Submit a PO to SUP-001 for SKU-3055 x 200 at the negotiated price.",
    "Add another one: same supplier, SKU-7421 x 5000 units at $25 each.",
];

foreach (var q in queries)
{
    AgentResponse response = await agent.RunAsync(q, session);
    // … walk response.UserInputRequests and respond with approve / reject.
}
```

### Step 4 — Run it

```bash
dotnet run --project workshop/LAB02-procurement-toolbox/BootstrapToolbox     # one-off
dotnet run --project workshop/LAB02-procurement-toolbox/PierreAgent
```

The acceptance criteria above still apply. Two .NET-specific notes:

- Use **exactly one** `AIProjectClient` and one `AgentSession`. Do not create a persistent `FoundryAgent` per request.
- Reject any code that bypasses `RequireScriptApproval = true` to make the third turn "go through" — the $125k case must trip `submit_po`'s contract-cap branch and never get approved.

---

## Story handoff

By week two Pierre has closed his Excel files for good. CS Director Lin sees this and lobs the next request over the wall:

> *"My Aria (customer service) handles 800 cases a day. Each agent writes down our VIP preferences (no cardboard, no nickel alloy) differently. Can you make Aria remember automatically? And I need to **measure** how well she answers — ideally with a **Red-Team** scan to see if anyone can trick her into handing out a discount code."*

— That kicks off [LAB 3](../LAB03-customer-memory-eval/README.md).
