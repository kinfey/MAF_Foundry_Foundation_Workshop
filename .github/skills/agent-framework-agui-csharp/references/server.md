# AG-UI Server Reference (.NET)

How to host one or more `AIAgent`s behind the AG-UI protocol using `Microsoft.Agents.AI.Hosting.AGUI.AspNetCore`.

## DI Wiring

A minimal AG-UI server is a normal ASP.NET Core app with two additions:

```csharp
WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// 1) AG-UI services in DI
builder.Services.AddAGUI();

// 2) JSON serializer context for the wire payloads
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.TypeInfoResolverChain.Add(AGUIServerSerializerContext.Default));

// 3) Register the agent(s) with a session store
builder
    .AddAIAgent(AgentName, (_, _) => agent)
    .WithInMemorySessionStore();

WebApplication app = builder.Build();
app.MapAGUI(AgentName, "/");
await app.RunAsync();
```

### Mapping overloads

| Overload | Purpose |
|----------|---------|
| `app.MapAGUI(string agentName, string path)` | Resolve the agent registered with `AddAIAgent(agentName, ...)` and bind it to `path`. |
| `app.MapAGUI(string path, AIAgent agent)` | Bind a directly constructed `AIAgent` to `path`. No DI registration needed. |

Both return an `IEndpointRouteBuilder` you can chain `.RequireAuthorization()` / `.WithName()` etc. onto.

## Building the Agent

`AsAIAgent(...)` is the typical hand-off from `IChatClient` to `AIAgent`. The relevant overloads are:

```csharp
// Simple
ChatClient chatClient = new AzureOpenAIClient(new Uri(endpoint), new DefaultAzureCredential())
    .GetChatClient(deploymentName);

AIAgent agent = chatClient.AsAIAgent(
    name: "AGUIAssistant",
    description: "A helpful assistant.",
    tools: [ /* backend tools */ ]);
```

```csharp
// Full options — instructions, tools, response format, tool-call policy
AIAgent agent = chatClient.AsAIAgent(new ChatClientAgentOptions
{
    Name = "MyAgent",
    Description = "...",
    ChatOptions = new ChatOptions
    {
        Instructions = "You are a careful assistant.",
        Tools = [ /* AIFunction[] */ ],
        AllowMultipleToolCalls = false,
        // ResponseFormat = ChatResponseFormat.ForJsonSchema<MySchema>(),
    },
});
```

## Backend Tools

Tools registered on the server execute server-side. The client only sees text deltas and (optionally) `FunctionResultContent` events.

```csharp
tools: [
    AIFunctionFactory.Create(
        () => DateTimeOffset.UtcNow,
        name: "get_current_time",
        description: "Get the current UTC time."),

    AIFunctionFactory.Create(
        ([Description("The weather forecast request")] ServerWeatherForecastRequest request) =>
            new ServerWeatherForecastResponse
            {
                Summary = "Sunny",
                TemperatureC = 25,
                Date = request.Date,
            },
        name: "get_server_weather_forecast",
        description: "Gets the forecast for a specific location and date",
        AGUIServerSerializerContext.Default.Options),
]
```

Tools that take or return a custom type **must** be created with an explicit `JsonSerializerOptions` rooted in a source-generated `JsonSerializerContext` (the trailing argument above). Without it, the serializer falls back to reflection — fine in dev, broken under AOT/trimming.

## Session Storage

`AG-UI` clients send `threadId` — the server maps that to an `AgentSession` via a session store. Choose one:

```csharp
// In-memory (dev / single-instance)
builder.AddAIAgent(AgentName, (_, _) => agent).WithInMemorySessionStore();

// Persistent (production) — implement and register your own
builder.AddAIAgent(AgentName, (_, _) => agent).WithSessionStore<RedisSessionStore>();
```

Without a session store, every request is treated as a brand-new conversation.

## Multiple Endpoints, One Server

Register many agents on different routes to support multiple frontend scenarios. Mirrors `AGUIDojoServer/Program.cs`:

```csharp
builder.Services.AddAGUI();
ChatClientAgentFactory.Initialize(app.Configuration);

app.MapAGUI("/agentic_chat",             ChatClientAgentFactory.CreateAgenticChat());
app.MapAGUI("/backend_tool_rendering",   ChatClientAgentFactory.CreateBackendToolRendering());
app.MapAGUI("/human_in_the_loop",        ChatClientAgentFactory.CreateHumanInTheLoop());
app.MapAGUI("/tool_based_generative_ui", ChatClientAgentFactory.CreateToolBasedGenerativeUI());

var jsonOptions = app.Services
    .GetRequiredService<IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>>();

app.MapAGUI("/agentic_generative_ui",    ChatClientAgentFactory.CreateAgenticUI(jsonOptions.Value.SerializerOptions));
app.MapAGUI("/shared_state",             ChatClientAgentFactory.CreateSharedState(jsonOptions.Value.SerializerOptions));
app.MapAGUI("/predictive_state_updates", ChatClientAgentFactory.CreatePredictiveStateUpdates(jsonOptions.Value.SerializerOptions));
```

A single `AzureOpenAIClient` is reused across all factories — only the `name`, `description`, `tools`, and any wrapping `DelegatingAIAgent` differ:

```csharp
internal static class ChatClientAgentFactory
{
    private static AzureOpenAIClient? s_client;
    private static string? s_deployment;

    public static void Initialize(IConfiguration configuration)
    {
        string endpoint = configuration["AZURE_OPENAI_ENDPOINT"]
            ?? throw new InvalidOperationException("AZURE_OPENAI_ENDPOINT is not set.");
        s_deployment = configuration["AZURE_OPENAI_DEPLOYMENT_NAME"]
            ?? throw new InvalidOperationException("AZURE_OPENAI_DEPLOYMENT_NAME is not set.");
        s_client = new AzureOpenAIClient(new Uri(endpoint), new DefaultAzureCredential());
    }

    public static ChatClientAgent CreateAgenticChat() =>
        s_client!.GetChatClient(s_deployment!).AsAIAgent(
            name: "AgenticChat",
            description: "A simple chat agent using Azure OpenAI");

    public static ChatClientAgent CreateBackendToolRendering() =>
        s_client!.GetChatClient(s_deployment!).AsAIAgent(
            name: "BackendToolRenderer",
            description: "An agent that uses backend tools",
            tools: [
                AIFunctionFactory.Create(
                    GetWeather,
                    name: "get_weather",
                    description: "Get the weather for a given location.",
                    AGUIDojoServerSerializerContext.Default.Options),
            ]);
}
```

## Customizing Behavior with `DelegatingAIAgent`

When you need to inject AG-UI-specific behavior — state snapshots, predictive streaming, multi-turn rewrites — wrap an inner agent. `DelegatingAIAgent` is the supported extension point.

### Pattern: shared client state → server-side structured snapshot

The client sends its UI state in `ChatOptions.AdditionalProperties["ag_ui_state"]`. The server merges it into a system message, runs the inner agent against a JSON-schema response format, deserializes the result, and emits a `DataContent` snapshot back to the client.

```csharp
internal sealed class SharedStateAgent(AIAgent innerAgent, JsonSerializerOptions options)
    : DelegatingAIAgent(innerAgent)
{
    protected override Task<AgentResponse> RunCoreAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session = null,
        AgentRunOptions? options = null,
        CancellationToken cancellationToken = default) =>
        this.RunCoreStreamingAsync(messages, session, options, cancellationToken)
            .ToAgentResponseAsync(cancellationToken);

    protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session = null,
        AgentRunOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Pass through when no state is supplied.
        if (options is not ChatClientAgentRunOptions { ChatOptions.AdditionalProperties: { } props } chatRunOptions ||
            !props.TryGetValue("ag_ui_state", out JsonElement state))
        {
            await foreach (var u in InnerAgent.RunStreamingAsync(messages, session, options, cancellationToken))
            {
                yield return u;
            }
            yield break;
        }

        // Force structured output for the first turn.
        var firstRunOptions = new ChatClientAgentRunOptions
        {
            ChatOptions = chatRunOptions.ChatOptions.Clone(),
            AllowBackgroundResponses = chatRunOptions.AllowBackgroundResponses,
            ContinuationToken = chatRunOptions.ContinuationToken,
            ChatClientFactory = chatRunOptions.ChatClientFactory,
        };
        firstRunOptions.ChatOptions.ResponseFormat = ChatResponseFormat.ForJsonSchema<RecipeResponse>(
            schemaName: "RecipeResponse",
            schemaDescription: "A response containing a recipe");

        ChatMessage stateMsg = new(ChatRole.System,
        [
            new TextContent("Here is the current state in JSON format:"),
            new TextContent(state.GetRawText()),
            new TextContent("The new state is:"),
        ]);

        var firstMessages = messages.Append(stateMsg);
        var allUpdates = new List<AgentResponseUpdate>();

        await foreach (var update in InnerAgent.RunStreamingAsync(firstMessages, session, firstRunOptions, cancellationToken))
        {
            allUpdates.Add(update);
            // Forward tool calls; suppress text (it's the structured JSON).
            if (update.Contents.Any(c => c is not TextContent))
            {
                yield return update;
            }
        }

        var response = allUpdates.ToAgentResponse();

        if (!TryDeserialize<JsonElement>(response.Text, options, out var stateSnapshot))
        {
            yield break;
        }

        // Emit the structured state snapshot as a DataContent event.
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(
            stateSnapshot,
            options.GetTypeInfo(typeof(JsonElement)));

        yield return new AgentResponseUpdate
        {
            Contents = [new DataContent(bytes, "application/json")],
        };

        // Second turn: short natural-language summary.
        var secondMessages = messages.Concat(response.Messages).Append(
            new ChatMessage(ChatRole.System,
                [new TextContent("Please provide a concise summary of the state changes in at most two sentences.")]));

        await foreach (var u in InnerAgent.RunStreamingAsync(secondMessages, session, options, cancellationToken))
        {
            yield return u;
        }
    }

    private static bool TryDeserialize<T>(string json, JsonSerializerOptions o, out T value)
    {
        try
        {
            T? r = JsonSerializer.Deserialize<T>(json, o);
            if (r is null) { value = default!; return false; }
            value = r;
            return true;
        }
        catch
        {
            value = default!;
            return false;
        }
    }
}
```

### Pattern: predictive state updates (faux-streaming a tool argument)

While the model is still emitting a `write_document` tool call, "fake-stream" the partial document back to the client as a series of `DataContent` snapshots so the frontend can render progressively.

```csharp
internal sealed class PredictiveStateUpdatesAgent(AIAgent innerAgent, JsonSerializerOptions options)
    : DelegatingAIAgent(innerAgent)
{
    private const int ChunkSize = 10;

    protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session = null,
        AgentRunOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        string? lastEmitted = null;

        await foreach (var update in InnerAgent.RunStreamingAsync(messages, session, options, cancellationToken))
        {
            string? document = null;
            foreach (var c in update.Contents)
            {
                if (c is FunctionCallContent call && call.Name == "write_document" &&
                    call.Arguments?.TryGetValue("document", out var v) == true)
                {
                    document = v?.ToString();
                }
            }

            yield return update;

            if (document is null || document == lastEmitted) { continue; }

            int start = lastEmitted is not null && document.StartsWith(lastEmitted, StringComparison.Ordinal)
                ? lastEmitted.Length : 0;

            for (int i = start; i < document.Length; i += ChunkSize)
            {
                int len = Math.Min(ChunkSize, document.Length - i);
                string chunk = document[..(i + len)];

                byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(
                    new DocumentState { Document = chunk },
                    options.GetTypeInfo(typeof(DocumentState)));

                yield return new AgentResponseUpdate(
                    new ChatResponseUpdate(role: ChatRole.Assistant,
                        [new DataContent(bytes, "application/json")])
                    {
                        MessageId = "snapshot" + Guid.NewGuid().ToString("N"),
                        CreatedAt = update.CreatedAt,
                        ResponseId = update.ResponseId,
                        AdditionalProperties = update.AdditionalProperties,
                        AuthorName = update.AuthorName,
                        ContinuationToken = update.ContinuationToken,
                    })
                {
                    AgentId = update.AgentId,
                };

                await Task.Delay(50, cancellationToken);
            }

            lastEmitted = document;
        }
    }
}
```

### Pattern: agentic UI (plan + step updates)

Force the model to call planning tools (`create_plan`, `update_plan_step`) instead of speaking. The frontend renders the plan UI from the tool-call stream:

```csharp
ChatOptions = new ChatOptions
{
    Instructions = """
        When planning use tools only, without any other messages.
        IMPORTANT:
        - Use the `create_plan` tool to set the initial state of the steps
        - Use the `update_plan_step` tool to update the status of each step
        - Do NOT repeat the plan or summarise it in a message
        - Continue calling update_plan_step until all steps are marked as completed.
        Only one plan can be active at a time.
        """,
    Tools =
    [
        AIFunctionFactory.Create(AgenticPlanningTools.CreatePlan,
            name: "create_plan",
            description: "Create a plan with multiple steps.",
            AGUIDojoServerSerializerContext.Default.Options),
        AIFunctionFactory.Create(AgenticPlanningTools.UpdatePlanStepAsync,
            name: "update_plan_step",
            description: "Update a step in the plan with new description or status.",
            AGUIDojoServerSerializerContext.Default.Options),
    ],
    AllowMultipleToolCalls = false,
};
```

## HTTP Logging (Dev Only)

For protocol-level debugging, enable full request/response logging — keep it off in production:

```csharp
builder.Services.AddHttpLogging(logging =>
{
    logging.LoggingFields =
        HttpLoggingFields.RequestPropertiesAndHeaders | HttpLoggingFields.RequestBody |
        HttpLoggingFields.ResponsePropertiesAndHeaders | HttpLoggingFields.ResponseBody;
    logging.RequestBodyLogLimit  = int.MaxValue;
    logging.ResponseBodyLogLimit = int.MaxValue;
});

WebApplication app = builder.Build();
app.UseHttpLogging();
```

SSE response bodies are large — restrict this to `Development`:

```csharp
if (app.Environment.IsDevelopment())
{
    app.UseHttpLogging();
}
```
