using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

using Azure.AI.Projects;
using Azure.Core;
using Azure.Identity;

using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

using ModelContextProtocol.Client;

using ZavaShop.Workshop.Data;

namespace ZavaShop.LAB02.PierreAgent;

public static class Program
{
    public static async Task<int> Main()
    {
        LoadEnv();

        string endpoint = Environment.GetEnvironmentVariable("FOUNDRY_PROJECT_ENDPOINT")
            ?? throw new InvalidOperationException("FOUNDRY_PROJECT_ENDPOINT is not set.");
        string model = Environment.GetEnvironmentVariable("FOUNDRY_MODEL")
            ?? throw new InvalidOperationException("FOUNDRY_MODEL is not set.");

        // Optional Foundry Toolbox endpoint. When unset we fall back to the public
        // Microsoft Learn MCP so the wiring still demonstrably runs end-to-end.
        string? toolboxEndpoint = Environment.GetEnvironmentVariable("FOUNDRY_TOOLBOX_ENDPOINT");
        bool useRealToolbox = !string.IsNullOrWhiteSpace(toolboxEndpoint);

        TokenCredential credential = new AzureCliCredential();
        AIProjectClient projectClient = new(new Uri(endpoint), credential);

        // ---------------------------------------------------------------
        // 1. MCP wiring — local McpClient so we can pass tools into the
        //    stateless AsAIAgent(model, instructions, name, tools:) overload.
        // ---------------------------------------------------------------
        HttpClient? bearerHttpClient = null;
        Uri mcpUri;
        Dictionary<string, string>? mcpHeaders = null;
        string mcpLabel;

        if (useRealToolbox)
        {
            mcpUri = new Uri(toolboxEndpoint!);
            mcpLabel = "zavashop-procurement";
            bearerHttpClient = new HttpClient(new BearerTokenHandler(credential, "https://ai.azure.com/.default")
            {
                InnerHandler = new HttpClientHandler(),
            });
            mcpHeaders = new Dictionary<string, string>
            {
                ["Foundry-Features"] = "Toolboxes=V1Preview",
            };
        }
        else
        {
            mcpUri = new Uri("https://learn.microsoft.com/api/mcp");
            mcpLabel = "Microsoft Learn MCP";
        }

        HttpClientTransportOptions transportOptions = new()
        {
            Endpoint = mcpUri,
            Name = mcpLabel,
            TransportMode = HttpTransportMode.StreamableHttp,
        };
        if (mcpHeaders is not null)
        {
            transportOptions.AdditionalHeaders = mcpHeaders;
        }

        HttpClientTransport transport = bearerHttpClient is null
            ? new HttpClientTransport(transportOptions)
            : new HttpClientTransport(transportOptions, bearerHttpClient);

        await using McpClient mcpClient = await McpClient.CreateAsync(transport);
        IList<McpClientTool> mcpTools = await mcpClient.ListToolsAsync();
        Console.WriteLine($"MCP source: {mcpLabel} ({mcpUri})");
        Console.WriteLine($"  {mcpTools.Count} tools exposed.");

        // ---------------------------------------------------------------
        // 2. Local function tools — same role as bootstrap_toolbox.py's
        //    get_supplier / get_contract fallbacks. They guarantee the
        //    model can always quote real fixtures, even when the toolbox
        //    MCP isn't provisioned yet.
        // ---------------------------------------------------------------
        AITool getSupplierTool = AIFunctionFactory.Create(GetSupplier);
        AITool getContractTool = AIFunctionFactory.Create(GetContract);

        List<AITool> tools =
        [
            getSupplierTool,
            getContractTool,
            .. mcpTools.Cast<AITool>(),
        ];

        // ---------------------------------------------------------------
        // 3. Procurement skill — InlineSkill with two layers of guardrail.
        //    Skill names must be kebab-case (^[a-z][a-z0-9-]*[a-z0-9]$).
        // ---------------------------------------------------------------
        AgentInlineSkill procurementSkill = new AgentInlineSkill(
                name: "procurement-actions",
                description: "Submit / modify Purchase Orders for approved suppliers.",
                instructions:
                    "Use submit_po to submit a PO. Before submitting, confirm SKU, quantity " +
                    "and unit price, and ALWAYS look up the supplier's contract first — if the " +
                    "order exceeds max_single_po_usd, propose splitting the order instead of " +
                    "forcing it through.")
            .AddScript("submit_po", SubmitPo);

        AgentSkillsProvider skillsProvider = new(
            new[] { procurementSkill },
            new AgentSkillsProviderOptions { ScriptApproval = true });

        // ---------------------------------------------------------------
        // 4. Build the agent. ChatClientAgentOptions lets us attach the
        //    skills provider via AIContextProviders.
        // ---------------------------------------------------------------
        AIAgent agent = projectClient.AsAIAgent(new ChatClientAgentOptions
        {
            Name = "Pierre",
            ChatOptions = new()
            {
                ModelId = model,
                Instructions =
                    "You are Pierre, the AI procurement agent for ZavaShop's Shanghai office. " +
                    "When asked about a supplier or contract, call GetSupplier / GetContract first to " +
                    "ground your answer in real data. To submit purchase orders, use the procurement-actions " +
                    "skill's submit_po script. Never invent supplier ids, contract ids, or prices.",
                Tools = tools.Cast<AITool>().ToList(),
            },
            AIContextProviders = [skillsProvider],
        });

        // ---------------------------------------------------------------
        // 5. Three-turn conversation, single AgentSession.
        // ---------------------------------------------------------------
        AgentSession session = await agent.CreateSessionAsync();

        string[] queries =
        [
            "What is the latest contract with SUP-001 (YiwuClay)? Quote me the negotiated unit price for SKU-3055.",
            "Good. Submit a PO to SUP-001 for SKU-3055 x 200 at the negotiated price.",
            "Add another one: same supplier, SKU-7421 x 5000 units at $25 each.",
        ];

        for (int i = 0; i < queries.Length; i++)
        {
            Console.WriteLine();
            Console.WriteLine($"--- Turn {i + 1} ---");
            Console.WriteLine($"USER: {queries[i]}");

            AgentResponse response = await agent.RunAsync(queries[i], session);

            // Auto-approve any procurement-script approval requests.
            // The cap-guard inside SubmitPo enforces the contract ceiling
            // BEFORE the model ever sees the result, so blanket-approving here
            // is safe: the rejected case still surfaces as [REJECTED ...].
            while (true)
            {
                List<ToolApprovalRequestContent> approvals = response.Messages
                    .SelectMany(m => m.Contents)
                    .OfType<ToolApprovalRequestContent>()
                    .ToList();
                if (approvals.Count == 0) { break; }

                foreach (ToolApprovalRequestContent req in approvals)
                {
                    Console.WriteLine($"[approval] auto-approving request {req.RequestId} ({req.ToolCall.GetType().Name})");
                }

                List<AIContent> responses = approvals
                    .Select(req => (AIContent)req.CreateResponse(approved: true, reason: "demo auto-approve"))
                    .ToList();
                ChatMessage approvalMessage = new(ChatRole.User, responses);
                response = await agent.RunAsync(new[] { approvalMessage }, session);
            }

            Console.WriteLine($"PIERRE: {response}");
        }

        bearerHttpClient?.Dispose();
        return 0;
    }

    // -------------------------------------------------------------------
    // Local function tools — pull straight from the JSON fixtures.
    // -------------------------------------------------------------------

    [Description("Look up a ZavaShop supplier by id (e.g. 'SUP-001') or name (e.g. 'YiwuClay').")]
    static string GetSupplier(
        [Description("Supplier id or name.")] string supplier)
    {
        JsonNode? sup = ZavaData.FindSupplier(supplier);
        return sup?.ToJsonString() ?? $"{{\"error\":\"Unknown supplier '{supplier}'.\"}}";
    }

    [Description("Look up the framework contract for a supplier (by supplier id).")]
    static string GetContract(
        [Description("Supplier id, e.g. SUP-001.")] string supplierId)
    {
        JsonNode? contract = ZavaData.FindContract(supplierId);
        return contract?.ToJsonString() ?? $"{{\"error\":\"No contract on file for {supplierId}.\"}}";
    }

    // -------------------------------------------------------------------
    // The action-grade script behind the procurement-actions skill.
    // Layer 1 of the guardrail (contract-cap check) lives here; the
    // AgentSkillsProvider can layer human approval on top.
    // -------------------------------------------------------------------

    [Description("Submit a purchase order for an approved supplier.")]
    static string SubmitPo(
        [Description("Supplier id or name.")] string supplier,
        [Description("SKU id, e.g. SKU-3055.")] string sku,
        [Description("Order quantity.")] int qty,
        [Description("Unit price in USD.")] double unitPrice)
    {
        JsonNode? sup = ZavaData.FindSupplier(supplier);
        if (sup is null) { return $"[REJECTED] Unknown supplier '{supplier}'."; }

        string supplierId = sup["supplier_id"]!.GetValue<string>();
        JsonNode? contract = ZavaData.FindContract(supplierId);
        double total = qty * unitPrice;

        if (contract is not null)
        {
            double cap = contract["max_single_po_usd"]!.GetValue<double>();
            if (total > cap)
            {
                return $"[REJECTED] PO total ${total:N0} exceeds contract " +
                       $"{contract["contract_id"]} ceiling ${cap:N0}. " +
                       "Suggest splitting into multiple POs.";
            }
        }

        return $"[OK] Submitted PO supplier={sup["name"]} ({supplierId}) sku={sku} " +
               $"qty={qty} unit_price={unitPrice} total=${total:N0}";
    }

    // -------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------

    static void LoadEnv()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        string? envPath = null;
        while (dir is not null)
        {
            string candidate = Path.Combine(dir.FullName, "workshop", ".env");
            if (File.Exists(candidate)) { envPath = candidate; break; }
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

// ---------------------------------------------------------------------
// BearerTokenHandler — refreshes the AAD bearer token on every MCP call.
// Only used when FOUNDRY_TOOLBOX_ENDPOINT is set; the Learn MCP fallback
// is unauthenticated so the handler stays out of that code path.
// ---------------------------------------------------------------------

internal sealed class BearerTokenHandler(TokenCredential credential, string scope) : DelegatingHandler
{
    private readonly TokenRequestContext _tokenContext = new(new[] { scope });

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        AccessToken token = await credential
            .GetTokenAsync(_tokenContext, cancellationToken)
            .ConfigureAwait(false);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }
}
