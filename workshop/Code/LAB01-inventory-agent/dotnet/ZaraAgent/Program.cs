using System;
using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

using Azure.AI.Projects;
using Azure.Identity;

using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

using ModelContextProtocol.Client;

using ZavaShop.Workshop.Data;

namespace ZavaShop.LAB01;

public static class Program
{
    public static async Task<int> Main()
    {
        LoadEnv();

        string endpoint = Environment.GetEnvironmentVariable("FOUNDRY_PROJECT_ENDPOINT")
            ?? throw new InvalidOperationException("FOUNDRY_PROJECT_ENDPOINT is not set.");
        string model = Environment.GetEnvironmentVariable("FOUNDRY_MODEL")
            ?? throw new InvalidOperationException("FOUNDRY_MODEL is not set.");

        AIProjectClient projectClient = new(new Uri(endpoint), new AzureCliCredential());

        await using McpClient learnMcp = await McpClient.CreateAsync(new HttpClientTransport(new()
        {
            Endpoint = new Uri("https://learn.microsoft.com/api/mcp"),
            Name = "Microsoft Learn MCP",
        }));
        IList<McpClientTool> mcpTools = await learnMcp.ListToolsAsync();

        List<AITool> tools =
        [
            AIFunctionFactory.Create(GetStock),
            AIFunctionFactory.Create(GetPoStatus),
            AIFunctionFactory.Create(FindOpenPosBySku),
            .. mcpTools.Cast<AITool>(),
        ];

        AIAgent agent = projectClient.AsAIAgent(
            model,
            instructions:
                "You are Zara, the warehouse assistant for ZavaShop's Seattle fulfillment center. " +
                "Always answer using real data from the tools and never invent stock or PO numbers. " +
                "To find the latest unarrived PO for a SKU, call FindOpenPosBySku first, then " +
                "GetPoStatus for fuller detail.",
            name: "Zara",
            tools: tools);

        AgentSession session = await agent.CreateSessionAsync();

        Console.WriteLine("--- Turn 1 ---");
        Console.WriteLine(await agent.RunAsync(
            "How many SKU-7421 do we have left at SEA-01?", session));

        Console.WriteLine();
        Console.WriteLine("--- Turn 2 ---");
        Console.WriteLine(await agent.RunAsync(
            "What is the most recent PO for that SKU that hasn't arrived yet?", session));

        Console.WriteLine();
        Console.WriteLine("--- Turn 3 ---");
        Console.WriteLine(await agent.RunAsync(
            "Search the Microsoft Learn MCP for best practices on Azure AI Foundry.", session));

        return 0;
    }

    [Description("Get current on-hand stock of a SKU in a warehouse.")]
    static string GetStock(
        [Description("SKU id, e.g. SKU-7421.")] string sku,
        [Description("Warehouse id, e.g. SEA-01.")] string warehouse = "SEA-01")
    {
        JsonNode? row = ZavaData.FindStock(sku, warehouse);
        if (row is null)
        {
            return $"{sku} is not tracked at warehouse {warehouse}.";
        }

        return $"{sku} @ {warehouse}: on_hand={row["on_hand"]}, " +
               $"reserved={row["reserved"]}, reorder_point={row["reorder_point"]}";
    }

    [Description("Query the status of an inbound Purchase Order by its number.")]
    static string GetPoStatus(
        [Description("PO number, e.g. PO-20260518-001.")] string poNumber)
    {
        JsonNode? po = ZavaData.FindPo(poNumber);
        return po?.ToJsonString()
            ?? JsonSerializer.Serialize(new { po_number = poNumber, status = "unknown" });
    }

    [Description("List open (not-yet-delivered) Purchase Orders for a SKU, newest ETA first.")]
    static string FindOpenPosBySku(
        [Description("SKU id, e.g. SKU-7421.")] string sku,
        [Description("Optional warehouse id; pass an empty string to ignore.")] string warehouse = "")
    {
        JsonArray pos = ZavaData.LoadPurchaseOrders();
        List<JsonNode> open = new();

        foreach (JsonNode? po in pos)
        {
            if (po is null) { continue; }
            if (po["sku"]?.GetValue<string>() != sku) { continue; }
            if (po["status"]?.GetValue<string>() == "delivered") { continue; }
            if (!string.IsNullOrEmpty(warehouse) &&
                po["destination_warehouse"]?.GetValue<string>() != warehouse)
            {
                continue;
            }

            open.Add(po);
        }

        open.Sort((a, b) => string.CompareOrdinal(
            b["eta"]?.GetValue<string>(),
            a["eta"]?.GetValue<string>()));

        JsonArray result = new(open.Select(n => JsonNode.Parse(n.ToJsonString())!).ToArray());
        return result.ToJsonString();
    }

    static void LoadEnv()
    {
        // Walk up from the executable looking for workshop/.env
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        string? envPath = null;
        while (dir is not null)
        {
            string candidate = Path.Combine(dir.FullName, "workshop", ".env");
            if (File.Exists(candidate)) { envPath = candidate; break; }

            string sibling = Path.GetFullPath(Path.Combine(dir.FullName, "..", ".env"));
            if (File.Exists(sibling) && dir.Name.Equals("workshop", StringComparison.OrdinalIgnoreCase))
            {
                envPath = sibling;
                break;
            }

            string siblingWorkshop = Path.GetFullPath(Path.Combine(dir.FullName, "..", "workshop", ".env"));
            if (File.Exists(siblingWorkshop)) { envPath = siblingWorkshop; break; }

            dir = dir.Parent;
        }

        if (envPath is null) { return; }

        foreach (string raw in File.ReadAllLines(envPath))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#') || !line.Contains('=')) { continue; }

            int eq = line.IndexOf('=');
            string key = line[..eq].Trim();
            string value = line[(eq + 1)..].Trim().Trim('"').Trim('\'');
            if (key.Length == 0) { continue; }
            if (Environment.GetEnvironmentVariable(key) is null)
            {
                Environment.SetEnvironmentVariable(key, value);
            }
        }
    }
}
