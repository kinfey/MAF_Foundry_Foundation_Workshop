# ZavaShop Supply-Chain Workshop

> A Chinese edition of every README in this workshop is preserved alongside as `README.zh.md`.

This workshop walks you through the **Microsoft Agent Framework + Microsoft Foundry (gpt-5.5)** stack in **five LABs**, each one delivering a runnable feature on top of a single, continuous story.

---

## 0. The story — ZavaShop

**ZavaShop** is a fictional global e-commerce company. Its catalog covers three categories:

- 🏠 **Home goods** — bedding, decor, lighting
- 🌿 **Garden & outdoor** — garden tools, BBQ grills, outdoor furniture
- 🔌 **Small home appliances** — coffee machines, air purifiers, robotic vacuums

ZavaShop has **5 fulfillment centers** (Seattle SEA-01, London LON-02, Shanghai SHA-03, São Paulo SAO-04, Dubai DXB-05) and partners with **dozens of suppliers**. Over the past year, the same pain points keep surfacing:

| Domain | Pain point |
|--------|-----------|
| Warehousing | Stock numbers are inconsistent across systems; warehouse supervisors are blocked by repetitive questions |
| Procurement | Buyers manage 8+ ERP/supplier portals daily; quotes and contract clauses are scattered, with frequent approval errors |
| Customer service | Reps don't remember VIP preferences (white-glove delivery, no cardboard, time windows…); chat models drift off-topic |
| Fulfillment | Quote → stock check → approval → dispatch → finance is fully manual; one $10k+ exception per day |
| Operations | Leadership wants a single dashboard, but every team builds their own |

The CTO has signed off on a **Foundry-as-the-brain** charter:

> *Every team rebuilds its workflow on Microsoft Agent Framework + Microsoft Foundry. The model is **gpt-5.5**. Workflows must orchestrate cross-team. The frontend is **AG-UI + React**, so the CEO can see everything from one console.*

You are the **AI Platform Engineer** at ZavaShop. The five LABs below are the five deliverables of this initiative.

---

## 1. The five LABs

| LAB | Story | Owner | What you build | SKILL |
|-----|-------|-------|---------------|-------|
| [LAB01 — Inventory Agent](workshop/LAB01-inventory-agent/README.md) | Seattle DC supervisor **Mei** gets interrupted 60× a day | Mei | Agent **Zara**: function tools + HostedMCPTool + Thread | [agent-framework-azure-ai-py](.github/skills/agent-framework-azure-ai-py/SKILL.md) |
| [LAB02 — Procurement Toolbox](workshop/LAB02-procurement-toolbox/README.md) | Shanghai senior buyer **Pierre** juggles 8 systems | Pierre | Agent **Pierre**: Foundry Toolbox + Agent Skills + approval workflow | same as above (Toolbox / Skills / Threads) |
| [LAB03 — Customer Memory & Eval](workshop/LAB03-customer-memory-eval/README.md) | CS director **Lin** wants an agent that "remembers customers and is measurable" | Lin | Agent **Aria**: Foundry Memory + Evaluation + Red-Team | same as above (Memory / Evaluation) |
| [LAB04 — Fulfillment Workflow](workshop/LAB04-fulfillment-workflow/README.md) | Fulfillment director **Diego** wants exception orders to self-orchestrate | Diego | Multi-agent **ZavaFulfillment** workflow: WorkflowBuilder + HITL + Checkpoint | [agent-framework-workflows-py](.github/skills/agent-framework-workflows-py/SKILL.md) |
| [LAB05 — Control Tower with AG-UI](workshop/LAB05-control-tower-agui/README.md) | The CEO wants a single dashboard that "feels alive" | CEO | **ZavaControlTower** AG-UI server + React frontend (covers all 7 AG-UI features) | [agent-framework-agui-py](.github/skills/agent-framework-agui-py/SKILL.md) |

Every LAB ships:

- A **story setup** so you understand *why* you are building this
- A **task list** anchored to the SKILL's best practices
- **Acceptance criteria** so you can self-verify
- A **story handoff** that connects to the next LAB

---

## 2. Common prerequisites

### 2.1 Azure / Foundry

- An **Azure subscription** with the **Microsoft Foundry** service enabled
- A Foundry project with the **gpt-5.5** model + **text-embedding-3-small** deployed
- Local Azure CLI logged in:

```bash
az login --use-device-code
```

### 2.2 Local environment

- Python **3.10+**
- Node.js **20+** (LAB05 frontend only)

```bash
python -m venv .venv
source .venv/bin/activate   # macOS / Linux
# .venv\Scripts\activate    # Windows PowerShell

pip install \
  agent-framework \
  agent-framework-azure-ai \
  agent-framework-ag-ui \
  azure-identity \
  python-dotenv \
  fastapi \
  "uvicorn[standard]"
```

> Individual LABs may add small extras (e.g. AG-UI client). Check each LAB's README for details.

### 2.3 `.env`

Create `.env` at the workspace root:

```dotenv
FOUNDRY_PROJECT_ENDPOINT=https://<your-project>.services.ai.azure.com/api/projects/<project-name>
FOUNDRY_MODEL=gpt-5.5
AZURE_OPENAI_EMBEDDING_MODEL=text-embedding-3-small

# Only required for LAB05
AGUI_SERVER_URL=http://127.0.0.1:5100/
AG_UI_API_KEY=zava-control-tower-demo-key
```

> Every LAB reads `FOUNDRY_MODEL` / `FOUNDRY_PROJECT_ENDPOINT` from the environment — **never hardcode** these in source files.

---

## 3. Three SKILL files (read in order)

| SKILL | Use it for | Read it before |
|-------|-----------|---------------|
| [agent-framework-azure-ai-py](.github/skills/agent-framework-azure-ai-py/SKILL.md) | Building agents on Foundry / Azure AI Agents Service: tools, MCP, Toolbox, Agent Skills, Memory, Evaluation, Threads | LAB01, LAB02, LAB03 |
| [agent-framework-workflows-py](.github/skills/agent-framework-workflows-py/SKILL.md) | Multi-agent workflows: WorkflowBuilder, Executor, HITL, Checkpoint, ConcurrentBuilder | LAB04 |
| [agent-framework-agui-py](.github/skills/agent-framework-agui-py/SKILL.md) | AG-UI server / client: SSE endpoints, frontend / backend tools, HITL, state, Generative UI, predictive updates | LAB05 |

> Every LAB README starts by saying *"Read the SKILL first"*. **Do not skip.**

---

## 3.5 Shared ZavaShop data ([`workshop/data/`](workshop/data/README.md))

All five LABs read the same fictional dataset from [`workshop/data/`](workshop/data/) instead of inlining mock dicts. That keeps stock numbers, PO IDs, customer preferences and freight rates consistent across labs:

| File | Used by | Highlights |
|------|---------|-----------|
| `warehouses.json` | LAB01 / LAB04 / LAB05 | 5 fulfillment centers (`SEA-01`, `LON-02`, `SHA-03`, `SAO-04`, `DXB-05`) |
| `skus.json` + `inventory.json` | LAB01 / LAB04 | 10 SKUs across home / garden / appliance + on-hand-by-warehouse rows |
| `purchase_orders.json` | LAB01 / LAB02 | 6 POs covering every common status |
| `suppliers.json` + `contracts.json` | LAB02 | 8 suppliers + 5 framework contracts (MOQ, max single-PO ceiling, tier discounts) |
| `customers.json` + `orders.json` | LAB03 / LAB04 / LAB05 | 4 customer profiles (3 VIP) + 6 cross-LAB orders |
| `carriers.json` | LAB04 / LAB05 | 5 freight carriers (FedEx / DHL / USPS / Aramex / SF Express) |
| `exceptions.json` | LAB05 | 4 open exception cases for the control tower |
| `eval_queries.jsonl` | LAB03 | 5 evaluation prompts with expected tool + expected outcome |
| `zava_data.py` | every LAB | Loader module (`find_stock`, `find_po`, `find_supplier`, `find_contract`, `find_customer`, `find_order`, `load_*`) |

In every LAB script, add the data folder to `sys.path` and call the loader:

```python
import sys
from pathlib import Path
sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "data"))
from zava_data import find_stock, find_po
```

See [workshop/data/README.md](workshop/data/README.md) for the full schema and editing rules.

---

## 4. GitHub Copilot Coding Agent + SKILL convention

This workshop ships a Coding Agent ready for GitHub Copilot Chat:

- Definition: [.github/agents/zavashop-coding-agent.agent.md](.github/agents/zavashop-coding-agent.agent.md)
- Mention it as: **`@zavashop-coding-agent`**

It already knows the LAB ↔ SKILL routing. For every LAB, the very first step in the README is:

```text
@zavashop-coding-agent I'm doing LAB X — <one-line goal>
```

When you do that, the Coding Agent will:

1. Look up the LAB ↔ SKILL routing table.
2. `read_file` the matching SKILL into context.
3. `read_file` the LAB README.
4. Generate the task plan, write the code, run validation.
5. Map each acceptance criterion in your LAB and report which ones pass.

> Use `@zavashop-coding-agent` instead of `@workspace` to start each LAB — this guarantees the SKILL is loaded before any code is written.
>
> The Coding Agent will reply in your language (English by default). If you write Chinese, it will switch automatically.

---

## 5. Suggested order

```text
LAB01  →  LAB02  →  LAB03  →  LAB04  →  LAB05
single   tools+    memory+   workflow   AG-UI
agent    Skills    Eval      (HITL)     frontend
```

- LAB01 → LAB02 → LAB03 progress from a single agent to a "real" agent (tools, Skills, memory, evaluation).
- LAB04 lifts the single agent up to a **multi-agent workflow** (with stock-check, shipping-quote, approval — reusing LAB01's tool).
- LAB05 publishes the LAB04 workflow as an **AG-UI control tower** for end-user interaction.

You can do them in 5 short sessions or in one long workshop day — your call.

---

## 6. Directory layout

```text
MAF_Foundry_Foundation_Workshop/
├── README.md                                  # ← you are here
├── README.zh.md                               # Chinese edition
├── .github/
│   ├── agents/
│   │   └── zavashop-coding-agent.agent.md   # GitHub Copilot Coding Agent
│   └── skills/
│       ├── agent-framework-azure-ai-py/
│       ├── agent-framework-workflows-py/
│       └── agent-framework-agui-py/
└── workshop/
    ├── data/                                  # 🔵 shared ZavaShop fixtures (see data/README.md)
    │   ├── zava_data.py
    │   ├── warehouses.json / skus.json / inventory.json
    │   ├── purchase_orders.json / suppliers.json / contracts.json
    │   ├── customers.json / orders.json / carriers.json
    │   └── exceptions.json / eval_queries.jsonl
    ├── LAB01-inventory-agent/
    ├── LAB02-procurement-toolbox/
    ├── LAB03-customer-memory-eval/
    ├── LAB04-fulfillment-workflow/
    └── LAB05-control-tower-agui/
```

---

## 7. Onwards

Set up your environment, then go to **[LAB01 — Inventory Agent](workshop/LAB01-inventory-agent/README.md)** to meet Mei, the warehouse supervisor desperate for help.

> One mantra to remember: **"Read the SKILL first."**
