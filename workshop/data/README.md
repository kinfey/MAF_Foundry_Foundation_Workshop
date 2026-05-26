# ZavaShop Workshop Data

> Chinese edition: [README.zh.md](./README.zh.md)

All five LABs share the same fictional ZavaShop dataset that lives in this folder.
Loading from these files (rather than inlining a mock dict inside every agent)
keeps the workshop **realistic, debuggable, and consistent across LABs**.

## Files

| File | Used by | What it contains |
|------|---------|------------------|
| `warehouses.json` | LAB01, LAB04, LAB05 | 5 fulfillment centers (SEA-01 / LON-02 / SHA-03 / SAO-04 / DXB-05) with regions, supervisors, addresses, capacity. |
| `skus.json` | LAB01, LAB02, LAB04 | 10 catalog items across home goods / garden & outdoor / small appliances, with unit price, weight, hazmat flag. |
| `inventory.json` | LAB01, LAB04 | On-hand & reserved counts per (SKU, warehouse), with reorder point. |
| `purchase_orders.json` | LAB01, LAB02 | Six representative POs covering all common statuses: `confirmed`, `production`, `in_transit`, `customs_clearing`, `delayed`, `delivered`. |
| `suppliers.json` | LAB02 | 8 suppliers across China / Italy / France / Japan / USA with specialties and incoterms. |
| `contracts.json` | LAB02 | Five framework contracts with payment terms, MOQ, max single-PO ceiling, and discount tiers. |
| `customers.json` | LAB03, LAB05 | 4 customer profiles (3 VIP + 1 standard) with delivery / packaging / materials-to-avoid / time-window preferences. |
| `orders.json` | LAB03, LAB04, LAB05 | 6 customer orders spanning `delivered` / `in_transit` / `processing` / `new` / `exception`. |
| `carriers.json` | LAB04, LAB05 | 5 freight carriers (FedEx / DHL / USPS / Aramex / SF Express) with lane coverage and rates. |
| `exceptions.json` | LAB05 | 4 open exception cases for the control-tower `list_exceptions` tool. |
| `eval_queries.jsonl` | LAB03 | 5 evaluation prompts with the expected tool + expected outcome, ready for `evaluate_agent`. |
| `zava_data.py` | every Python LAB | Loader module with `load_*()` + small convenience lookups (`find_stock`, `find_po`, `find_supplier`, `find_contract`, `find_customer`, `find_order`). |
| `ZavaData.cs` | every .NET LAB | Loader module for the .NET track — mirrors `zava_data.py`. Exposes static `Load*()` (returns `JsonArray`) and `Find*()` (returns `JsonNode?`) under namespace `ZavaShop.Workshop.Data`. Each LAB's `.csproj` links it via `<Compile Include="..\data\ZavaData.cs" Link="ZavaData.cs" />`. |

## How LABs use it

Every LAB script should add `workshop/data/` to its import path and call the
loaders instead of redefining mock data:

### Python

```python
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "data"))
from zava_data import find_stock, find_po, load_customers
```

### .NET (C#)

Link the shared helper into the LAB's csproj and use it directly:

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

> Both helpers wrap the same JSON files — keep them in sync. If you add a new fixture, expose it through both `zava_data.py` and `ZavaData.cs`.

That way:

- LAB01's `get_stock` / `get_po_status` return data from `inventory.json` /
  `purchase_orders.json` (so the warehouse, ETA and "last event" notes the
  agent quotes are real and traceable).
- LAB02's contract lookup MCP and `submit_po` tool validate against
  `contracts.json` (MOQ, max single-PO ceiling, discount tiers).
- LAB03 seeds Aria's first turn from `customers.json` and runs the
  `evaluate_agent` batch from `eval_queries.jsonl`.
- LAB04 parses real `orders.json` rows in the `intake` executor, looks up
  stock via `inventory.json`, and quotes freight from `carriers.json`.
- LAB05's `list_exceptions` tool returns `exceptions.json` rows, and
  `quote_freight` uses `carriers.json` to build its comparison card.

## Editing the data

The data is small enough to hand-edit. Two rules:

1. **Keep the cross-references consistent.** A `customer_id` in `orders.json`
   must exist in `customers.json`; a `supplier_id` in `contracts.json` must
   exist in `suppliers.json`; a `(sku, warehouse)` row in `inventory.json`
   uses warehouse codes from `warehouses.json`.
2. **Do not check in PII.** All names, emails and addresses here are
   fictional — keep them that way.

If you change a value while a LAB is running, restart the script (the loader
uses `lru_cache` so the data is read once per process).
