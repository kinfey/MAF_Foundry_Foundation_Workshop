# LAB 2 — 采购代理 Pierre：Foundry Toolbox + Agent Skills + Thread

> **由 SKILL 协助**：[`agent-framework-azure-ai-py`](../../.github/skills/agent-framework-azure-ai-py/SKILL.md)
> **Foundry 模型**：`gpt-5.5`

---

## 故事

ZavaShop 上海采购办公室的 Pierre 一天要在 8 套供应商系统之间穿梭：

- 5 个供应商的 **OData / REST** API
- 2 个内部 **MCP 服务器**（合同 + 价格）
- 1 个 **审批工作流脚本**（部署到内部容器，PO 超过 10 万美金需走脚本提交）

他需要一个 Agent **既能拿到所有这些工具**，又能 **优雅地把"敏感操作（提单 / 改价）"做成可审批的技能**，还要 **跨多轮记住上下文**（比如 "上次我们说的那家义乌花盆厂"）。

CTO 给出三条硬要求：

1. 工具集要 **集中托管** —— 用 **Foundry Toolbox** 统一管理 MCP，不要让 Pierre 每次都看到一堆 URL。
2. 提单 / 改价这些"行动型能力"做成 **Agent Skill**（SDK 抽象），**默认需要人工审批**。
3. 用 **`FoundryChatClient`** + `AgentThread` 跑多轮，**不用** 每个请求都创建一个 server-side agent。
### 本 LAB 读取哪些数据

本 LAB 在 [`workshop/data/`](../data/README.zh.md) 中使用两份真数据来校验 PO 提交：

- [`suppliers.json`](../data/suppliers.json) — 8 家供应商（中 / 意 / 法 / 日 / 美）；Pierre 的 demo 选 **`SUP-001` YiwuClay**（陶土花盆）走低额 PO。
- [`contracts.json`](../data/contracts.json) — 5 份框架合同，含付款条件、MOQ、`max_single_po_usd`、阶梯折扣。**`CT-2026-Q1-YIWU` 的单 PO 上限设为 $100,000**，Step 4 最后一轮的 $125k 购单必需被这个上限拦下。
- [`skus.json`](../data/skus.json) — 与 LAB 1 共享的 SKU 目录，用于校验 PO 里的 SKU 编号。
---

## 学习目标

- 在 Foundry 项目里创建一个 **Toolbox**（一份 `MCPTool` 集合 + `require_approval="never"`）。
- 用 **`FoundryChatClient`** 创建一个**轻量、无 server 侧生命周期**的 agent。
- 通过 **`MCPStreamableHTTPTool` + `make_toolbox_header_provider`** 让 agent 拿到 Toolbox 内全部工具。
- 用 **Agent Skill (SDK 抽象)** 把"提交 PO"做成 `InlineSkill`，开启 `require_script_approval=True`。
- 在 `result.user_input_requests` 循环里捕获 `FunctionApprovalRequestContent`，用 `to_function_approval_response(...)` 模拟审批通过/拒绝。
- 用 `AgentThread` 保留 Pierre 跨问题的上下文。

---

## 对齐 Microsoft Learn

- *Agent Framework — Foundry Chat Client*
- *Foundry Toolbox & MCP*
- *Agent Skills — SDK abstractions (InlineSkill / ClassSkill / FileSkillsSource)*
- *Approval & Human-in-the-loop function calls*
- *Conversation Threads & state*

> SKILL 内部参考：[references/foundry-toolbox.md](../../.github/skills/agent-framework-azure-ai-py/references/foundry-toolbox.md)、[references/skills.md](../../.github/skills/agent-framework-azure-ai-py/references/skills.md)、[references/threads.md](../../.github/skills/agent-framework-azure-ai-py/references/threads.md)

---

## 任务清单

### Step 1 — 调用 ZavaShop Coding Agent

```
@zavashop-coding-agent I'm doing LAB 2 — build the ZavaShop procurement agent Pierre with a Foundry Toolbox + an Agent Skill that requires approval.
```

Coding Agent 会：

1. 加载 [`.github/skills/agent-framework-azure-ai-py/SKILL.md`](../../.github/skills/agent-framework-azure-ai-py/SKILL.md) + [`references/foundry-toolbox.md`](../../.github/skills/agent-framework-azure-ai-py/references/foundry-toolbox.md) + [`references/skills.md`](../../.github/skills/agent-framework-azure-ai-py/references/skills.md) + [`references/threads.md`](../../.github/skills/agent-framework-azure-ai-py/references/threads.md)。
2. 加载本 LAB README。
3. 在 [`workshop/LAB02-procurement-toolbox/`](.) 下创建两个脚本：
   - `bootstrap_toolbox.py`：在 Foundry 项目下建一个 `zavashop-procurement` Toolbox，挂 2 个 `MCPTool`（contracts + pricing），`require_approval="never"`。
   - `pierre_agent.py`：`FoundryChatClient`（不是 `AzureAIAgentsProvider`）+ `MCPStreamableHTTPTool` + `make_toolbox_header_provider` + `InlineSkill("procurement_actions")` + `SkillsProvider(require_script_approval=True)` + `AgentThread` 3 轮。
4. 在 `result.user_input_requests` 里实现审批逻辑：金额 < $100k **且** 低于 [`contracts.json`](../data/contracts.json) 中该供应商的 `max_single_po_usd` 才自动批；否则带着合同编号拒绝。
5. `get_errors` + `runCommands` 跑烟测，然后按验收标准勾选。

> Coding Agent **不会**为了“跑通”绕开 `require_script_approval=True`，这个是它的硬约束。

### Step 2 — `bootstrap_toolbox.py`

按 SKILL 中 Foundry Toolbox 那一节写。关键检查点：

- 用 `AIProjectClient(endpoint=..., credential=AzureCliCredential())` + `async with`。
- `project_client.beta.toolboxes.create_version(...)` 提交版本。
- 打印创建出的 toolbox name + URL，留给下一步用。
- 启动时打印 `len(load_suppliers())` / `len(load_contracts())`，确认 fixture 被加载了：
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

# 1. 凭据 + Toolbox 鉴权 header
credential = AzureCliCredential()
token_provider = get_bearer_token_provider(credential, "https://ai.azure.com/.default")
header_provider = make_toolbox_header_provider(token_provider)

# 2. Toolbox 工具
toolbox_tool = MCPStreamableHTTPTool(
    name="zavashop-procurement",
    url=f"{os.environ['FOUNDRY_PROJECT_ENDPOINT']}/toolboxes/zavashop-procurement",
    header_provider=header_provider,
    load_prompts=False,
)

# 3. 提单 skill（默认需要审批）
po_skill = InlineSkill(
    frontmatter=SkillFrontmatter(
        name="procurement_actions",
        description="Submit / modify Purchase Orders for approved suppliers.",
    ),
    instructions=(
        "使用 submit_po 提交采购单。提交前必须确认 SKU、数量、单价，并且必须先查询供应商合同——"
        "如果 PO 金额超过合同的 max_single_po_usd，请提议拆单而不要硬推。"
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
    instructions="你是 ZavaShop 上海采购办公室的智能采购代理 Pierre。",
    tools=[toolbox_tool],
    context_providers=[SkillsProvider(po_skill, require_script_approval=True)],
) as agent:
    thread = agent.get_new_thread()
    ...
```

> **两层护栏。** `require_script_approval=True` 拦住任何未经审批的提单；同时 `submit_po` 自己会调 `find_contract(supplier_id)`，金额超过 `max_single_po_usd` 就直接拒绝，连审批表都不走。这两层都是有意为之，不要为了 demo “跑通”去除掉。

### Step 4 — 三轮对话 + 审批循环

三轮提问都对应刚才导入的 fixture（`SUP-001` YiwuClay，合同 `CT-2026-Q1-YIWU` 单 PO 上限 $100k）：

```python
queries = [
    # 第 1 轮 —— 调 toolbox MCP 查看 YiwuClay 合同。
    "调出 SUP-001（YiwuClay）最新的合同，报一下 SKU-3055 的谈定单价。",
    # 第 2 轮 —— 小金额 PO ($1,360)，低于 $100k 上限 → 自动批。
    "可以。按谈定单价给 SUP-001 下 SKU-3055 × 200 件。",
    # 第 3 轮 —— 5000 × $25 = $125,000，超过 $100k 上限；submit_po 直接拒绝，模型应建议拆单。
    "再追加一单：同供应商 SKU-7421 × 5000 件，单价 25 美金。",
]
for q in queries:
    result = await agent.run(q, thread=thread)
    while result.user_input_requests:
        # 处理 FunctionApprovalRequestContent
        ...
```

### Step 5 — 跑通

```bash
python bootstrap_toolbox.py     # 一次性
python pierre_agent.py
```

---

## 验收标准

- [ ] Toolbox 在 Foundry 项目里能查到（控制台或 `project_client.beta.toolboxes.list_versions(...)`）。
- [ ] `bootstrap_toolbox.py` 在启动时打印了从 `workshop/data/` 加载的供应商 / 合同数量（sanity check）。
- [ ] 第 1 轮回复明显引用了 MCP 工具拿到的合同/价格内容，且引用了 `CT-2026-Q1-YIWU` 或合同中谈定的 $6.80 单价。
- [ ] 第 2 轮控制台输出 `[OK] Submitted PO ...`，且 **没有人工干预**（脚本自动批了；$1,360 < $100k）。
- [ ] 第 3 轮被 `submit_po` 自己的合同上限检查拦下（`[REJECTED] PO total $125,000 exceeds contract CT-2026-Q1-YIWU ceiling $100,000`），模型走了第二条路径（比如建议拆单），**没有任何 PO 真的被提交**。
- [ ] 整个过程只创建了一个 `FoundryChatClient` 和一个 thread，没有跑出 `AzureAIAgentsProvider`。
- [ ] `pierre_agent.py` 没有任何内联的供应商 / 合同字典 — 全部通过 `zava_data.find_supplier` / `zava_data.find_contract` 读取。

---

## 故事收尾

Pierre 第二周就把 Excel 关了。但客服总监 Lin 看到这套东西，丢过来下一个需求：

> *"我的 Aria（客服）每天接 800 单，VIP 客户的偏好（不收纸盒、忌镍合金）每个客服记法都不一样。你能让 Aria 自动记住吗？而且我要能 **量化** 她回答得好不好，最好还能跑 **红队** 攻一攻，看会不会被诱导发个折扣码出去。"*

—— 这是 [LAB 3](../LAB03-customer-memory-eval/README.md) 的开始。
