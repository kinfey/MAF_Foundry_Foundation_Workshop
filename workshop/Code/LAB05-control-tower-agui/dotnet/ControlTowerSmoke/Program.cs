// LAB 5 smoke client — connects to the ControlTower AG-UI server, registers a
// client-side play_alert_sound tool, and runs the same 5-turn script as the
// Python client_smoketest.py.

using System.Net.Http.Headers;

using Microsoft.Agents.AI;
using Microsoft.Agents.AI.AGUI;
using Microsoft.Extensions.AI;

namespace ZavaShop.LAB05.ControlTowerSmoke;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        LoadEnv();

        string serverUrl = Environment.GetEnvironmentVariable("AGUI_SERVER_URL")
            ?? "http://127.0.0.1:5100/";
        string apiKey = Environment.GetEnvironmentVariable("AG_UI_API_KEY")
            ?? "zava-control-tower-demo-key";

        using HttpClient http = new() { Timeout = TimeSpan.FromSeconds(120) };
        http.DefaultRequestHeaders.Add("X-API-Key", apiKey);
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        AIFunction playAlertSound = AIFunctionFactory.Create(
            (string level) =>
            {
                string bell = level == "high" ? "🚨🚨🚨" : "🔔";
                Console.WriteLine();
                Console.WriteLine($"  {bell} [client.play_alert_sound] level={level}");
                return $"alert_played:{level}";
            },
            name: "play_alert_sound",
            description: "Play a UI alert sound. Use level=\"high\" for severe operational events.");

        AGUIChatClient chatClient = new(http, serverUrl);
        AIAgent agent = chatClient.AsAIAgent(
            name: "control-tower-smoke",
            description: "Operations console smoketest client for ZavaControlTower.",
            tools: [playAlertSound]);

        // NOTE: each smoketest turn opens a fresh AgentSession. Foundry's Responses
        // API tracks every function_call in thread state and requires a matching
        // function_call_output — but AG-UI executes client tools locally and the
        // hosting bridge does not persist that result back into the Foundry thread.
        // Reusing a single session across turns therefore wedges with a 400
        // "No tool output found for function call …" on the second turn. Each prompt
        // in this 5-turn script is intentionally self-contained, so a fresh session
        // per turn matches what a real UI does on page transitions.

        (string label, string prompt)[] turns =
        [
            ("Turn 1 — exceptions + alert",
                "What exception orders are open right now? If any of them are high-severity, also call play_alert_sound with level=\"high\" so the night shift hears it."),
            ("Turn 2 — freight quote",
                "Get me freight quotes from US to EU for a 5 kg parcel."),
            ("Turn 3 — under-threshold fulfill",
                "Please fulfill ORD-20260524-001 — it's well under the approval threshold."),
            ("Turn 4 — over-threshold fulfill (HITL)",
                "Now please fulfill ORD-20260524-002 for VIP_003 — it was flagged in the exceptions list."),
            ("Turn 5 — supervisor re-approval",
                "Approved by operations manager. Please re-run fulfill_order on ORD-20260524-002 with supervisor_approval=true."),
        ];

        for (int i = 0; i < turns.Length; i++)
        {
            (string label, string prompt) = turns[i];
            Console.WriteLine();
            Console.WriteLine(new string('─', 72));
            Console.WriteLine($"[{label}]");
            Console.WriteLine($"  user> {prompt}");
            Console.Write("  assistant> ");

            AgentSession session = await agent.CreateSessionAsync();
            List<ChatMessage> messages = new() { new ChatMessage(ChatRole.User, prompt) };

            await foreach (AgentResponseUpdate update in agent.RunStreamingAsync(messages, session))
            {
                foreach (AIContent content in update.Contents)
                {
                    switch (content)
                    {
                        case TextContent text when !string.IsNullOrEmpty(text.Text):
                            Console.Write(text.Text);
                            break;
                        case FunctionCallContent fc:
                            Console.WriteLine();
                            Console.WriteLine($"  → tool call: {fc.Name}({string.Join(", ", fc.Arguments?.Select(kv => $"{kv.Key}={kv.Value}") ?? Array.Empty<string>())})");
                            Console.Write("  assistant> ");
                            break;
                        case FunctionResultContent fr:
                            Console.WriteLine();
                            Console.WriteLine($"  ← tool result: {Truncate(fr.Result?.ToString() ?? "", 240)}");
                            Console.Write("  assistant> ");
                            break;
                        case ErrorContent err:
                            Console.WriteLine();
                            Console.Error.WriteLine($"  !! error: {err.Message}");
                            Console.Write("  assistant> ");
                            break;
                    }
                }
            }
            Console.WriteLine();
        }

        Console.WriteLine();
        Console.WriteLine("[smoke] all 5 turns complete.");
        return 0;
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";

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
