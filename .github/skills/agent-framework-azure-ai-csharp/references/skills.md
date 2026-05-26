# Agent Skills Reference (.NET)

Agent Skills are modular, lazy-loaded capability bundles. They let an agent advertise a name + description + per-skill instructions, and load the full body (resources + scripts) only when the model chooses to use them — known as progressive disclosure.

Three flavors are supported in .NET:

| Flavor | Where defined | Use case |
|---|---|---|
| `AgentInlineSkill` | C# code, ad hoc | Quick skills, demos, in-process |
| `AgentClassSkill<TSelf>` | Class with `[AgentSkillResource]` / `[AgentSkillScript]` attrs | Reusable, DI-friendly, typed |
| File-based skills | `SKILL.md` directory + script files | Polyglot, shippable, portable |

All three plug in through `AgentSkillsProvider` (or `AgentSkillsProviderBuilder`) and are attached via `AIContextProviders`.

---

## `AgentInlineSkill` (Code, Inline)

```csharp
using Microsoft.Agents.AI;
using System.Text.Json;

var unitConverter = new AgentInlineSkill(
        name: "unit-converter",
        description: "Convert between miles/km and lb/kg using a multiplication factor.",
        instructions: "1. Read the conversion-table resource. 2. Call the 'convert' script with the value and factor.")
    .AddResource("conversion-table", """
        | from   | to         | factor   |
        |--------|------------|----------|
        | miles  | kilometers | 1.60934  |
        | km     | miles      | 0.62137  |
        | lb     | kg         | 0.45359  |
        | kg     | lb         | 2.20462  |
        """)
    .AddScript("convert", (double value, double factor) =>
        JsonSerializer.Serialize(new { value, factor, result = Math.Round(value * factor, 4) }));
```

### Dynamic resources

A resource can be a delegate that produces a fresh value on each request — useful for system stats, current time, or user-scoped data:

```csharp
var systemStats = new AgentInlineSkill(
        name: "system-stats",
        description: "Report current process memory and uptime.",
        instructions: "Read the 'live' resource to answer.")
    .AddResource("live", () => JsonSerializer.Serialize(new
    {
        memoryMb = GC.GetTotalMemory(forceFullCollection: false) / (1024 * 1024),
        uptimeSeconds = Environment.TickCount / 1000,
    }));
```

### Attaching to an agent

```csharp
AIAgent agent = projectClient.AsAIAgent(new ChatClientAgentOptions
{
    Name = "UnitConverterAgent",
    ChatOptions = new() { ModelId = deploymentName, Instructions = "You convert units." },
    AIContextProviders = [new AgentSkillsProvider(unitConverter)],
});

Console.WriteLine(await agent.RunAsync("How many km is 26.2 miles?"));
```

### Requiring approval before scripts run

Pass `AgentSkillsProviderOptions { ScriptApproval = true }` to gate every script execution behind a human approval step. The flag lives on the **options** object — there is no ctor parameter named `requireScriptApproval`.

```csharp
AgentSkillsProvider provider = new(
    new[] { procurementSkill },
    new AgentSkillsProviderOptions { ScriptApproval = true });
```

When the agent decides to call a script, the next `RunAsync` returns a `Microsoft.Extensions.AI.ToolApprovalRequestContent` inside the response messages instead of the script's result. Walk every request, reply with `request.CreateResponse(approved, reason)`, wrap the responses in a single user `ChatMessage`, and feed it back to `RunAsync` on the same `AgentSession`:

```csharp
using Microsoft.Extensions.AI;

AgentResponse response = await agent.RunAsync(prompt, session);

while (true)
{
    var approvals = response.Messages
        .SelectMany(m => m.Contents)
        .OfType<ToolApprovalRequestContent>()
        .ToList();
    if (approvals.Count == 0) break;

    var replies = approvals
        .Select(r => (AIContent)r.CreateResponse(approved: true, reason: "policy: auto-approve"))
        .ToList();
    response = await agent.RunAsync(new[] { new ChatMessage(ChatRole.User, replies) }, session);
}
```

`ToolApprovalRequestContent` exposes `RequestId` and a `ToolCall` (the underlying function-call content). Use those for logging or for routing a request to a real human reviewer — return `approved: false` to abort the script run.

> The current prerelease marks `AgentInlineSkill`, `AgentClassSkill<TSelf>`, `AgentSkillsProvider`, and `AgentSkillsProviderOptions` with `[Experimental("MAAI001")]`. Add `MAAI001` to `<NoWarn>` in any csproj that uses them.

---

## `AgentClassSkill<TSelf>` (Class with Attributes)

Best for skills that need DI, complex state, or that ship as part of a library.

```csharp
using Microsoft.Agents.AI;

public sealed class WeatherSkill : AgentClassSkill<WeatherSkill>
{
    public override AgentSkillFrontmatter Frontmatter { get; } =
        new(name: "weather", description: "Look up the weather for cities.");

    protected override string Instructions =>
        """
        1. Read the 'supported-cities' resource to list cities you can answer for.
        2. Call the 'lookup' script with the city name and an ISO date.
        """;

    [AgentSkillResource("supported-cities")]
    public string SupportedCities => "amsterdam, seattle, redmond, hyderabad";

    [AgentSkillScript("lookup")]
    private static string LookupWeather(string city, string isoDate)
        => $"Weather for {city} on {isoDate}: 18°C, sunny.";
}
```

Attach the same way:

```csharp
AIAgent agent = projectClient.AsAIAgent(new ChatClientAgentOptions
{
    Name = "WeatherAgent",
    ChatOptions = new() { ModelId = deploymentName, Instructions = "You answer weather questions." },
    AIContextProviders = [new AgentSkillsProvider(new WeatherSkill())],
});
```

---

## File-Based Skills (Portable, Polyglot)

A file-based skill is a folder containing a `SKILL.md` (YAML frontmatter + instructions) plus optional resource files and a `scripts/` folder. Scripts can be Python, PowerShell, or any binary you can run.

```
my-skills/
└── code-formatter/
    ├── SKILL.md              (frontmatter: name, description; body: instructions)
    ├── style-guide.md        (resource)
    └── scripts/
        └── format.py         (executable script invoked by name "format")
```

Load it with a script runner — `SubprocessScriptRunner` shells out to the script file:

```csharp
using Microsoft.Agents.AI;

string skillsRoot = Path.Combine(AppContext.BaseDirectory, "my-skills");

var provider = new AgentSkillsProvider(skillsRoot, SubprocessScriptRunner.RunAsync);

AIAgent agent = projectClient.AsAIAgent(new ChatClientAgentOptions
{
    Name = "FormatterAgent",
    ChatOptions = new() { ModelId = deploymentName, Instructions = "Help users format code." },
    AIContextProviders = [provider],
});
```

### SKILL.md structure

```markdown
---
name: code-formatter
description: Format source code using a project style guide.
---

# Code Formatter Skill

When the user asks to format code:
1. Read the `style-guide` resource.
2. Pass the source code to the `format` script along with the language.
```

The runtime exposes any sibling file as a resource keyed by file stem, and any file under `scripts/` as a script keyed by file stem.

---

## Mixing All Three with `AgentSkillsProviderBuilder`

```csharp
AgentSkillsProvider provider = new AgentSkillsProviderBuilder()
    .AddInlineSkill(unitConverter)
    .AddClassSkill(new WeatherSkill())
    .AddDirectory(Path.Combine(AppContext.BaseDirectory, "skills"), SubprocessScriptRunner.RunAsync)
    .Build();

AIAgent agent = projectClient.AsAIAgent(new ChatClientAgentOptions
{
    Name = "MultiSkillAgent",
    ChatOptions = new() { ModelId = deploymentName, Instructions = "Use the right skill for each task." },
    AIContextProviders = [provider],
});
```

---

## Dependency Injection

```csharp
using Microsoft.Extensions.DependencyInjection;

services.AddSingleton<WeatherSkill>();
services.AddSingleton<AgentSkillsProvider>(sp => new AgentSkillsProviderBuilder()
    .AddClassSkill(sp.GetRequiredService<WeatherSkill>())
    .Build());

services.AddSingleton<AIProjectClient>(sp =>
    new AIProjectClient(new Uri(endpoint), new DefaultAzureCredential()));

services.AddSingleton<AIAgent>(sp => sp.GetRequiredService<AIProjectClient>().AsAIAgent(new ChatClientAgentOptions
{
    Name = "DIAgent",
    ChatOptions = new() { ModelId = deploymentName, Instructions = "..." },
    AIContextProviders = [sp.GetRequiredService<AgentSkillsProvider>()],
}));
```

---

## Comparison

| Concern | `AgentInlineSkill` | `AgentClassSkill<TSelf>` | File-based |
|---|---|---|---|
| Definition style | Fluent C# | Class + attributes | Markdown + script files |
| Best for | Quick demos, prototyping | Reusable library skills | Polyglot, shippable bundles |
| Hot reload | No | No | Yes (re-read directory) |
| Polyglot | C# only | C# only | Python, PowerShell, anything |
| DI integration | Manual | Native (class constructor) | Limited |

---

## Best Practices

1. **One responsibility per skill** — keep the description specific.
2. **Make instructions imperative** — "1. Read X. 2. Call Y." beats prose.
3. **Use dynamic resources sparingly** — they refresh on every progressive-disclosure load.
4. **For file-based skills, ship a real `SKILL.md`** — the same format the agent runtime parses.
5. **Test the script runner outside the agent** — verify the script works with the exact args the runtime passes.
