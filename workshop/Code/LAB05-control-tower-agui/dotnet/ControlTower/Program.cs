// LAB 5 — ZavaShop Control Tower (.NET track).
//
// Exposes a Foundry-backed control-tower AIAgent over AG-UI on `/`, gated by
// an X-API-Key middleware. The agent has three server-side tools:
//
//   • list_exceptions  — reads exceptions.json; instructs the model to fire
//                        the client-side play_alert_sound tool when a high-
//                        severity row appears.
//   • quote_freight    — reads carriers.json with the same lane-match shape
//                        as LAB 4's shipping_quote executor.
//   • fulfill_order    — for orders under $1000 it spins up the actual LAB 4
//                        workflow (linked into this assembly as Lab04Workflow.cs)
//                        and returns the ShippedVoucher; for orders at or
//                        above $1000 it returns an ApprovalDialog tool payload
//                        and only short-circuits to "shipped" once the client
//                        retries with supervisor_approval=true.
//
// LAB 4's workflow types live in the same assembly (via <Compile Include …>)
// so we can use WorkflowFactory.Build / InProcessExecution / ShippedVoucher
// directly. LAB 4's CLI Main is gated out by the LAB05_AGUI_HOST constant.

using System.ComponentModel;
using System.Text.Json.Nodes;

using Azure.AI.Projects;
using Azure.Identity;

using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Hosting;
using Microsoft.Agents.AI.Hosting.AGUI.AspNetCore;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Agents.AI.Workflows.Checkpointing;
using Microsoft.Extensions.AI;

using ZavaShop.LAB04.FulfillmentWorkflow;
using ZavaShop.Workshop.Data;

namespace ZavaShop.LAB05.ControlTower;

public static class Program
{
    private const string AgentName = "ZavaControlTower";
    private const decimal HitlThresholdUsd = 1000m;

    public static async Task<int> Main(string[] args)
    {
        LoadEnv();

        string endpoint = RequiredEnv("FOUNDRY_PROJECT_ENDPOINT");
        string model = RequiredEnv("FOUNDRY_MODEL");

        AzureCliCredential credential = new();
        AIProjectClient projectClient = new(new Uri(endpoint), credential);

        AIAgent controlTower = projectClient.AsAIAgent(new ChatClientAgentOptions
        {
            Name = AgentName,
            ChatOptions = new()
            {
                ModelId = model,
                Instructions =
                    "You are ZavaControlTower, the supply-chain operations console for ZavaShop. " +
                    "Operations managers chat with you to monitor exceptions, quote freight, and " +
                    "dispatch orders. Always answer using the tools — never invent data. When " +
                    "list_exceptions returns any high-severity row, call the client-side " +
                    "play_alert_sound tool with level=\"high\". For fulfill_order on totals at or " +
                    "above $1000, surface the ApprovalDialog payload and only retry with " +
                    "supervisor_approval=true once the operator confirms.",
                Tools =
                [
                    AIFunctionFactory.Create(
                        (Func<ExceptionsResult>)ListExceptions,
                        name: "list_exceptions",
                        description: "List today's open fulfillment exceptions from exceptions.json."),
                    AIFunctionFactory.Create(
                        (Func<string, string, double, FreightQuoteResult>)QuoteFreight,
                        name: "quote_freight",
                        description: "Quote freight between two regions (US / EU / APAC / META) for a given weight in kg."),
                    AIFunctionFactory.Create(
                        (Func<string, bool, Task<object>>)FulfillOrder,
                        name: "fulfill_order",
                        description:
                            "Drive the LAB 4 fulfillment workflow for an order id. Orders at or above $1000 require " +
                            "supervisor_approval=true — without it, this tool returns an ApprovalDialog payload."),
                ],
            },
        });

        WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
        builder.Services.AddHttpClient().AddLogging();
        builder.Services.AddAGUI();
        builder.AddAIAgent(AgentName, (_, _) => controlTower).WithInMemorySessionStore();

        WebApplication app = builder.Build();
        app.UseMiddleware<ApiKeyMiddleware>();
        app.MapGet("/health", () => Results.Ok(new { status = "ok", agent = AgentName }));
        app.MapAGUI(AgentName, "/");

        Console.WriteLine($"[server] {AgentName} AG-UI endpoint listening on http://127.0.0.1:5100/");
        await app.RunAsync("http://127.0.0.1:5100");
        return 0;
    }

    // ─── Server-side tools ────────────────────────────────────────────────

    [Description("List today's open fulfillment exceptions from exceptions.json.")]
    private static ExceptionsResult ListExceptions()
    {
        List<ExceptionRow> rows = new();
        int high = 0;
        foreach (JsonNode? row in ZavaData.LoadExceptions())
        {
            if (row is null) { continue; }
            string severity = row["severity"]?.GetValue<string>() ?? "unknown";
            if (severity == "high") { high++; }
            rows.Add(new ExceptionRow(
                ExceptionId: row["exception_id"]?.GetValue<string>() ?? "",
                OrderId: row["order_id"]?.GetValue<string>() ?? "",
                Severity: severity,
                Type: row["type"]?.GetValue<string>() ?? "",
                Owner: row["owner"]?.GetValue<string>() ?? "",
                Summary: row["summary"]?.GetValue<string>() ?? ""));
        }

        return new ExceptionsResult(
            Component: "ExceptionsList",
            Total: rows.Count,
            HighSeverity: high,
            Summary: $"{rows.Count} open exceptions — {high} high-severity.",
            Exceptions: rows);
    }

    [Description("Quote freight between two regions (US / EU / APAC / META) for a given weight in kg.")]
    private static FreightQuoteResult QuoteFreight(
        [Description("Origin region code, e.g. US, EU, APAC, META.")] string origin,
        [Description("Destination region code, e.g. US, EU, APAC, META.")] string destination,
        [Description("Total parcel weight in kilograms.")] double weightKg)
    {
        string o = origin.ToUpperInvariant();
        string d = destination.ToUpperInvariant();
        string lane = o == d ? $"{o}-domestic" : $"{o}-{d}";
        string laneRev = $"{d}-{o}";

        List<FreightCarrierLine> quotes = new();
        foreach (JsonNode? c in ZavaData.LoadCarriers())
        {
            if (c is null) { continue; }
            JsonArray lanes = c["lanes"]?.AsArray() ?? new JsonArray();
            bool match = lanes.Any(x =>
            {
                string s = x?.GetValue<string>() ?? "";
                return s == lane || s == laneRev;
            });
            if (!match) { continue; }

            decimal baseUsd = c["base_usd"]!.GetValue<decimal>();
            decimal perKgUsd = c["per_kg_usd"]!.GetValue<decimal>();
            decimal price = Math.Round(baseUsd + perKgUsd * (decimal)weightKg, 2);
            quotes.Add(new FreightCarrierLine(
                Carrier: c["carrier_id"]!.GetValue<string>(),
                Name: c["name"]!.GetValue<string>(),
                PriceUsd: price,
                TransitDays: c["transit_days_typical"]!.GetValue<int>()));
        }

        quotes = quotes.OrderBy(q => q.PriceUsd).Take(3).ToList();

        return new FreightQuoteResult(
            Component: "FreightCompareCard",
            Origin: o,
            Destination: d,
            WeightKg: (decimal)weightKg,
            Lane: lane,
            Summary: $"Found {quotes.Count} freight quotes for {o}→{d} at {weightKg} kg.",
            Quotes: quotes);
    }

    [Description("Drive the LAB 4 fulfillment workflow for an order id. " +
                 "Orders at or above $1000 require supervisor_approval=true — without it, this tool returns an ApprovalDialog payload.")]
    private static async Task<object> FulfillOrder(
        [Description("Order id, e.g. ORD-20260524-001.")] string orderId,
        [Description("Set to true once the supervisor has approved an over-threshold order.")] bool supervisorApproval = false)
    {
        JsonNode? order = ZavaData.FindOrder(orderId);
        if (order is null)
        {
            return new FulfillmentResult(
                Component: "FulfillmentResult",
                Status: "unknown",
                OrderId: orderId,
                Summary: $"Unknown order {orderId}.",
                Warehouse: null, Supervisor: null, Carrier: null, EtaDays: null);
        }

        decimal totalUsd = order["total_usd"]!.GetValue<decimal>();
        string customerId = order["customer_id"]?.GetValue<string>() ?? "";

        if (totalUsd >= HitlThresholdUsd && !supervisorApproval)
        {
            return new ApprovalDialogResult(
                Component: "ApprovalDialog",
                OrderId: orderId,
                CustomerId: customerId,
                TotalUsd: totalUsd,
                Reason: "amount_over_threshold",
                Summary: $"{orderId} requires supervisor approval — ${totalUsd:N2} is at or above the $1,000 threshold. " +
                         "Re-run fulfill_order with supervisor_approval=true once it is signed off.");
        }

        if (supervisorApproval)
        {
            // Short-circuit: driving LAB 4's RequestPort gate from inside a tool would require
            // Workflow.RunStreamingAsync(responses:...) plumbing that is out of scope for LAB 5.
            string? warehouse = order["ship_to_warehouse"]?.GetValue<string>();
            string supervisor = warehouse switch
            {
                "SEA-01" => "Mei Tanaka",
                "FRA-01" => "Lukas Becker",
                "SHA-01" => "Wei Zhang",
                _ => "supervisor",
            };
            return new FulfillmentResult(
                Component: "FulfillmentResult",
                Status: "shipped",
                OrderId: orderId,
                Summary: $"{orderId} approved by supervisor; voucher issued (supervisor {supervisor}).",
                Warehouse: warehouse,
                Supervisor: supervisor,
                Carrier: "MANUAL",
                EtaDays: null);
        }

        // Under-threshold path: drive the actual LAB 4 workflow end-to-end.
        string checkpointDir = Path.Combine(Path.GetTempPath(), $"zava-lab05-{Guid.NewGuid():N}");
        (Workflow workflow, CheckpointManager checkpoints, _) = WorkflowFactory.Build(checkpointDir);
        string sessionId = $"lab05-{orderId}-{DateTime.UtcNow:yyyyMMddHHmmssfff}";

        ShippedVoucher? shipped = null;
        string? failure = null;

        await using (StreamingRun run = await InProcessExecution
            .RunStreamingAsync(workflow, orderId, checkpoints, sessionId: sessionId)
            .ConfigureAwait(false))
        {
            await foreach (WorkflowEvent evt in run.WatchStreamAsync().ConfigureAwait(false))
            {
                switch (evt)
                {
                    case WorkflowOutputEvent output when output.Data is ShippedVoucher sv:
                        shipped = sv;
                        break;
                    case ExecutorFailedEvent failed:
                        failure = $"executor {failed.ExecutorId} failed: {failed.Data}";
                        break;
                    case WorkflowErrorEvent err:
                        failure = $"workflow error: {err.Data}";
                        break;
                }
            }
        }

        try { Directory.Delete(checkpointDir, recursive: true); } catch { /* best effort */ }

        if (shipped is not null)
        {
            return new FulfillmentResult(
                Component: "FulfillmentResult",
                Status: shipped.Status,
                OrderId: shipped.OrderId,
                Summary: $"Fulfilled {shipped.OrderId} — shipped via {shipped.Carrier} from {shipped.Warehouse} " +
                         $"(supervisor {shipped.Supervisor}), ETA {shipped.EtaDays}d.",
                Warehouse: shipped.Warehouse,
                Supervisor: shipped.Supervisor,
                Carrier: shipped.Carrier,
                EtaDays: shipped.EtaDays);
        }

        return new FulfillmentResult(
            Component: "FulfillmentResult",
            Status: "error",
            OrderId: orderId,
            Summary: failure ?? "Workflow ended without a shipped voucher.",
            Warehouse: null, Supervisor: null, Carrier: null, EtaDays: null);
    }

    // ─── .env loader (same pattern as the other LABs) ─────────────────────

    private static string RequiredEnv(string name) =>
        Environment.GetEnvironmentVariable(name)
        ?? throw new InvalidOperationException($"{name} is not set.");

    private static void LoadEnv()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        string? envPath = null;
        while (dir is not null)
        {
            string candidate = Path.Combine(dir.FullName, "workshop", ".env");
            if (File.Exists(candidate)) { envPath = candidate; break; }

            string siblingWorkshop = Path.GetFullPath(Path.Combine(dir.FullName, "..", "workshop", ".env"));
            if (File.Exists(siblingWorkshop)) { envPath = siblingWorkshop; break; }

            string sibling = Path.GetFullPath(Path.Combine(dir.FullName, "..", ".env"));
            if (File.Exists(sibling) && dir.Name.Equals("workshop", StringComparison.OrdinalIgnoreCase))
            {
                envPath = sibling;
                break;
            }

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

// ─── X-API-Key middleware ─────────────────────────────────────────────────

internal sealed class ApiKeyMiddleware(RequestDelegate next, ILogger<ApiKeyMiddleware> logger)
{
    private static int _warned;

    public async Task InvokeAsync(HttpContext context)
    {
        if (context.Request.Path.StartsWithSegments("/health"))
        {
            await next(context);
            return;
        }

        string? expected = Environment.GetEnvironmentVariable("AG_UI_API_KEY");
        if (string.IsNullOrEmpty(expected))
        {
            if (Interlocked.Exchange(ref _warned, 1) == 0)
            {
                logger.LogWarning("AG_UI_API_KEY is not set — running in dev mode without auth.");
            }
            await next(context);
            return;
        }

        if (!context.Request.Headers.TryGetValue("X-API-Key", out var got))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsync("Missing API key.");
            return;
        }

        if (got != expected)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsync("Invalid API key.");
            return;
        }

        await next(context);
    }
}

// ─── Tool payload records (returned to the model as structured JSON) ─────

internal sealed record ExceptionRow(
    string ExceptionId,
    string OrderId,
    string Severity,
    string Type,
    string Owner,
    string Summary);

internal sealed record ExceptionsResult(
    string Component,
    int Total,
    int HighSeverity,
    string Summary,
    IReadOnlyList<ExceptionRow> Exceptions);

internal sealed record FreightCarrierLine(
    string Carrier,
    string Name,
    decimal PriceUsd,
    int TransitDays);

internal sealed record FreightQuoteResult(
    string Component,
    string Origin,
    string Destination,
    decimal WeightKg,
    string Lane,
    string Summary,
    IReadOnlyList<FreightCarrierLine> Quotes);

internal sealed record FulfillmentResult(
    string Component,
    string Status,
    string OrderId,
    string Summary,
    string? Warehouse,
    string? Supervisor,
    string? Carrier,
    int? EtaDays);

internal sealed record ApprovalDialogResult(
    string Component,
    string OrderId,
    string CustomerId,
    decimal TotalUsd,
    string Reason,
    string Summary);
