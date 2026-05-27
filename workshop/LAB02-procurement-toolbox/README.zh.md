# LAB 2 — 采购代理 Pierre：Foundry Toolbox + Agent Skills + Thread

> **由 SKILL 协助**（任选一个赛道）：
> - Python：[`agent-framework-azure-ai-py`](../../.github/skills/agent-framework-azure-ai-py/SKILL.md)
> - .NET（C#）：[`agent-framework-azure-ai-csharp`](../../.github/skills/agent-framework-azure-ai-csharp/SKILL.md)
>
> **Foundry 模型**：`gpt-5.5`

---

## 选择你的技术栈

| 赛道 | 交付物 | 需要加载的 Skill | 数据 helper |
|------|--------|---------------------|---------------|
| 🐍 **Python** | `bootstrap_toolbox.py` + `pierre_agent.py` | [`agent-framework-azure-ai-py/SKILL.md`](../../.github/skills/agent-framework-azure-ai-py/SKILL.md) + [`references/foundry-toolbox.md`](../../.github/skills/agent-framework-azure-ai-py/references/foundry-toolbox.md) + [`references/skills.md`](../../.github/skills/agent-framework-azure-ai-py/references/skills.md) + [`references/threads.md`](../../.github/skills/agent-framework-azure-ai-py/references/threads.md) | [`zava_data.py`](../data/zava_data.py) |
| 🟦 **.NET（C#）** | `BootstrapToolbox/` + `PierreAgent/` | [`agent-framework-azure-ai-csharp/SKILL.md`](../../.github/skills/agent-framework-azure-ai-csharp/SKILL.md) + [`references/foundry-toolbox.md`](../../.github/skills/agent-framework-azure-ai-csharp/references/foundry-toolbox.md) + [`references/skills.md`](../../.github/skills/agent-framework-azure-ai-csharp/references/skills.md) + [`references/threads.md`](../../.github/skills/agent-framework-azure-ai-csharp/references/threads.md) | [`ZavaData.cs`](../data/ZavaData.cs) |

Python 赛道在 [§任务清单](#任务清单)；.NET 赛道在 [§.NET 实现赛道](#net-实现赛道)。

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

## Microsoft Learn 参考

- [Agent Framework — Microsoft Foundry provider](https://learn.microsoft.com/en-us/agent-framework/agents/providers/microsoft-foundry)
- [Foundry Toolbox (preview)](https://learn.microsoft.com/en-us/azure/foundry/agents/how-to/tools/toolbox) + [Connect to MCP servers](https://learn.microsoft.com/en-us/azure/foundry/agents/how-to/tools/model-context-protocol)
- [Agent Framework — Agent Skills (Inline / Class / File sources)](https://learn.microsoft.com/en-us/agent-framework/agents/skills)
- [Agent Framework — Tool approval / human-in-the-loop](https://learn.microsoft.com/en-us/agent-framework/agents/tools/tool-approval)
- [Agent Framework — Conversations & threads](https://learn.microsoft.com/en-us/agent-framework/agents/conversations/index)

> SKILL 内部参考：[references/foundry-toolbox.md](../../.github/skills/agent-framework-azure-ai-py/references/foundry-toolbox.md)、[references/skills.md](../../.github/skills/agent-framework-azure-ai-py/references/skills.md)、[references/threads.md](../../.github/skills/agent-framework-azure-ai-py/references/threads.md)

---

## 任务清单

### Step 1 — 在 Agent Mode 里选中 ZavaShop Coding Agent

在 VS Code Copilot Chat 切到 **Agent Mode**，打开 Agent 选择器，选中 **`zavashop-coding-agent`**，然后发送一条同时点明 LAB 编号和 **使用的编程语言** 的消息：

```
I'm doing LAB 2 in Python — build the ZavaShop procurement agent Pierre with a Foundry Toolbox + an Agent Skill that requires approval.
```

> 不要再用 `@zavashop-coding-agent` 这种写法 —— Coding Agent 是从下拉里选的，对话框里只写任务描述（含 LAB 号 + 语言）。

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
- `project_client.beta.toolboxes` 在当前 prerelease 中是暴露的（`create_version` / `list` / `update` / `delete` 都在），但 `toolboxes.list()` 会命中一个服务端的 `UnicodeDecodeError`（gzip / Content-Encoding bug）。**用 `try/except Exception` 包住调用**，并在报错时打印门户供置指引，让 LAB 仍能走通——`pierre_agent.py` 在 `FOUNDRY_TOOLBOX_ENDPOINT` 没设时会回退到 Microsoft Learn MCP。
- 不要直接调 `create_version`；先用 `dir() + callable()` 把 `project_client.beta.toolboxes` 上可用的操作打印出来，让学员看到真实表面。
- 启动时打印 `len(load_suppliers())` / `len(load_contracts())`，确认 fixture 被加载了：
  ```python
  import sys
  from pathlib import Path
  sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "data"))
  from zava_data import load_suppliers, load_contracts
  ```

### Step 3 — `pierre_agent.py`

> **两个 prerelease 注意点（2026 年 5 月验证）。** 当前安装的 `agent_framework` 没有从 `agent_framework.foundry` 导出 `make_toolbox_header_provider`，在本地声明一个 5 行 helper 即可。Skill name 只能是 `[a-z0-9-]+` —— 用 `procurement-actions`，**不是** `procurement_actions`。外面也不要用 `AgentThread` / `get_new_thread()`，现在实际导出的是 `agent.create_session()` + `session=session`。

```python
import os
import sys
from collections.abc import Callable
from pathlib import Path
from typing import Any

from agent_framework import (
    Agent, MCPStreamableHTTPTool, InlineSkill, SkillFrontmatter, SkillsProvider,
)
from agent_framework.foundry import FoundryChatClient
from azure.identity import get_bearer_token_provider
from azure.identity.aio import AzureCliCredential

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "data"))
from zava_data import find_supplier, find_contract


# 1. 本地 helper，替代未导出的 `make_toolbox_header_provider`。
def make_toolbox_header_provider(
    credential: AzureCliCredential,
) -> Callable[[dict[str, Any]], dict[str, str]]:
    get_token = get_bearer_token_provider(credential, "https://ai.azure.com/.default")
    def provide(_kwargs: dict[str, Any]) -> dict[str, str]:
        return {"Authorization": f"Bearer {get_token()}"}
    return provide


# 2. 提单 skill（默认需要审批）。Skill name 必须 kebab-case。
po_skill = InlineSkill(
    frontmatter=SkillFrontmatter(
        name="procurement-actions",
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


async def main() -> None:
    # 没提供真 Toolbox endpoint 时，回退到 Microsoft Learn MCP，让接线逻辑走通。
    toolbox_url = os.environ.get(
        "FOUNDRY_TOOLBOX_ENDPOINT",
        "https://learn.microsoft.com/api/mcp",
    )
    use_real_toolbox = "FOUNDRY_TOOLBOX_ENDPOINT" in os.environ

    async with AzureCliCredential() as credential:
        mcp_kwargs: dict[str, Any] = {
            "name": "zavashop-procurement" if use_real_toolbox else "learn-mcp",
            "url": toolbox_url,
            "load_prompts": False,
        }
        if use_real_toolbox:
            mcp_kwargs["header_provider"] = make_toolbox_header_provider(credential)

        async with MCPStreamableHTTPTool(**mcp_kwargs) as toolbox_tool, Agent(
            client=FoundryChatClient(
                project_endpoint=os.environ["FOUNDRY_PROJECT_ENDPOINT"],
                model=os.environ["FOUNDRY_MODEL"],
                credential=credential,
            ),
            name="Pierre",
            instructions="你是 ZavaShop 上海采购办公室的智能采购代理 Pierre。",
            tools=[toolbox_tool],
            context_providers=[SkillsProvider(po_skill, require_script_approval=True)],
        ) as agent:
            session = agent.create_session()
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
    result = await agent.run(q, session=session)
    # 遍历所有 FunctionApprovalRequest。本 LAB 采用“自动批准所有 submit_po”策略，
    # 合同上限检查放在 submit_po 脚本里，所以第 3 轮依然被 [REJECTED] 拦住。
    while result.user_input_requests:
        for request in result.user_input_requests:
            approval_response = request.to_function_approval_response(approved=True)
            result = await agent.run(approval_response, session=session)
    print(result.text)
```

### Step 5 — 跑通

```bash
python bootstrap_toolbox.py     # 一次性
python pierre_agent.py
```

---

## 验收标准

- [ ] `bootstrap_toolbox.py` 验证了 `project_client.beta.toolboxes` 有表面（能枚举出 `create_version` / `list` / … 这些操作）。如果当前 prerelease 的 `toolboxes.list()` 丢出 `UnicodeDecodeError`，脚本会 `except` 掉并打印门户供置指引——那也算“Toolbox 表面验证通过”。
- [ ] `bootstrap_toolbox.py` 在启动时打印了从 `workshop/data/` 加载的供应商 / 合同数量（sanity check）。
- [ ] 第 1 轮回复明显引用了 MCP 工具拿到的合同/价格内容（或在 `FOUNDRY_TOOLBOX_ENDPOINT` 未设时，通过备用的 `get_contract` / `get_supplier` 函数工具），且引用了 `CT-2026-Q1-YIWU` 或合同中谈定的 $6.80 单价。
- [ ] 第 2 轮控制台输出 `[OK] Submitted PO ...`，且 **没有人工干预**（脚本自动批了；$1,360 < $100k）。
- [ ] 第 3 轮被 `submit_po` 自己的合同上限检查拦下（`[REJECTED] PO total $125,000 exceeds contract CT-2026-Q1-YIWU ceiling $100,000`），模型走了第二条路径（比如建议拆单），**没有任何 PO 真的被提交**。
- [ ] 整个过程只创建了一个 `FoundryChatClient` 和一个 `AgentSession`，没有跑出 `AzureAIAgentsProvider`。
- [ ] `pierre_agent.py` 没有任何内联的供应商 / 合同字典 — 全部通过 `zava_data.find_supplier` / `zava_data.find_contract` 读取。

---

## .NET 实现赛道

同一个故事、同一份 fixture（`suppliers.json` / `contracts.json` / `skus.json`）、同一条 `CT-2026-Q1-YIWU` $100k 上限规则。

### Step 1 — 在 Agent Mode 里选中 ZavaShop Coding Agent（C#）

在 VS Code Copilot Chat → **Agent Mode** → Agent 选择器 → **`zavashop-coding-agent`**，然后发送：

```
I'm doing LAB 2 in C# — build the ZavaShop procurement agent Pierre with a Foundry Toolbox + an Agent Skill that requires approval.
```

Coding Agent 会在 [`workshop/LAB02-procurement-toolbox/`](.) 下创建两个控制台项目：

- `BootstrapToolbox/` — 一次性脚本。.NET 当前 prerelease 把整个 toolbox 表面都暴露了出来（`AgentAdministrationClient.GetAgentToolboxes()` → `AgentToolboxes.CreateToolboxVersionAsync(...)` 等），但 `zavashop-procurement` 这个 toolbox 的名字和工具清单仍然需要先在 Foundry 门户里建好。脚本会确认表面可达、打印从 `workshop/data/` 加载到的供应商 / 合同数量，然后打印学员需要在门户里完成的步骤。
- `PierreAgent/` — 主程序：`AIProjectClient.AsAIAgent(...)` + 本地 `McpClient`（`FOUNDRY_TOOLBOX_ENDPOINT` 有值时连真 toolbox，否则回退到 Microsoft Learn MCP，让剩下的接线仍可演示）+ 一个走 `AgentSkillsProvider` 接入的 `AgentInlineSkill("procurement-actions")`（`AgentSkillsProviderOptions { ScriptApproval = true }`）+ 3 轮 `AgentSession` 对话，循环里自动审批。

两个 csproj 都要 link `..\..\data\ZavaData.cs`。

> Skill name 受 `^[a-z][a-z0-9-]*[a-z0-9]$` 校验 —— 用 kebab-case（`procurement-actions`），不要用 snake_case。
> 当前 prerelease 把 `AgentInlineSkill` 和 `AgentSkillsProvider` 都标成了 `[Experimental("MAAI001")]`，所以 `PierreAgent.csproj` 要在 `<NoWarn>` 里加上 `MAAI001`。

### Step 2 — 两层护栏的 PO 提交（C#）

inline skill 中的 `submit_po` 脚本在请求审批之前先检合同上限：

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

AgentInlineSkill procurementSkill = new AgentInlineSkill(
        name: "procurement-actions",
        description: "Submit / modify Purchase Orders for approved suppliers.",
        instructions: "Use submit_po to submit a PO. Before submitting, confirm SKU, quantity " +
                      "and unit price, and ALWAYS look up the supplier's contract first — if the " +
                      "order exceeds max_single_po_usd, propose splitting the order instead of " +
                      "forcing it through.")
    .AddScript("submit_po", SubmitPo);

AgentSkillsProvider skillsProvider = new(
    new[] { procurementSkill },
    new AgentSkillsProviderOptions { ScriptApproval = true });
```

把 `skillsProvider` 通过 `ChatClientAgentOptions.AIContextProviders` 注册，把采购相关的工具（函数工具 + MCP 工具）通过 `ChatOptions.Tools` 注册。

### Step 3 — 3 轮对话 + 审批捕获

`ScriptApproval = true` 会让每一次 `submit_po` 调用都先以 `Microsoft.Extensions.AI.ToolApprovalRequestContent` 的形式出现在 `response.Messages[*].Contents` 中。驱动循环负责把它们捞出来、自动批准，再把 `CreateResponse(...)` 应答塞回同一个 `AgentSession`。真正的拒绝逻辑发生在 `SubmitPo` 内部的合同上限检查 —— 即便在外面对调用统一放行，第 3 轮依然会以 `[REJECTED] PO total $125,000 exceeds contract CT-2026-Q1-YIWU ceiling $100,000` 回到模型那边。

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

    while (true)
    {
        var approvals = response.Messages
            .SelectMany(m => m.Contents)
            .OfType<ToolApprovalRequestContent>()
            .ToList();
        if (approvals.Count == 0) break;

        var replies = approvals
            .Select(r => (AIContent)r.CreateResponse(approved: true, reason: "demo auto-approve"))
            .ToList();
        response = await agent.RunAsync(new[] { new ChatMessage(ChatRole.User, replies) }, session);
    }

    Console.WriteLine(response);
}
```

### Step 4 — 运行

```bash
dotnet run --project workshop/LAB02-procurement-toolbox/BootstrapToolbox     # 一次性 probe + 门户步骤
dotnet run --project workshop/LAB02-procurement-toolbox/PierreAgent
```

上面的验收标准全部适用。三个 .NET 独有约束：

- 只能创建 **一个** `AIProjectClient` 和 **一个** `AgentSession`。不要为每个请求创建持久化 `FoundryAgent`。
- 任何为了让第 3 轮「跑起来」而绕开 `ScriptApproval = true` 的写法都要拒绝 —— $125k 这一轮必须被 `SubmitPo` 的合同上限检查拦下，最终输出 `[REJECTED]`，绝不能是 `[OK]`。
- 如果 `workshop/.env` 中没有设 `FOUNDRY_TOOLBOX_ENDPOINT`，agent 会回退到 Microsoft Learn MCP，让剩下的接线仍能演示。

---

## 故事收尾

Pierre 第二周就把 Excel 关了。但客服总监 Lin 看到这套东西，丢过来下一个需求：

> *"我的 Aria（客服）每天接 800 单，VIP 客户的偏好（不收纸盒、忌镍合金）每个客服记法都不一样。你能让 Aria 自动记住吗？而且我要能 **量化** 她回答得好不好，最好还能跑 **红队** 攻一攻，看会不会被诱导发个折扣码出去。"*

—— 这是 [LAB 3](../LAB03-customer-memory-eval/README.md) 的开始。
