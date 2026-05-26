using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

using Azure.AI.Projects;
using Azure.Identity;

using ZavaShop.Workshop.Data;

namespace ZavaShop.LAB02.BootstrapToolbox;

public static class Program
{
    public static async Task<int> Main()
    {
        LoadEnv();

        Console.WriteLine("=== ZavaShop LAB 2 — Bootstrap Toolbox (.NET) ===");
        Console.WriteLine();

        // 1. Fixture sanity check — confirms ZavaData is wired in.
        JsonArray suppliers = ZavaData.LoadSuppliers();
        JsonArray contracts = ZavaData.LoadContracts();
        Console.WriteLine($"Loaded {suppliers.Count} suppliers, {contracts.Count} contracts from workshop/data/.");

        JsonNode? sup001 = ZavaData.FindSupplier("SUP-001");
        JsonNode? sup001Contract = ZavaData.FindContract("SUP-001");
        Console.WriteLine(
            $"  SUP-001 -> {sup001?["name"]}, contract={sup001Contract?["contract_id"]}, " +
            $"max_single_po_usd={sup001Contract?["max_single_po_usd"]}");
        Console.WriteLine();

        // 2. Probe the AgentAdministrationClient surface for toolbox-related methods.
        //    The .NET SKILL describes `adminClient.GetAgentToolboxes()` returning an
        //    `AgentToolboxes` accessor with `CreateToolboxVersionAsync` / `GetToolboxVersionsAsync` /
        //    `DeleteToolboxVersionAsync` / `DeleteToolboxAsync`. Reality varies by prerelease,
        //    so we reflect over the installed types and report what we actually have.
        string endpoint = Environment.GetEnvironmentVariable("FOUNDRY_PROJECT_ENDPOINT")
            ?? throw new InvalidOperationException("FOUNDRY_PROJECT_ENDPOINT is not set.");

        AIProjectClient projectClient = new(new Uri(endpoint), new AzureCliCredential());
        object adminClient = projectClient.AgentAdministrationClient;

        Console.WriteLine($"AgentAdministrationClient type: {adminClient.GetType().FullName}");

        // 3. List methods that look toolbox-related.
        MethodInfo[] adminMethods = adminClient.GetType().GetMethods(
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
        var toolboxMethods = adminMethods
            .Where(m => m.Name.Contains("Toolbox", StringComparison.OrdinalIgnoreCase))
            .Select(m => m.Name)
            .Distinct()
            .OrderBy(n => n)
            .ToList();

        Console.WriteLine($"Toolbox-related methods on AgentAdministrationClient: " +
            (toolboxMethods.Count == 0 ? "<none>" : string.Join(", ", toolboxMethods)));

        // 4. Look up AgentToolboxes accessor type in the loaded assemblies, if any.
        Type? toolboxesType = AppDomain.CurrentDomain
            .GetAssemblies()
            .SelectMany(a =>
            {
                try { return a.GetTypes(); }
                catch { return Array.Empty<Type>(); }
            })
            .FirstOrDefault(t => t.Name == "AgentToolboxes" || t.Name.EndsWith("Toolboxes"));

        if (toolboxesType is not null)
        {
            Console.WriteLine($"Found toolbox accessor type: {toolboxesType.FullName}");
            var toolboxOps = toolboxesType
                .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Select(m => m.Name)
                .Distinct()
                .OrderBy(n => n)
                .ToList();
            Console.WriteLine($"  operations: {string.Join(", ", toolboxOps)}");
        }
        else
        {
            Console.WriteLine("No AgentToolboxes accessor type found in the installed prerelease.");
        }

        // 5. Reality-check the Agent Skills surface used by PierreAgent.
        Type? inlineSkillType = AppDomain.CurrentDomain
            .GetAssemblies()
            .SelectMany(a =>
            {
                try { return a.GetTypes(); }
                catch { return Array.Empty<Type>(); }
            })
            .FirstOrDefault(t => t.Name == "AgentInlineSkill");

        Type? skillsProviderType = AppDomain.CurrentDomain
            .GetAssemblies()
            .SelectMany(a =>
            {
                try { return a.GetTypes(); }
                catch { return Array.Empty<Type>(); }
            })
            .FirstOrDefault(t => t.Name == "AgentSkillsProvider");

        Console.WriteLine();
        Console.WriteLine($"AgentInlineSkill type: {inlineSkillType?.FullName ?? "<missing>"}");
        Console.WriteLine($"AgentSkillsProvider type: {skillsProviderType?.FullName ?? "<missing>"}");

        if (inlineSkillType is not null)
        {
            var ctors = inlineSkillType.GetConstructors();
            foreach (var ctor in ctors)
            {
                Console.WriteLine($"  ctor: ({string.Join(", ", ctor.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"))})");
            }
        }

        if (skillsProviderType is not null)
        {
            var ctors = skillsProviderType.GetConstructors();
            foreach (var ctor in ctors)
            {
                Console.WriteLine($"  ctor: ({string.Join(", ", ctor.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"))})");
            }
        }

        // 6. Try the high-level toolbox accessor if it's exposed via a getter.
        Console.WriteLine();
        try
        {
            MethodInfo? getToolboxes = adminClient.GetType().GetMethod(
                "GetAgentToolboxes",
                BindingFlags.Public | BindingFlags.Instance);
            if (getToolboxes is not null)
            {
                object? accessor = getToolboxes.Invoke(adminClient, Array.Empty<object>());
                Console.WriteLine($"adminClient.GetAgentToolboxes() -> {accessor?.GetType().FullName}");
            }
            else
            {
                Console.WriteLine("adminClient.GetAgentToolboxes() not present in this prerelease.");
                Console.WriteLine("Provision the 'zavashop-procurement' toolbox via the Azure AI Foundry portal:");
                Console.WriteLine("  1. Open the Foundry project at $FOUNDRY_PROJECT_ENDPOINT.");
                Console.WriteLine("  2. Toolboxes -> New -> Name = 'zavashop-procurement'.");
                Console.WriteLine("  3. Add MCP tools for contracts + pricing, set RequireApproval=false.");
                Console.WriteLine("  4. Export FOUNDRY_TOOLBOX_ENDPOINT=<the printed MCP URL> before running PierreAgent.");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"adminClient.GetAgentToolboxes() probe failed: {ex.GetType().Name}: {ex.Message}");
            Console.WriteLine("PierreAgent will fall back to the Microsoft Learn MCP when FOUNDRY_TOOLBOX_ENDPOINT is unset.");
        }

        Console.WriteLine();
        Console.WriteLine("Bootstrap complete.");
        return await Task.FromResult(0);
    }

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
