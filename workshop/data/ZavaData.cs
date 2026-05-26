// Shared ZavaShop data loader for every .NET LAB.
//
// Mirrors workshop/data/zava_data.py. Each loader returns plain
// System.Text.Json.Nodes.JsonNode trees (or convenience records for the
// most-used rows) so the JSON fixtures stay the single source of truth
// across the Python and .NET tracks.
//
// Reference it from a LAB csproj with:
//   <ItemGroup>
//     <Compile Include="..\data\ZavaData.cs" Link="ZavaData.cs" />
//   </ItemGroup>
//
// Then in any executable: `using ZavaShop.Workshop.Data;` and
// `ZavaData.FindStock("SKU-7421", "SEA-01")`.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ZavaShop.Workshop.Data;

public static class ZavaData
{
    private static readonly Lazy<string> _dataDir = new(() =>
    {
        // Walk up from the executable until we find the workshop/data folder.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "workshop", "data");
            if (Directory.Exists(candidate) && File.Exists(Path.Combine(candidate, "warehouses.json")))
            {
                return candidate;
            }

            // Or sibling: when the LAB binary runs from workshop/LABxx/bin/...
            var sibling = Path.Combine(dir.FullName, "..", "data");
            sibling = Path.GetFullPath(sibling);
            if (Directory.Exists(sibling) && File.Exists(Path.Combine(sibling, "warehouses.json")))
            {
                return sibling;
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException(
            "Could not locate workshop/data — run the LAB from inside the repo.");
    });

    private static readonly Dictionary<string, JsonArray> _cache = new();

    public static string DataDir => _dataDir.Value;

    public static JsonArray Load(string fileName)
    {
        if (_cache.TryGetValue(fileName, out var cached))
        {
            return cached;
        }

        var path = Path.Combine(DataDir, fileName);
        var json = File.ReadAllText(path);
        var node = JsonNode.Parse(json)
            ?? throw new JsonException($"Failed to parse {fileName}");
        var array = node.AsArray();
        _cache[fileName] = array;
        return array;
    }

    public static IEnumerable<JsonNode?> LoadJsonLines(string fileName)
    {
        var path = Path.Combine(DataDir, fileName);
        foreach (var line in File.ReadAllLines(path))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            yield return JsonNode.Parse(line);
        }
    }

    // ---------- Loaders (full collection) ----------

    public static JsonArray LoadWarehouses() => Load("warehouses.json");
    public static JsonArray LoadSkus() => Load("skus.json");
    public static JsonArray LoadInventory() => Load("inventory.json");
    public static JsonArray LoadPurchaseOrders() => Load("purchase_orders.json");
    public static JsonArray LoadSuppliers() => Load("suppliers.json");
    public static JsonArray LoadContracts() => Load("contracts.json");
    public static JsonArray LoadCustomers() => Load("customers.json");
    public static JsonArray LoadOrders() => Load("orders.json");
    public static JsonArray LoadCarriers() => Load("carriers.json");
    public static JsonArray LoadExceptions() => Load("exceptions.json");
    public static IEnumerable<JsonNode?> LoadEvalQueries() => LoadJsonLines("eval_queries.jsonl");

    // ---------- Finders (return one row by id, or null) ----------

    public static JsonNode? FindStock(string sku, string warehouse) =>
        LoadInventory().FirstOrDefault(row =>
            row?["sku"]?.GetValue<string>() == sku &&
            row?["warehouse"]?.GetValue<string>() == warehouse);

    public static JsonNode? FindPo(string poNumber) =>
        LoadPurchaseOrders().FirstOrDefault(row =>
            row?["po_number"]?.GetValue<string>() == poNumber);

    public static JsonNode? FindSupplier(string supplierIdOrName)
    {
        var needle = supplierIdOrName.ToLowerInvariant();
        return LoadSuppliers().FirstOrDefault(row =>
            row?["supplier_id"]?.GetValue<string>()?.ToLowerInvariant() == needle ||
            row?["name"]?.GetValue<string>()?.ToLowerInvariant() == needle);
    }

    public static JsonNode? FindContract(string supplierId) =>
        LoadContracts().FirstOrDefault(row =>
            row?["supplier_id"]?.GetValue<string>() == supplierId);

    public static JsonNode? FindCustomer(string customerId) =>
        LoadCustomers().FirstOrDefault(row =>
            row?["customer_id"]?.GetValue<string>() == customerId);

    public static JsonNode? FindOrder(string orderId) =>
        LoadOrders().FirstOrDefault(row =>
            row?["order_id"]?.GetValue<string>() == orderId);
}
