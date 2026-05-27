// LAB 4 — ZavaShop order fulfillment workflow (.NET track).
//
// DAG:
//     intake ──┬──► stock_check ──┐
//              └──► shipping_quote ┴──► allocator ──► gate ──► dispatch ──► finance
//                                                       │
//                                                       └──► approval_port ──► resume ──► dispatch
//
// Demonstrates:
// - Fan-out / fan-in: stock_check and shipping_quote run in the same super-step
// - HITL: RequestPort<HumanApprovalRequest, HumanApprovalResponse> when total ≥ $1000
// - FileSystemJsonCheckpointStore: every super-step persists state to ./.checkpoints
// - Resume: InProcessExecution.ResumeStreamingAsync(workflow, checkpoint, manager, ct)
//
// Run:
//     dotnet run                                            # both scenarios + resume demo
//     dotnet run -- ORD-20260524-001                        # one scenario only
//     LAB04_AUTO_APPROVE=yes dotnet run -- ORD-20260524-002 # non-interactive approve
//     LAB04_AUTO_APPROVE=no  dotnet run -- ORD-20260524-002 # non-interactive reject

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Agents.AI.Workflows.Checkpointing;

using ZavaShop.Workshop.Data;

namespace ZavaShop.LAB04.FulfillmentWorkflow;

internal static class Constants
{
    public const decimal HitlThresholdUsd = 1000m;
    public const string ApprovalPortId = "approval_port";
    public const string ApprovalScope = "Approval";
    public const string PendingPlanKey = "pending_plan";
}

// ───────────────────────────────────────────────────────────────────────────
// Records that flow between executors
// ───────────────────────────────────────────────────────────────────────────

internal sealed record OrderLine(string Sku, int Qty, decimal UnitPriceUsd);
internal sealed record OrderRecord(
    string OrderId,
    string CustomerId,
    IReadOnlyList<OrderLine> Lines,
    string ShipToCity,
    string ShipToWarehouse,
    decimal TotalUsd);

internal sealed record StockLine(string Sku, int Qty, int OnHand, int Available, bool Sufficient);
internal sealed record StockReport(
    string OrderId,
    string Warehouse,
    IReadOnlyList<StockLine> Lines,
    bool AllInStock);

internal sealed record CarrierQuote(string CarrierId, string Name, decimal PriceUsd, int TransitDays);
internal sealed record FreightQuote(
    string OrderId,
    string Lane,
    decimal TotalKg,
    IReadOnlyList<CarrierQuote> Quotes,
    CarrierQuote Cheapest);

internal enum LegKind { Stock, Freight }
internal sealed record LegResult(
    LegKind Kind,
    OrderRecord Order,
    StockReport? Stock,
    FreightQuote? Freight);

internal sealed record AllocationPlan(
    OrderRecord Order,
    StockReport Stock,
    FreightQuote Freight,
    decimal TotalUsd);

internal sealed record HumanApprovalRequest(
    string OrderId,
    string CustomerId,
    decimal TotalUsd,
    string Reason);
internal sealed record HumanApprovalResponse(bool Approved, string Reason);

internal sealed record DispatchResult(
    string OrderId,
    string Warehouse,
    string Supervisor,
    string Carrier,
    int EtaDays);

internal sealed record ShippedVoucher(
    string Status,
    string OrderId,
    string Warehouse,
    string Supervisor,
    string Carrier,
    int EtaDays);

internal sealed record RejectedVoucher(string Status, string OrderId, string Reason);

// ───────────────────────────────────────────────────────────────────────────
// Helpers
// ───────────────────────────────────────────────────────────────────────────

internal static class Lanes
{
    private static readonly Dictionary<string, string> CityRegion = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Seattle"] = "US",        ["Boston"] = "US",        ["New York"] = "US",
        ["San Francisco"] = "US",  ["Los Angeles"] = "US",   ["Chicago"] = "US",
        ["London"] = "EU",         ["Berlin"] = "EU",        ["Paris"] = "EU", ["Madrid"] = "EU",
        ["Shanghai"] = "APAC",     ["Tokyo"] = "APAC",       ["Singapore"] = "APAC", ["Beijing"] = "APAC",
        ["Dubai"] = "META",        ["Riyadh"] = "META",
        ["Sao Paulo"] = "LATAM",   ["São Paulo"] = "LATAM",
    };

    private static readonly Dictionary<string, string> WarehousePrefix = new()
    {
        ["US-West"] = "US", ["EU"] = "EU", ["APAC"] = "APAC", ["META"] = "META", ["LATAM"] = "LATAM",
    };

    public static string Resolve(string shipToWarehouse, string shipToCity)
    {
        JsonNode wh = ZavaData.LoadWarehouses()
            .FirstOrDefault(w => (string?)w!["code"] == shipToWarehouse)
            ?? throw new InvalidOperationException($"warehouse {shipToWarehouse} not found");
        string region = (string?)wh["region"] ?? throw new InvalidOperationException("warehouse missing region");
        string src = WarehousePrefix.GetValueOrDefault(region, region);
        string dst = CityRegion.GetValueOrDefault(shipToCity, src);
        return src == dst ? $"{src}-domestic" : $"{src}-{dst}";
    }
}

internal static class Lookups
{
    public static JsonNode Warehouse(string code) =>
        ZavaData.LoadWarehouses().FirstOrDefault(w => (string?)w!["code"] == code)
            ?? throw new InvalidOperationException($"warehouse {code} not found");

    public static JsonNode Sku(string sku) =>
        ZavaData.LoadSkus().FirstOrDefault(s => (string?)s!["sku"] == sku)
            ?? throw new InvalidOperationException($"sku {sku} not found");
}

// ───────────────────────────────────────────────────────────────────────────
// Executors
// ───────────────────────────────────────────────────────────────────────────

internal sealed class IntakeExecutor() : Executor<string, OrderRecord>("intake")
{
    public override ValueTask<OrderRecord> HandleAsync(
        string message, IWorkflowContext context, CancellationToken cancellationToken = default)
    {
        string orderId = message.Trim();
        JsonNode raw = ZavaData.FindOrder(orderId)
            ?? throw new InvalidOperationException($"Unknown order: {orderId}");

        List<OrderLine> lines = (raw["lines"]?.AsArray() ?? throw new InvalidOperationException("order missing lines"))
            .Select(l => new OrderLine(
                Sku: (string?)l!["sku"] ?? throw new InvalidOperationException("line missing sku"),
                Qty: (int)l["qty"]!,
                UnitPriceUsd: (decimal)l["unit_price_usd"]!))
            .ToList();

        OrderRecord order = new(
            OrderId: (string)raw["order_id"]!,
            CustomerId: (string)raw["customer_id"]!,
            Lines: lines,
            ShipToCity: (string)raw["ship_to_city"]!,
            ShipToWarehouse: (string)raw["ship_to_warehouse"]!,
            TotalUsd: (decimal)raw["total_usd"]!);

        Console.WriteLine($"[intake] {order.OrderId} → {order.ShipToWarehouse} ({order.ShipToCity}) goods=${order.TotalUsd}");
        return ValueTask.FromResult(order);
    }
}

internal sealed class StockCheckExecutor() : Executor<OrderRecord, LegResult>("stock_check")
{
    public override ValueTask<LegResult> HandleAsync(
        OrderRecord order, IWorkflowContext context, CancellationToken cancellationToken = default)
    {
        List<StockLine> lines = new();
        foreach (OrderLine ln in order.Lines)
        {
            JsonNode? row = ZavaData.FindStock(ln.Sku, order.ShipToWarehouse);
            int onHand = row is null ? 0 : (int)row["on_hand"]!;
            int reserved = row is null ? 0 : (int)row["reserved"]!;
            int available = Math.Max(onHand - reserved, 0);
            lines.Add(new StockLine(ln.Sku, ln.Qty, onHand, available, available >= ln.Qty));
        }
        StockReport report = new(order.OrderId, order.ShipToWarehouse, lines, lines.All(l => l.Sufficient));
        string summary = string.Join(", ", lines.Select(l => $"{l.Sku}:{l.Available}/{l.Qty}"));
        Console.WriteLine($"[stock_check] {order.OrderId} all_in_stock={report.AllInStock} [{summary}]");
        return ValueTask.FromResult(new LegResult(LegKind.Stock, order, report, null));
    }
}

internal sealed class ShippingQuoteExecutor() : Executor<OrderRecord, LegResult>("shipping_quote")
{
    public override ValueTask<LegResult> HandleAsync(
        OrderRecord order, IWorkflowContext context, CancellationToken cancellationToken = default)
    {
        string lane = Lanes.Resolve(order.ShipToWarehouse, order.ShipToCity);
        decimal totalKg = order.Lines.Sum(ln => (decimal)Lookups.Sku(ln.Sku)["weight_kg"]! * ln.Qty);

        List<CarrierQuote> quotes = new();
        foreach (JsonNode? c in ZavaData.LoadCarriers())
        {
            JsonArray laneArr = c!["lanes"]?.AsArray() ?? throw new InvalidOperationException("carrier missing lanes");
            if (!laneArr.Any(x => (string?)x == lane)) continue;
            decimal price = Math.Round(
                (decimal)c["base_usd"]! + (decimal)c["per_kg_usd"]! * totalKg, 2);
            quotes.Add(new CarrierQuote(
                CarrierId: (string)c["carrier_id"]!,
                Name: (string)c["name"]!,
                PriceUsd: price,
                TransitDays: (int)c["transit_days_typical"]!));
        }
        if (quotes.Count == 0)
            throw new InvalidOperationException($"No carrier covers lane '{lane}' for {order.OrderId}");

        quotes = quotes.OrderBy(q => q.PriceUsd).Take(3).ToList();
        CarrierQuote cheapest = quotes[0];
        FreightQuote freight = new(order.OrderId, lane, Math.Round(totalKg, 2), quotes, cheapest);
        Console.WriteLine(
            $"[shipping_quote] {order.OrderId} lane={lane} kg={freight.TotalKg} "
            + $"cheapest={cheapest.CarrierId}@${cheapest.PriceUsd}");
        return ValueTask.FromResult(new LegResult(LegKind.Freight, order, null, freight));
    }
}

internal sealed class AllocatorExecutor() : Executor<LegResult, AllocationPlan?>("allocator")
{
    // Per-run instance state: the barrier delivers each leg message individually,
    // so we buffer until both arms (stock + freight) have reported, then emit one plan.
    private LegResult? _stock;
    private LegResult? _freight;

    public override ValueTask<AllocationPlan?> HandleAsync(
        LegResult leg, IWorkflowContext context, CancellationToken cancellationToken = default)
    {
        if (leg.Kind == LegKind.Stock) _stock = leg;
        else _freight = leg;

        if (_stock is null || _freight is null)
        {
            Console.WriteLine($"[allocator] buffered {leg.Kind} for {leg.Order.OrderId}; waiting for other leg");
            return ValueTask.FromResult<AllocationPlan?>(null);
        }

        OrderRecord order = _stock.Order;
        FreightQuote freight = _freight.Freight!;
        decimal total = Math.Round(order.TotalUsd + freight.Cheapest.PriceUsd, 2);
        AllocationPlan plan = new(order, _stock.Stock!, freight, total);
        _stock = null;
        _freight = null;
        Console.WriteLine(
            $"[allocator] {order.OrderId} plan_total=${plan.TotalUsd} "
            + $"(goods=${order.TotalUsd} + freight=${freight.Cheapest.PriceUsd})");
        return ValueTask.FromResult<AllocationPlan?>(plan);
    }
}

internal sealed class ApprovalGateExecutor() : Executor<AllocationPlan, AllocationPlan>("approval_gate")
{
    public override ValueTask<AllocationPlan> HandleAsync(
        AllocationPlan plan, IWorkflowContext context, CancellationToken cancellationToken = default)
    {
        if (plan.TotalUsd < Constants.HitlThresholdUsd)
        {
            Console.WriteLine(
                $"[approval_gate] {plan.Order.OrderId} auto-approved "
                + $"(${plan.TotalUsd} < ${Constants.HitlThresholdUsd})");
        }
        else
        {
            Console.WriteLine(
                $"[approval_gate] {plan.Order.OrderId} requires HITL approval "
                + $"(${plan.TotalUsd} ≥ ${Constants.HitlThresholdUsd}) — workflow PAUSES at approval_port");
        }
        return ValueTask.FromResult(plan);
    }
}

internal sealed class ApprovalRequestBuilderExecutor() : Executor<AllocationPlan, HumanApprovalRequest>("approval_request_builder")
{
    public override async ValueTask<HumanApprovalRequest> HandleAsync(
        AllocationPlan plan, IWorkflowContext context, CancellationToken cancellationToken = default)
    {
        // Stash the pending plan so ApprovalResumeExecutor can re-emit it downstream
        // once the human supervisor responds.
        await context.QueueStateUpdateAsync(
            Constants.PendingPlanKey, plan,
            scopeName: Constants.ApprovalScope,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        string reason = plan.Stock.AllInStock ? "amount_over_threshold" : "stock_shortage";
        return new HumanApprovalRequest(
            plan.Order.OrderId, plan.Order.CustomerId, plan.TotalUsd, reason);
    }
}

internal sealed class ApprovalResumeExecutor : Executor<HumanApprovalResponse>
{
    public ApprovalResumeExecutor() : base("approval_resume") { }

    // We don't declare a TOut on the base class because this executor can emit
    // EITHER an AllocationPlan (forwarded to dispatch on approval) OR a
    // RejectedVoucher (yielded as a workflow output on rejection). Declaring both
    // here keeps the message router happy without leaking the plan as an output.
    protected override ProtocolBuilder ConfigureProtocol(ProtocolBuilder protocol) =>
        base.ConfigureProtocol(protocol)
            .SendsMessageType(typeof(AllocationPlan))
            .YieldsOutputType(typeof(RejectedVoucher));

    public override async ValueTask HandleAsync(
        HumanApprovalResponse reply, IWorkflowContext context, CancellationToken cancellationToken = default)
    {
        AllocationPlan plan = await context.ReadStateAsync<AllocationPlan>(
            Constants.PendingPlanKey,
            scopeName: Constants.ApprovalScope,
            cancellationToken: cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("approval_resume invoked but no pending plan was saved");

        // Clear the slot so a subsequent run on the same workflow starts clean.
        await context.QueueStateUpdateAsync<AllocationPlan?>(
            Constants.PendingPlanKey, null,
            scopeName: Constants.ApprovalScope,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        if (!reply.Approved)
        {
            string why = string.IsNullOrWhiteSpace(reply.Reason) ? "supervisor declined" : reply.Reason;
            Console.WriteLine($"[approval_resume] {plan.Order.OrderId} REJECTED: {why}");
            await context.YieldOutputAsync(
                new RejectedVoucher("rejected", plan.Order.OrderId, why),
                cancellationToken).ConfigureAwait(false);
            return;
        }

        Console.WriteLine(
            $"[approval_resume] {plan.Order.OrderId} APPROVED: "
            + (string.IsNullOrWhiteSpace(reply.Reason) ? "(no reason)" : reply.Reason));
        await context.SendMessageAsync(plan, cancellationToken).ConfigureAwait(false);
    }
}

internal sealed class DispatchExecutor() : Executor<AllocationPlan, DispatchResult>("dispatch")
{
    public override ValueTask<DispatchResult> HandleAsync(
        AllocationPlan plan, IWorkflowContext context, CancellationToken cancellationToken = default)
    {
        JsonNode wh = Lookups.Warehouse(plan.Order.ShipToWarehouse);
        DispatchResult result = new(
            OrderId: plan.Order.OrderId,
            Warehouse: (string)wh["code"]!,
            Supervisor: (string)wh["supervisor"]!,
            Carrier: plan.Freight.Cheapest.CarrierId,
            EtaDays: plan.Freight.Cheapest.TransitDays);
        Console.WriteLine(
            $"[dispatch] {result.OrderId} → {result.Warehouse} (supervisor {result.Supervisor}) "
            + $"via {result.Carrier}, ETA {result.EtaDays}d");
        return ValueTask.FromResult(result);
    }
}

internal sealed class FinanceExecutor : Executor<DispatchResult>
{
    public FinanceExecutor() : base("finance") { }

    protected override ProtocolBuilder ConfigureProtocol(ProtocolBuilder protocol) =>
        base.ConfigureProtocol(protocol).YieldsOutputType(typeof(ShippedVoucher));

    public override async ValueTask HandleAsync(
        DispatchResult result, IWorkflowContext context, CancellationToken cancellationToken = default)
    {
        ShippedVoucher voucher = new(
            "shipped", result.OrderId, result.Warehouse,
            result.Supervisor, result.Carrier, result.EtaDays);
        Console.WriteLine($"[finance] {result.OrderId} voucher issued — shipped via {result.Carrier}");
        await context.YieldOutputAsync(voucher, cancellationToken).ConfigureAwait(false);
    }
}

// ───────────────────────────────────────────────────────────────────────────
// Workflow assembly
// ───────────────────────────────────────────────────────────────────────────

internal static class WorkflowFactory
{
    public static (Workflow Workflow, CheckpointManager Checkpoints, DirectoryInfo Dir) Build(string checkpointDir)
    {
        IntakeExecutor intake = new();
        StockCheckExecutor stockCheck = new();
        ShippingQuoteExecutor shippingQuote = new();
        AllocatorExecutor allocator = new();
        ApprovalGateExecutor gate = new();
        ApprovalRequestBuilderExecutor requestBuilder = new();
        RequestPort<HumanApprovalRequest, HumanApprovalResponse> approvalPort =
            RequestPort.Create<HumanApprovalRequest, HumanApprovalResponse>(Constants.ApprovalPortId);
        ApprovalResumeExecutor resume = new();
        DispatchExecutor dispatch = new();
        FinanceExecutor finance = new();

        Workflow workflow = new WorkflowBuilder(intake)
            .WithName("ZavaFulfillment")
            .WithDescription("ZavaShop order fulfillment workflow with HITL approval gate.")
            .AddFanOutEdge(intake, [stockCheck, shippingQuote])
            .AddFanInBarrierEdge([stockCheck, shippingQuote], allocator)
            .AddEdge<AllocationPlan?>(allocator, gate, plan => plan is not null)
            .AddEdge<AllocationPlan>(gate, dispatch, plan => plan!.TotalUsd < Constants.HitlThresholdUsd)
            .AddEdge<AllocationPlan>(gate, requestBuilder, plan => plan!.TotalUsd >= Constants.HitlThresholdUsd)
            .AddEdge(requestBuilder, approvalPort)
            .AddEdge(approvalPort, resume)
            .AddEdge(resume, dispatch)
            .AddEdge(dispatch, finance)
            .WithOutputFrom(finance, resume)
            .Build();

        DirectoryInfo dir = new(checkpointDir);
        dir.Create();
        FileSystemJsonCheckpointStore store = new(dir);
        CheckpointManager mgr = CheckpointManager.CreateJson(store, customOptions: null);
        return (workflow, mgr, dir);
    }
}

// ───────────────────────────────────────────────────────────────────────────
// Demo driver
// ───────────────────────────────────────────────────────────────────────────
// LAB 5 links this file (with the LAB05_AGUI_HOST constant defined) to reuse
// the workflow types — but it brings its own ASP.NET Core entry point, so the
// CLI Main below is gated out for that consumer.
#if !LAB05_AGUI_HOST

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        JsonNode? stock = ZavaData.FindStock("SKU-7421", "SEA-01");
        int onHand = stock is null ? 0 : (int)stock["on_hand"]!;
        Console.WriteLine($"[smoke] find_stock(SKU-7421, SEA-01).on_hand = {onHand}  (expected: 312)");

        string checkpointDir = Path.Combine(AppContext.BaseDirectory, ".checkpoints");
        var (workflow, checkpoints, dir) = WorkflowFactory.Build(checkpointDir);
        Console.WriteLine($"[setup] checkpoints → {dir.FullName}");

        IReadOnlyList<string> targets = args.Length == 0
            ? new[] { "ORD-20260524-001", "ORD-20260524-002" }
            : args;

        bool sawHitlScenario = false;
        foreach (string orderId in targets)
        {
            bool isHitl = await RunScenarioAsync(workflow, checkpoints, orderId).ConfigureAwait(false);
            sawHitlScenario |= isHitl;
        }

        if (sawHitlScenario)
        {
            await RunResumeDemoAsync(workflow, checkpoints, "ORD-20260524-002").ConfigureAwait(false);
        }

        // Criterion: workflow.as_agent() callable from external code (full exercise is LAB 5).
        Console.WriteLine();
        Console.WriteLine("════════════════════════════════════════════════════════════════════════");
        Console.WriteLine("  workflow.AsAIAgent(...) handle (the Python `workflow.as_agent()` analog)");
        Console.WriteLine("════════════════════════════════════════════════════════════════════════");
        AIAgent fulfillmentAgent = workflow.AsAIAgent(
            id: "zava-fulfillment",
            name: "ZavaFulfillment",
            description: "ZavaShop order fulfillment workflow surfaced as an AIAgent for LAB 5.");
        Console.WriteLine($"  ✓ AIAgent ready — id={fulfillmentAgent.Id} name={fulfillmentAgent.Name}");
        Console.WriteLine($"    description: {fulfillmentAgent.Description}");

        return 0;
    }

    private static async Task<bool> RunScenarioAsync(
        Workflow workflow, CheckpointManager checkpoints, string orderId)
    {
        Console.WriteLine();
        Console.WriteLine(new string('─', 72));
        Console.WriteLine($"  Scenario: {orderId}");
        Console.WriteLine(new string('─', 72));

        string sessionId = $"run-{orderId}-{DateTime.UtcNow:yyyyMMddHHmmssfff}";
        await using StreamingRun run = await InProcessExecution
            .RunStreamingAsync(workflow, orderId, checkpoints, sessionId: sessionId)
            .ConfigureAwait(false);

        return await DriveAsync(run, requireOutput: true, checkConcurrency: true).ConfigureAwait(false);
    }

    private static async Task<bool> DriveAsync(StreamingRun run, bool requireOutput, bool checkConcurrency = true)
    {
        bool sawHitl = false;
        List<string> invokeOrder = new();
        bool gotOutput = false;

        await foreach (WorkflowEvent evt in run.WatchStreamAsync().ConfigureAwait(false))
        {
            switch (evt)
            {
                case ExecutorInvokedEvent invoked:
                    invokeOrder.Add(invoked.ExecutorId);
                    Console.WriteLine($"  ► invoked    {invoked.ExecutorId}");
                    break;

                case ExecutorCompletedEvent completed:
                    Console.WriteLine($"  ✓ completed  {completed.ExecutorId}");
                    break;

                case SuperStepCompletedEvent step:
                    string ckId = step.CompletionInfo?.Checkpoint?.CheckpointId ?? "(none)";
                    Console.WriteLine($"  ◆ superstep  #{step.StepNumber} checkpoint={ckId}");
                    break;

                case RequestInfoEvent reqEvt:
                    sawHitl = true;
                    if (reqEvt.Request.TryGetDataAs<HumanApprovalRequest>(out HumanApprovalRequest? approvalReq)
                        && approvalReq is not null)
                    {
                        Console.WriteLine($"  ⏸  request_info pending approval for {approvalReq.OrderId}");
                        Console.WriteLine($"       data = {approvalReq}");
                        HumanApprovalResponse decision = AskHuman(approvalReq);
                        ExternalResponse response = reqEvt.Request.CreateResponse(decision);
                        await run.SendResponseAsync(response).ConfigureAwait(false);
                    }
                    else
                    {
                        Console.Error.WriteLine("  ✗ unhandled request_info payload type");
                    }
                    break;

                case WorkflowOutputEvent output:
                    Console.WriteLine($"  ★ output     {output.Data}");
                    gotOutput = true;
                    break;

                case ExecutorFailedEvent failed:
                    Console.Error.WriteLine($"  ✗ executor   {failed.ExecutorId} failed: {failed.Data}");
                    return sawHitl;

                case WorkflowErrorEvent err:
                    Console.Error.WriteLine($"  ✗ workflow   error: {err.Data}");
                    return sawHitl;
            }
        }

        if (checkConcurrency)
        {
            int idxStock = invokeOrder.IndexOf("stock_check");
            int idxFreight = invokeOrder.IndexOf("shipping_quote");
            int idxAlloc = invokeOrder.IndexOf("allocator");
            if (idxStock < 0 || idxFreight < 0 || idxAlloc < 0
                || idxAlloc < idxStock || idxAlloc < idxFreight)
            {
                Console.Error.WriteLine(
                    $"  ! concurrency check failed: invoke order was [{string.Join(", ", invokeOrder)}]");
            }
            else
            {
                Console.WriteLine("  ✓ concurrency check: stock_check and shipping_quote ran before allocator");
            }
        }

        if (requireOutput && !gotOutput)
        {
            Console.Error.WriteLine("  ! warning: workflow ended without a WorkflowOutputEvent");
        }

        return sawHitl;
    }

    private static HumanApprovalResponse AskHuman(HumanApprovalRequest req)
    {
        string? autoEnv = Environment.GetEnvironmentVariable("LAB04_AUTO_APPROVE");
        if (!string.IsNullOrWhiteSpace(autoEnv))
        {
            string trimmed = autoEnv.Trim();
            bool approved = trimmed.Equals("yes", StringComparison.OrdinalIgnoreCase)
                            || trimmed.Equals("y", StringComparison.OrdinalIgnoreCase)
                            || trimmed.Equals("true", StringComparison.OrdinalIgnoreCase);
            string reason = approved
                ? $"auto-approved via LAB04_AUTO_APPROVE for {req.OrderId}"
                : $"auto-rejected via LAB04_AUTO_APPROVE for {req.OrderId}";
            Console.WriteLine($"       LAB04_AUTO_APPROVE={autoEnv} → approved={approved}");
            return new HumanApprovalResponse(approved, reason);
        }

        Console.Write(
            $"       Approve order {req.OrderId} (customer {req.CustomerId}) "
            + $"totaling ${req.TotalUsd} [reason: {req.Reason}]? [y/N]: ");
        string? line = Console.ReadLine();
        bool yes = line is not null
                   && (line.Trim().Equals("y", StringComparison.OrdinalIgnoreCase)
                       || line.Trim().Equals("yes", StringComparison.OrdinalIgnoreCase));
        return new HumanApprovalResponse(yes, yes ? "supervisor approved" : "supervisor declined");
    }

    private static async Task RunResumeDemoAsync(
        Workflow workflow, CheckpointManager checkpoints, string orderId)
    {
        Console.WriteLine();
        Console.WriteLine(new string('═', 72));
        Console.WriteLine("  Resume demo: re-run from a mid-flight checkpoint");
        Console.WriteLine(new string('═', 72));

        string seedSession = $"resume-seed-{orderId}-{DateTime.UtcNow:yyyyMMddHHmmssfff}";
        CheckpointInfo? pausedAt = null;

        await using (StreamingRun seed = await InProcessExecution
            .RunStreamingAsync(workflow, orderId, checkpoints, sessionId: seedSession)
            .ConfigureAwait(false))
        {
            await foreach (WorkflowEvent evt in seed.WatchStreamAsync().ConfigureAwait(false))
            {
                if (evt is SuperStepCompletedEvent step && step.CompletionInfo?.Checkpoint is { } cp)
                {
                    pausedAt = cp;
                }
                if (evt is RequestInfoEvent)
                {
                    Console.WriteLine($"  ⏸  seed run paused at HITL; {seed.Checkpoints.Count} checkpoint(s) captured");
                    break;
                }
                if (evt is WorkflowOutputEvent)
                {
                    Console.WriteLine("  ! seed run finished without pausing — nothing to resume");
                    return;
                }
            }
        }

        if (pausedAt is null)
        {
            Console.WriteLine("  ! no checkpoint captured; aborting resume demo");
            return;
        }

        Console.WriteLine($"  ↻ resuming from checkpoint {pausedAt.CheckpointId} (session {pausedAt.SessionId})…");
        Environment.SetEnvironmentVariable("LAB04_AUTO_APPROVE", "yes");
        try
        {
            await using StreamingRun resumed = await InProcessExecution
                .ResumeStreamingAsync(workflow, pausedAt, checkpoints)
                .ConfigureAwait(false);
            await DriveAsync(resumed, requireOutput: true, checkConcurrency: false).ConfigureAwait(false);
        }
        finally
        {
            Environment.SetEnvironmentVariable("LAB04_AUTO_APPROVE", null);
        }
    }
}

#endif
