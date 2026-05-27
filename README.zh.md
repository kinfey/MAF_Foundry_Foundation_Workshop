# 利用 Foundry 学习 Microsoft Agent 框架 —— ZavaShop 供应链工作坊

> 用 **[Microsoft Agent Framework](https://learn.microsoft.com/en-us/agent-framework/) + [Microsoft Foundry](https://learn.microsoft.com/en-us/azure/foundry/agents/overview)（GPT-5.5）**，由 **GitHub Copilot 内置 SKILL** 协助，从单 Agent 一路构建到多 Agent Workflow + AG-UI 前端的供应链智能体系统。

> **一份故事，两个赛道**。所有 LAB 同时提供 **Python** 与 **.NET（C#）** 两个实现赛道。同一份 fixture、同一套验收标准、同一个 Foundry 模型；可以逐 LAB 切换赛道。

---

## 故事背景：ZavaShop

**ZavaShop** 是一家虚构的全球电商，主营家居/园艺/小家电三大品类。最近半年业务遇到的痛点：

| 部门 | 痛点 |
|------|------|
| 仓储 | 仓库管理员每天被「这个 SKU 还有多少？」「这批货什么时候到？」反复打断，找不到统一入口 |
| 采购 | 供应商系统分散在 SAP/CSV/几个内部 API，采购员靠 Excel 拼数据 |
| 客服 | VIP 客户的偏好（不收纸盒、要白手套配送、忌镍合金）记不住，回答不一致 |
| 履约 | 一个订单要在 5 个系统之间跳转：库存 → 仓位 → 物流 → 通知 → 财务，没人能讲清全链路 |
| 运营 | 经理想要一个"指挥中心"，能实时看 Agent 在做什么、必要时一键介入 |

CTO 决定：用 **Microsoft Agent Framework + Microsoft Foundry** 重构整个供应链智能层，模型统一用 **GPT-5.5** 部署在 Foundry 项目里，前端走 **AG-UI 协议**对接 React 控制台。

你（学员）是 ZavaShop 的 AI 平台工程师，本 Workshop 5 个 LAB 就是把这条故事线一步步落地。

---

## Workshop 概览

> 所有 LAB 的入口都是 **在 VS Code Copilot Chat 的 Agent Mode 下拉里选中 `zavashop-coding-agent`**（定义在 [.github/agents/zavashop-coding-agent.agent.md](.github/agents/zavashop-coding-agent.agent.md)）。选中后发一句同时点明 LAB 号和语言的话，它就会按下表自动 `read_file` 加载对应的 SKILL.md，然后帮你完成任务。**不要再用 `@zavashop-coding-agent` 这种写法**。

| LAB | 故事场景 | 核心能力 | Python SKILL | C# SKILL |
|-----|---------|---------|---------------|----------|
| **LAB 1** | 仓储助手 Zara | 单 Agent + Function Tools + MCP | [`agent-framework-azure-ai-py`](.github/skills/agent-framework-azure-ai-py/SKILL.md) | [`agent-framework-azure-ai-csharp`](.github/skills/agent-framework-azure-ai-csharp/SKILL.md) |
| **LAB 2** | 采购代理 Pierre | Foundry Toolbox + Agent Skills + Thread | [`agent-framework-azure-ai-py`](.github/skills/agent-framework-azure-ai-py/SKILL.md) | [`agent-framework-azure-ai-csharp`](.github/skills/agent-framework-azure-ai-csharp/SKILL.md) |
| **LAB 3** | 客服 Aria | Foundry Memory + Evaluation + Red-Team | [`agent-framework-azure-ai-py`](.github/skills/agent-framework-azure-ai-py/SKILL.md) | [`agent-framework-azure-ai-csharp`](.github/skills/agent-framework-azure-ai-csharp/SKILL.md)（仅 Memory；Evaluation / Red-Team 仅有 Python实现，复用 Python 脚本对 C# Agent 端点跑） |
| **LAB 4** | 订单履约调度 | 多 Agent Workflow + HITL + Checkpoint | [`agent-framework-workflows-py`](.github/skills/agent-framework-workflows-py/SKILL.md) | [`agent-framework-workflows-csharp`](.github/skills/agent-framework-workflows-csharp/SKILL.md) |
| **LAB 5** | 供应链指挥中心 | AG-UI 协议 + 共享状态 + 生成式 UI | [`agent-framework-agui-py`](.github/skills/agent-framework-agui-py/SKILL.md) | [`agent-framework-agui-csharp`](.github/skills/agent-framework-agui-csharp/SKILL.md) |

每一关都会：

1. **铺故事**：ZavaShop 业务为什么需要这个能力。
2. **指定 SKILL**：在 GitHub Copilot 里输入 `@workspace use skill <name>`，让 Copilot 按官方知识点带你写代码。
3. **对齐 Microsoft Learn**：列出本 LAB 涉及到的 [Microsoft Learn — Agent Framework](https://learn.microsoft.com/agent-framework/) 知识点章节。
4. **交付物 + 验收**：每一关都要能真的跑起来。

---

## 共享数据集（[`workshop/data/`](workshop/data/README.zh.md)）

五个 LAB 共享同一份 ZavaShop 虚构数据集，不再各自硬编码 mock dict —— 让仓库编号、PO 号、客户偏好、运费率在所有 LAB 之间保持一致：

| 文件 | 使用方 | 要点 |
|------|--------|------|
| `warehouses.json` | LAB01 / LAB04 / LAB05 | 5 个履约中心（`SEA-01` / `LON-02` / `SHA-03` / `SAO-04` / `DXB-05`） |
| `skus.json` + `inventory.json` | LAB01 / LAB04 | 10 个 SKU（家居 / 园艺 / 小家电） + 各仓库在手库存 |
| `purchase_orders.json` | LAB01 / LAB02 | 6 个 PO，覆盖所有典型状态 |
| `suppliers.json` + `contracts.json` | LAB02 | 8 家供应商 + 5 份框架合同（MOQ、单 PO 上限、阶梯折扣） |
| `customers.json` + `orders.json` | LAB03 / LAB04 / LAB05 | 4 位客户档案（3 位 VIP） + 6 笔跨 LAB 订单 |
| `carriers.json` | LAB04 / LAB05 | 5 家货代（FedEx / DHL / USPS / Aramex / 顺丰） |
| `exceptions.json` | LAB05 | 4 个未关闭的异常单 |
| `eval_queries.jsonl` | LAB03 | 5 条评估提示词，含期望工具与期望结果 |
| `zava_data.py` | 所有 Python LAB | 加载模块（`find_stock` / `find_po` / `find_supplier` / `find_contract` / `find_customer` / `find_order` / `load_*`） |
| `ZavaData.cs` | 所有 .NET LAB | .NET 赛道的加载模块。镜像 `zava_data.py`；提供静态 `Load*` / `Find*`，名命空间 `ZavaShop.Workshop.Data`。每个 LAB 的 `.csproj` 以共享编译的方式引入。 |

每个 Python LAB 脚本统一这样引入：

```python
import sys
from pathlib import Path
sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "data"))
from zava_data import find_stock, find_po
```

每个 .NET LAB 项目统一这样引入：

```xml
<ItemGroup>
  <Compile Include="..\..\data\ZavaData.cs" Link="ZavaData.cs" />
</ItemGroup>
```

```csharp
using ZavaShop.Workshop.Data;

var stock = ZavaData.FindStock("SKU-7421", "SEA-01");
int onHand = stock?["on_hand"]?.GetValue<int>() ?? 0;   // 312
```

完整字段说明 / 编辑规则见 [workshop/data/README.zh.md](workshop/data/README.zh.md)。

---

## 通用前置准备

### 1. Azure / Foundry 资源

- 一个 **Azure AI Foundry Project**（取 `FOUNDRY_PROJECT_ENDPOINT`）。
- 在项目里部署：
  - 一个 **GPT-5.5** 部署，作为主对话/推理模型 (`FOUNDRY_MODEL=gpt-5.5`)。
  - 一个 **embedding** 部署（`text-embedding-3-small`），LAB 3 的 Foundry Memory 会用。
- 在你的 Azure 身份上赋 **Azure AI Developer** 角色。

### 2. 本地环境 — 选一个赛道（或两个都要）

共享 fixture 与 Coding Agent 是跨语言的。按 LAB 选择你要跑的赛道。

#### Python 赛道

```bash
# Python 3.11+
python -m venv .venv && source .venv/bin/activate

# Workshop 基线依赖
pip install --pre \
    agent-framework \
    agent-framework-azure-ai \
    agent-framework-ag-ui \
    azure-identity \
    azure-ai-projects \
    azure-ai-evaluation \
    python-dotenv \
    fastapi uvicorn

# Azure CLI 登录（所有 LAB 都用 AzureCliCredential）
az login
```

#### .NET 赛道

- **.NET 10 SDK**（`dotnet --version` ≥ `10.0.100`）
- Node.js **20+**（仅 LAB 5 前端需要）

.NET 赛道依赖 Agent Framework 的预发布 NuGet 包 — 各 C# SKILL 给出每个 LAB 的准确组合，常用的是：

```text
Microsoft.Agents.AI
Microsoft.Agents.AI.Foundry
Microsoft.Agents.AI.Workflows                          # LAB 4 / LAB 5
Microsoft.Agents.AI.Hosting.AGUI.AspNetCore            # LAB 5 server
Microsoft.Agents.AI.AGUI                               # LAB 5 client
Microsoft.Extensions.AI
Azure.AI.Projects
Azure.Identity
ModelContextProtocol                                   # MCP 客户端
```

每个 LAB 的 `.csproj` Link 共享数据 helper：

```xml
<ItemGroup>
  <Compile Include="..\..\data\ZavaData.cs" Link="ZavaData.cs" />
</ItemGroup>
```

### 3. `.env`（项目根放一份）

```bash
FOUNDRY_PROJECT_ENDPOINT="https://<account>.services.ai.azure.com/api/projects/<project>"
FOUNDRY_MODEL="gpt-5.5"
AZURE_OPENAI_EMBEDDING_MODEL="text-embedding-3-small"
# AG-UI 前端会用
AGUI_SERVER_URL="http://127.0.0.1:5100/"
```

### 4. GitHub Copilot Coding Agent + SKILL 使用约定

本 Workshop **不要用 `@workspace`，也不要用 `@zavashop-coding-agent`**。我们在 [`.github/agents/zavashop-coding-agent.agent.md`](.github/agents/zavashop-coding-agent.agent.md) 里已经定义好了一个**专属 GitHub Copilot Coding Agent**，它知道每个 LAB 对应哪个 SKILL、按 7 步循环（Locate → Load skill → Load lab → Plan → Implement → Validate → Map to acceptance）干活。

每个 LAB 的节奏统一为：

1. 打开 `LABxx/README.md`，浏览一遍故事 + 验收标准。
2. 在 VS Code Copilot Chat 里切到 **Agent Mode**，打开 **Agent 选择器**，选中 **`zavashop-coding-agent`**，然后发送：
   ```
   I'm doing LAB X in <Python|C#> — <你这一关的目标，比如 "build the inventory agent Zara">
   ```
   > **不要加 `@zavashop-coding-agent` 前缀**。Coding Agent 是从下拉里选的，对话框里只写任务描述（一定要带 LAB 号 + 编程语言）。如果你漏了语言，它会问一次然后在整个 LAB 里锁定该语言。
3. Coding Agent 会自动：
   - 按赛道 `read_file` 加载 `.github/skills/<对应-skill>/SKILL.md`
   - `read_file` 加载本 LAB 的 README（走 Python 部分或 .NET 部分）
   - 列出文件计划 → 写代码 → `get_errors` → 跑烟测 → 按验收标准逐条勾选
4. 你只需要在 HITL（LAB 2 / LAB 4 / LAB 5 都有）出现时回答 yes/no，或在 Red-Team (LAB 3) 报告 ASR 后决定要不要让它重写 instructions 再扫一次。

> **如果你想自己写代码**：也欢迎，Coding Agent 会乖乖等你；你可以让它只跑 step 6（validate）和 step 7（acceptance check）。

---

## 目录结构

```
MAF_Foundry_Foundation_Workshop/
├── README.md                      # 英文总览
├── README.zh.md                   # 本文件
├── .github/
│   ├── agents/
│   │   └── zavashop-coding-agent.agent.md   # ← GitHub Copilot Coding Agent（同时认识 Python / .NET）
│   └── skills/
│       ├── agent-framework-azure-ai-py/     # 🐍 Python 赛道 · LAB 1 / 2 / 3
│       ├── agent-framework-workflows-py/    # 🐍 LAB 4
│       ├── agent-framework-agui-py/         # 🐍 LAB 5
│       ├── agent-framework-azure-ai-csharp/ # 🟦 .NET 赛道 · LAB 1 / 2 / 3
│       ├── agent-framework-workflows-csharp/# 🟦 LAB 4
│       └── agent-framework-agui-csharp/     # 🟦 LAB 5
└── workshop/
    ├── data/                          # 🔵 所有 LAB 共享的 ZavaShop 数据集（详见 data/README.md）
    │   ├── zava_data.py               # Python loader
    │   ├── ZavaData.cs                # .NET loader（Link 到每个 LAB 的 csproj）
    │   ├── warehouses.json / skus.json / inventory.json
    │   ├── purchase_orders.json / suppliers.json / contracts.json
    │   ├── customers.json / orders.json / carriers.json
    │   └── exceptions.json / eval_queries.jsonl
    ├── LAB01-inventory-agent/         # 仓储助手 Zara
    ├── LAB02-procurement-toolbox/     # 采购代理 Pierre
    ├── LAB03-customer-memory-eval/    # 客服 Aria
    ├── LAB04-fulfillment-workflow/    # 履约多 Agent
    └── LAB05-control-tower-agui/      # 指挥中心 AG-UI
```

两套 customization 的分工：

- **Coding Agent (`.github/agents/zavashop-coding-agent.agent.md`)** —— 工程纪律 + LAB→SKILL 路由表 + 7 步循环。
- **Skills (`.github/skills/...`)** —— Microsoft Agent Framework 的真正 API 参考；Coding Agent 每次开干前都要先把它读进上下文。
- **Data (`workshop/data/`)** —— 所有 LAB 共享的 ZavaShop fixture（仓库/SKU/库存/PO/供应商/合同/客户/订单/货代/异常/评估集）；调用方都通过 [`zava_data.py`](workshop/data/zava_data.py) 读取，详见 [data/README.md](workshop/data/README.md)。

---

## 故事推进的明线 / 暗线

- **明线**：从单 Agent → 多 Agent → 前端，把 ZavaShop 的「仓储 / 采购 / 客服 / 履约 / 指挥」5 个角色逐一上线。
- **暗线**：从 *能跑起来* → *记得住* → *评得分* → *协同得动* → *可观测可接管*，对齐生产级 Agent 的成熟度阶梯。

完成全部 5 个 LAB 后，你将拥有一套可演示的 ZavaShop 智能供应链系统，并掌握 Microsoft Agent Framework 在 Foundry 上的端到端最佳实践。

祝玩得开心 — **现在请打开 [workshop/LAB01-inventory-agent/README.md](workshop/LAB01-inventory-agent/README.md)** 开始你的第一关。
