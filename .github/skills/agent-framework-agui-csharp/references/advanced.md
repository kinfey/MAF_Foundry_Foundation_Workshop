# AG-UI Advanced Patterns (.NET)

Production hardening for AG-UI servers and clients built on `Microsoft.Agents.AI.AGUI` / `Microsoft.Agents.AI.Hosting.AGUI.AspNetCore`.

## 1. Auth in front of `MapAGUI`

AG-UI is plain HTTP/SSE. Put ASP.NET Core auth in front of it — never rely on the protocol layer.

```csharp
builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.Authority = builder.Configuration["Jwt:Authority"];
        options.Audience  = builder.Configuration["Jwt:Audience"];
    });

builder.Services.AddAuthorization();

WebApplication app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();

app.MapAGUI("AGUIAssistant", "/")
   .RequireAuthorization();
```

For per-user agent registrations, resolve the agent factory per request and use the authenticated identity as part of the session key.

## 2. Persistent Session Storage

`.WithInMemorySessionStore()` is in-process only. Production multi-instance deployments need a shared store.

Implement `ISessionStore` (or your local equivalent) backed by Redis / Cosmos DB / Postgres:

```csharp
public sealed class RedisSessionStore : ISessionStore
{
    private readonly IConnectionMultiplexer _redis;
    public RedisSessionStore(IConnectionMultiplexer redis) => _redis = redis;

    public async Task<SessionState?> GetAsync(string sessionId, CancellationToken ct)
    {
        IDatabase db = _redis.GetDatabase();
        RedisValue value = await db.StringGetAsync($"agui:session:{sessionId}");
        return value.IsNullOrEmpty
            ? null
            : JsonSerializer.Deserialize<SessionState>(value!, SessionContext.Default.SessionState);
    }

    public async Task SetAsync(string sessionId, SessionState state, CancellationToken ct)
    {
        IDatabase db = _redis.GetDatabase();
        string payload = JsonSerializer.Serialize(state, SessionContext.Default.SessionState);
        await db.StringSetAsync($"agui:session:{sessionId}", payload, TimeSpan.FromDays(7));
    }
}

builder.Services.AddSingleton<IConnectionMultiplexer>(
    ConnectionMultiplexer.Connect(builder.Configuration["Redis:ConnectionString"]!));
builder.Services.AddSingleton<ISessionStore, RedisSessionStore>();

builder
    .AddAIAgent(AgentName, (_, _) => agent)
    .WithSessionStore<RedisSessionStore>();
```

Add an expiration policy that matches your retention requirements; never let sessions accumulate indefinitely.

## 3. AOT- / Trim-Safe JSON

Every type that crosses the AG-UI wire — request payloads, tool arguments, tool results, state snapshots — must be reachable from a source-generated `JsonSerializerContext`.

```csharp
[JsonSerializable(typeof(SensorRequest))]
[JsonSerializable(typeof(SensorResponse))]
[JsonSerializable(typeof(DocumentState))]
[JsonSerializable(typeof(RecipeResponse))]
internal sealed partial class AGUIDojoServerSerializerContext : JsonSerializerContext;
```

Wire it everywhere:

```csharp
// HTTP serialization (server)
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.TypeInfoResolverChain.Add(AGUIDojoServerSerializerContext.Default));

// Tool argument/result serialization
AIFunctionFactory.Create(
    MyTool, name: "my_tool", description: "...",
    AGUIDojoServerSerializerContext.Default.Options);

// Manual serialization inside DelegatingAIAgent
byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(
    state,
    options.GetTypeInfo(typeof(DocumentState)));

// Client-side (passed to AGUIChatClient)
var chatClient = new AGUIChatClient(http, url,
    jsonSerializerOptions: AGUIClientSerializerContext.Default.Options);
```

For combined contexts, instantiate with merged options:

```csharp
var combined = new JsonSerializerOptions
{
    TypeInfoResolver = JsonTypeInfoResolver.Combine(
        AGUIDojoServerSerializerContext.Default,
        OtherContext.Default),
};
```

## 4. Observability

Three layers, low to high signal-to-noise:

```csharp
// Layer 1 — OpenTelemetry for ASP.NET Core + HTTP client
builder.Services.AddOpenTelemetry()
    .WithTracing(t => t.AddAspNetCoreInstrumentation().AddHttpClientInstrumentation())
    .WithMetrics(m => m.AddAspNetCoreInstrumentation());

// Layer 2 — HTTP logging (dev only; SSE bodies are large)
if (app.Environment.IsDevelopment())
{
    builder.Services.AddHttpLogging(l =>
    {
        l.LoggingFields = HttpLoggingFields.All;
        l.RequestBodyLogLimit  = int.MaxValue;
        l.ResponseBodyLogLimit = int.MaxValue;
    });
    app.UseHttpLogging();
}

// Layer 3 — Custom logging inside DelegatingAIAgent
internal sealed class TracingAgent(AIAgent inner, ILogger<TracingAgent> logger)
    : DelegatingAIAgent(inner)
{
    protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
        IEnumerable<ChatMessage> messages, AgentSession? session, AgentRunOptions? options,
        [EnumeratorCancellation] CancellationToken ct)
    {
        using var activity = ActivitySource.StartActivity("agui.run");
        int count = 0;
        await foreach (var u in InnerAgent.RunStreamingAsync(messages, session, options, ct))
        {
            count++;
            yield return u;
        }
        logger.LogInformation("Run produced {Count} updates", count);
    }
}
```

## 5. Multi-Tenancy

Different tenants → different agents / models / credentials. Resolve the agent factory from the authenticated principal:

```csharp
builder.Services.AddScoped<TenantContext>(sp =>
{
    var http = sp.GetRequiredService<IHttpContextAccessor>().HttpContext!;
    return new TenantContext(http.User.FindFirstValue("tenant_id")!);
});

builder.AddAIAgent("AGUIAssistant", (sp, name) =>
{
    var tenant = sp.GetRequiredService<TenantContext>();
    return AgentRegistry.Get(tenant.Id);   // your per-tenant cache
})
.WithSessionStore<RedisSessionStore>();
```

Combine the tenant id into the session key so threads can never bleed across tenants.

## 6. Production Credentials

Replace `DefaultAzureCredential` for hot paths — credential chain probing adds latency:

```csharp
TokenCredential credential = app.Environment.IsDevelopment()
    ? new DefaultAzureCredential()
    : new ManagedIdentityCredential();   // or WorkloadIdentityCredential on AKS

var aoai = new AzureOpenAIClient(new Uri(endpoint), credential);
```

For local Docker Compose / dev clusters use `AzureCliCredential` explicitly.

## 7. Bound Resources on the Server

SSE runs can be long. Bound them or you'll exhaust thread-pool slots and AOAI quota.

```csharp
// Per-run cap
app.MapAGUI("AGUIAssistant", "/").Add(endpoint =>
{
    var original = endpoint.RequestDelegate!;
    endpoint.RequestDelegate = async ctx =>
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted);
        cts.CancelAfter(TimeSpan.FromMinutes(5));
        var orig = ctx.RequestAborted;
        ctx.RequestAborted = cts.Token;
        try { await original(ctx); }
        finally { ctx.RequestAborted = orig; }
    };
});

// Per-tenant concurrency limit via the rate-limiter middleware
builder.Services.AddRateLimiter(o =>
{
    o.AddPolicy("per-tenant", ctx =>
        RateLimitPartition.GetConcurrencyLimiter(
            ctx.User.FindFirstValue("tenant_id") ?? "anon",
            _ => new ConcurrencyLimiterOptions { PermitLimit = 10, QueueLimit = 0 }));
});

app.UseRateLimiter();
app.MapAGUI("AGUIAssistant", "/").RequireRateLimiting("per-tenant");
```

## 8. Resilience on the Client

Wrap the `HttpClient` with `IHttpClientFactory` + Polly:

```csharp
builder.Services.AddHttpClient("agui", c =>
{
    c.BaseAddress = new Uri(builder.Configuration["AGUI:Url"]!);
    c.Timeout = TimeSpan.FromMinutes(5);
})
.AddStandardResilienceHandler(options =>
{
    // Don't retry SSE bodies — they aren't idempotent halfway through.
    options.Retry.MaxRetryAttempts = 1;
    options.AttemptTimeout.Timeout = TimeSpan.FromMinutes(5);
});

// Resolve at call-site
HttpClient http = httpFactory.CreateClient("agui");
var chatClient = new AGUIChatClient(http, url, jsonSerializerOptions: ctx.Default.Options);
```

Watch out: AG-UI runs are **not safely retriable** mid-stream. Retry only when no event has been observed yet (i.e. before the first delta) and you know the request was idempotent from a business standpoint.

## 9. Structured State Snapshots

When emitting `DataContent` snapshots from a `DelegatingAIAgent`, prefer typed payloads and a typed schema:

```csharp
internal sealed class RecipeResponse
{
    public string Title { get; init; } = "";
    public string SkillLevel { get; init; } = "";
    public string CookingTime { get; init; } = "";
    public string[] Preferences { get; init; } = [];
    public Ingredient[] Ingredients { get; init; } = [];
    public string[] Instructions { get; init; } = [];
}

firstRunOptions.ChatOptions.ResponseFormat = ChatResponseFormat.ForJsonSchema<RecipeResponse>(
    schemaName: "RecipeResponse",
    schemaDescription: "Complete recipe definition");

// ... after deserializing the model output:
byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(
    snapshot,
    AGUIDojoServerSerializerContext.Default.RecipeResponse);

yield return new AgentResponseUpdate
{
    Contents = [new DataContent(bytes, "application/json")],
};
```

The client renders each snapshot as the **entire** current state — no diffing required.

## 10. Testing

Test an AG-UI server end-to-end with `WebApplicationFactory` and `AGUIChatClient` over the test server:

```csharp
public sealed class AGUIIntegrationTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public AGUIIntegrationTests(WebApplicationFactory<Program> factory) => _factory = factory;

    [Fact]
    public async Task Streams_text_back()
    {
        using HttpClient http = _factory.CreateClient();
        http.Timeout = TimeSpan.FromMinutes(1);

        var chatClient = new AGUIChatClient(http, "/", jsonSerializerOptions: null);
        AIAgent agent = chatClient.AsAIAgent(name: "test-client");

        AgentSession session = await agent.CreateSessionAsync();
        string text = "";
        await foreach (var u in agent.RunStreamingAsync([new(ChatRole.User, "say hi")], session))
        {
            foreach (var c in u.Contents)
            {
                if (c is TextContent t) { text += t.Text; }
            }
        }

        Assert.NotEmpty(text);
    }
}
```

For unit tests of `DelegatingAIAgent` subclasses, swap the inner agent for a fake `AIAgent` that yields canned `AgentResponseUpdate`s — no HTTP needed.
