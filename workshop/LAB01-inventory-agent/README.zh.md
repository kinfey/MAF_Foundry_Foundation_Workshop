# LAB 1 — 仓储助手 Zara：单 Agent + Function Tools + MCP

> **由 SKILL 协助**：[`agent-framework-azure-ai-py`](../../.github/skills/agent-framework-azure-ai-py/SKILL.md)
> **Foundry 模型**：`gpt-5.5`

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

## 对齐 Microsoft Learn

本 LAB 涉及的 Agent Framework 知识点（Copilot 在 SKILL 里已经离线写好；以下是对应的 Learn 章节，建议事后阅读巩固）：

- *Agent Framework — Quickstart for Azure AI Foundry Agents*：[learn.microsoft.com/agent-framework/quickstart-azure-ai](https://learn.microsoft.com/agent-framework/)
- *Function Calling & Tools*
- *Hosted Tools — Code Interpreter / File Search / Web Search*
- *Model Context Protocol (MCP) integration*

> SKILL 内部参考：[references/tools.md](../../.github/skills/agent-framework-azure-ai-py/references/tools.md)、[references/mcp.md](../../.github/skills/agent-framework-azure-ai-py/references/mcp.md)、[references/threads.md](../../.github/skills/agent-framework-azure-ai-py/references/threads.md)

---

## 任务清单

### Step 1 — 调用 ZavaShop Coding Agent

在 Copilot Chat（Agent 模式）输入：

```
@zavashop-coding-agent I'm doing LAB 1 — build the ZavaShop inventory agent Zara.
```

Coding Agent 会自动按它的 7 步循环走：

1. `read_file` 加载 [`.github/skills/agent-framework-azure-ai-py/SKILL.md`](../../.github/skills/agent-framework-azure-ai-py/SKILL.md)，需要时额外加载 [`references/tools.md`](../../.github/skills/agent-framework-azure-ai-py/references/tools.md)、[`references/mcp.md`](../../.github/skills/agent-framework-azure-ai-py/references/mcp.md)、[`references/threads.md`](../../.github/skills/agent-framework-azure-ai-py/references/threads.md)。
2. `read_file` 本 LAB README。
3. 在 [`workshop/LAB01-inventory-agent/`](.) 下创建 `zara_agent.py`：
   - `AzureAIAgentsProvider` + `AzureCliCredential` + `async with`
   - 模型读 `os.environ["FOUNDRY_MODEL"]`（已设为 `gpt-5.5`）
   - 工具：`get_stock(sku, warehouse)` + `get_po_status(po_number)` + `HostedMCPTool` 指向 `https://learn.microsoft.com/api/mcp`
   - `AgentThread` 跑 3 轮对话
4. `get_errors` 校验语法，然后 `runCommands` 跑 `python zara_agent.py` 烟测。
5. 按下面验收标准逐条勾选。

> 如果你想手动写代码也可以，最后让 `@zavashop-coding-agent` 只跑 step 6+7。

### Step 2 — 编写两个 Function Tool

在 `zara_agent.py` 里实现（Copilot 应当按 SKILL 写出符合规范的 typed signature + docstring）：

```python
import sys
from pathlib import Path

# 导入共享 fixture
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

> **不要在脚本里写 mock dict。** 所有查找都走 [`workshop/data/`](../data/README.zh.md) 下的共享 fixture，这样所有 LAB 对 `SKU-7421 @ SEA-01` 的认知会保持一致。需要扩充数据时去改 JSON，不要改 Python。

Fixture 已经覆盖了 Mei 会问的 `SKU-7421`、`SKU-3055`、`PO-20260518-001`、`PO-20260519-007`，下一步直接用真数据跑。

### Step 3 — 创建 Agent 并挂载 MCP

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
            "你是 ZavaShop 西雅图配送中心的仓储助手 Zara。"
            "回答时永远用工具拿到的真实数据，不要编造库存数字。"
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

### Step 4 — 用 Thread 跑 3 轮对话

模拟 Mei 的真实场景（必须按顺序问，验证 Agent 记住上下文）：

```python
thread = agent.get_new_thread()

# 第 1 轮：直接问库存
await agent.run("SKU-7421 在 SEA-01 还有多少？", thread=thread)

# 第 2 轮：基于上一轮的 SKU 追问（不再重复 SKU）
await agent.run("那这个 SKU 最近一张未到货的 PO 是哪张？", thread=thread)

# 第 3 轮：调 MCP
await agent.run("帮我从 Microsoft Learn MCP 搜一下 Azure AI Foundry 的最佳实践", thread=thread)
```

### Step 5 — 跑通

```bash
cd workshop/LAB01-inventory-agent
python zara_agent.py
```

---

## 验收标准

- [ ] 控制台能看到三轮的 Agent 回复，第二轮没有重新问 SKU 也能继续。
- [ ] 第一轮 / 第二轮的库存与 PO 数字与 fixture 一致（`SKU-7421 @ SEA-01 = 312 件在手`；`PO-20260518-001` 状态为 `in_transit`，ETA `2026-05-26`），说明工具被真的调用了且没有幻觉数字。
- [ ] 第三轮的回复明显带有 Microsoft Learn 内容（说明 MCP 被调用）。
- [ ] 整个脚本没有手动 `close()`，全部走 `async with`。
- [ ] `zara_agent.py` 中 **没有任何 mock dict** — 库存 / PO 数据全部通过 `zava_data.find_stock` / `zava_data.find_po` 读取。

---

## 故事收尾

Mei 用 Zara 跑了一天，问答次数从 60 次降到 4 次。她在内部 Teams 群里发了个 🎉。但她马上提了一个新需求：

> *"采购的 Pierre 比我惨多了，他要查的不是库存，是 8 家供应商的发货计划，工具一堆，登录还要换 token。能不能也给他做一个？"*

—— 这是 [LAB 2](../LAB02-procurement-toolbox/README.md) 的开始。
