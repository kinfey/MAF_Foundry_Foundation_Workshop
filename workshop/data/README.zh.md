# ZavaShop Workshop 数据

> English edition: [README.md](./README.md)

五个 LAB 共享同一份虚构的 ZavaShop 数据集，全部放在本目录下。
让 Agent 从这些文件读取数据（而不是在每个脚本里硬编码一份 mock dict），
能让整个 workshop **更真实、更易调试、跨 LAB 行为更一致**。

## 文件清单

| 文件 | 使用方 | 内容 |
|------|--------|------|
| `warehouses.json` | LAB01、LAB04、LAB05 | 5 个履约中心（SEA-01 / LON-02 / SHA-03 / SAO-04 / DXB-05），含区域、主管、地址、容量。 |
| `skus.json` | LAB01、LAB02、LAB04 | 10 个 SKU，覆盖家居 / 花园户外 / 小家电三类，含单价、重量、是否危险品。 |
| `inventory.json` | LAB01、LAB04 | 按 (SKU, 仓库) 的在手库存、预占库存与补货点。 |
| `purchase_orders.json` | LAB01、LAB02 | 六个代表性 PO，覆盖 `confirmed` / `production` / `in_transit` / `customs_clearing` / `delayed` / `delivered` 等典型状态。 |
| `suppliers.json` | LAB02 | 8 家供应商，分布中国 / 意大利 / 法国 / 日本 / 美国，含品类专长与 Incoterm。 |
| `contracts.json` | LAB02 | 5 份框架合同，含付款条件、MOQ、单 PO 上限、阶梯折扣。 |
| `customers.json` | LAB03、LAB05 | 4 位客户档案（3 位 VIP + 1 位普通），含配送 / 包装 / 材料禁忌 / 时间窗偏好。 |
| `orders.json` | LAB03、LAB04、LAB05 | 6 笔客户订单，覆盖 `delivered` / `in_transit` / `processing` / `new` / `exception`。 |
| `carriers.json` | LAB04、LAB05 | 5 家货代（FedEx / DHL / USPS / Aramex / 顺丰），含线路覆盖与运费率。 |
| `exceptions.json` | LAB05 | 4 个未关闭的异常单，供控制塔 `list_exceptions` 工具返回。 |
| `eval_queries.jsonl` | LAB03 | 5 条评估提示词，含期望工具与期望结果，可直接喂给 `evaluate_agent`。 |
| `zava_data.py` | 所有 Python LAB | 数据加载模块，提供 `load_*()` 与便捷查找函数（`find_stock` / `find_po` / `find_supplier` / `find_contract` / `find_customer` / `find_order`）。 |
| `ZavaData.cs` | 所有 .NET LAB | .NET 赛道的加载模块 — 镜像 `zava_data.py`。提供静态 `Load*()`（返回 `JsonArray`）与 `Find*()`（返回 `JsonNode?`），名命空间 `ZavaShop.Workshop.Data`。每个 LAB 的 `.csproj` 通过 `<Compile Include="..\data\ZavaData.cs" Link="ZavaData.cs" />` 引入。 |

## LAB 如何使用

每个 LAB 脚本都应把 `workshop/data/` 加入 import 路径，调用 loader 而不要再写一份 mock：

### Python

```python
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "data"))
from zava_data import find_stock, find_po, load_customers
```

### .NET（C#）

在 LAB 的 csproj 里Link入共享 helper 后直接使用：

```xml
<ItemGroup>
  <Compile Include="..\..\data\ZavaData.cs" Link="ZavaData.cs" />
</ItemGroup>
```

```csharp
using ZavaShop.Workshop.Data;

var stock = ZavaData.FindStock("SKU-7421", "SEA-01");
int onHand = stock?["on_hand"]?.GetValue<int>() ?? 0;   // 312

foreach (var po in ZavaData.LoadPurchaseOrders())
{
    Console.WriteLine(po["po_number"] + " → " + po["status"]);
}
```

> 两个 helper 包装的是同一份 JSON，请保持同步。新增 fixture 时要同时暴露在 `zava_data.py` 与 `ZavaData.cs`。

这样：

- LAB01 的 `get_stock` / `get_po_status` 直接从 `inventory.json` / `purchase_orders.json` 取数（Agent 引用的仓库、ETA、"最后一次事件" 全部可追溯）。
- LAB02 的合同查询 MCP 与 `submit_po` 工具按 `contracts.json` 校验（MOQ、单 PO 上限、阶梯折扣）。
- LAB03 的 Aria 首轮种子来自 `customers.json`，`evaluate_agent` 直接读 `eval_queries.jsonl`。
- LAB04 的 `intake` executor 解析 `orders.json` 真实订单，按 `inventory.json` 查库存、按 `carriers.json` 报运费。
- LAB05 的 `list_exceptions` 工具返回 `exceptions.json` 的行；`quote_freight` 用 `carriers.json` 构造对比卡。

## 修改数据

数据量很小，可以直接手改。两条规矩：

1. **保持交叉引用的一致性。** `orders.json` 里出现的 `customer_id` 必须在 `customers.json` 中存在；`contracts.json` 里的 `supplier_id` 必须在 `suppliers.json` 中存在；`inventory.json` 里的 `(sku, warehouse)` 必须使用 `warehouses.json` 里的仓库编码。
2. **不要写入真实 PII。** 这里所有的姓名、邮箱、地址都是虚构的，请保持虚构。

LAB 运行中若修改了数据，请重启脚本（loader 使用 `lru_cache`，每个进程只读一次）。
