# AG-UI Client Reference (.NET)

How to connect to an AG-UI endpoint with `Microsoft.Agents.AI.AGUI` and consume the stream as `AgentResponseUpdate`s through the normal `AIAgent` API.

## Connecting

```csharp
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.AGUI;
using Microsoft.Extensions.AI;

string url = Environment.GetEnvironmentVariable("AGUI_SERVER_URL") ?? "http://localhost:5100";

using HttpClient httpClient = new() { Timeout = TimeSpan.FromSeconds(60) };

var chatClient = new AGUIChatClient(
    httpClient,
    url,
    jsonSerializerOptions: AGUIClientSerializerContext.Default.Options);

AIAgent agent = chatClient.AsAIAgent(
    name: "agui-client",
    description: "AG-UI Client Agent",
    tools: [ /* frontend tools */ ]);
```

### Constructor parameters

| Parameter | Notes |
|-----------|-------|
| `HttpClient` | You own the lifetime. AG-UI streams via SSE, so `Timeout` must be long enough for the longest run. Don't reuse a request-scoped `HttpClient` here. |
| `endpoint` (string or `Uri`) | Full URL of the `MapAGUI(...)` route. |
| `jsonSerializerOptions` | Optional. Required when frontend tools take/return custom types (AOT/trim safety). |

## Sessions

A session represents an ongoing conversation against the same agent endpoint. The server uses it as the `threadId`.

```csharp
AgentSession session = await agent.CreateSessionAsync(cancellationToken);

// Reuse `session` across multiple Run/RunStreaming calls to continue the conversation.
```

`CreateSessionAsync` is cheap; create one per user / chat tab and keep it alive for the duration of the conversation.

## Frontend Tools

Tools defined on the client are exposed to the server's model. The model can decide to call them — the SDK executes the .NET delegate **locally** on the client and ships the result back automatically.

```csharp
using System.ComponentModel;

var changeBackground = AIFunctionFactory.Create(
    () =>
    {
        Console.ForegroundColor = ConsoleColor.DarkBlue;
        Console.WriteLine("Changing color to blue");
    },
    name: "change_background_color",
    description: "Change the console background color to dark blue.");

var readClimateSensors = AIFunctionFactory.Create(
    ([Description("The sensors measurements to include in the response")] SensorRequest request) =>
        new SensorResponse { Temperature = 22.5, Humidity = 45.0, AirQualityIndex = 75 },
    name: "read_client_climate_sensors",
    description: "Reads the climate sensor data from the client device.",
    serializerOptions: AGUIClientSerializerContext.Default.Options);

AIAgent agent = chatClient.AsAIAgent(
    name: "agui-client",
    description: "AG-UI Client Agent",
    tools: [changeBackground, readClimateSensors]);
```

Custom request/response types must be registered in a `JsonSerializerContext`:

```csharp
[JsonSerializable(typeof(SensorRequest))]
[JsonSerializable(typeof(SensorResponse))]
internal sealed partial class AGUIClientSerializerContext : JsonSerializerContext;
```

## Streaming a Run

The streaming API is the same as any other `AIAgent`:

```csharp
List<ChatMessage> messages =
[
    new(ChatRole.System, "You are a helpful assistant."),
    new(ChatRole.User, "What time is it?"),
];

await foreach (AgentResponseUpdate update in agent.RunStreamingAsync(messages, session, cancellationToken: ct))
{
    foreach (AIContent content in update.Contents)
    {
        switch (content)
        {
            case TextContent text:
                Console.Write(text.Text);
                break;
            case FunctionCallContent call:
                Console.WriteLine($"[Function Call] {call.Name}");
                break;
            case FunctionResultContent result:
                if (result.Exception is not null) { Console.WriteLine($"[Error] {result.Exception}"); }
                else { Console.WriteLine($"[Result] {result.Result}"); }
                break;
            case DataContent data when data.MediaType == "application/json":
                Console.WriteLine($"[State Snapshot] {data.Data.Length} bytes");
                break;
            case ErrorContent err:
                string code = err.AdditionalProperties?["Code"] as string ?? "Unknown";
                Console.WriteLine($"[Error {code}] {err.Message}");
                break;
        }
    }
}
```

### Run lifecycle

The first `AgentResponseUpdate` carries the run-started signal — inspect `ConversationId` / `ResponseId`:

```csharp
bool isFirst = true;
string? threadId = null, runId = null;

await foreach (AgentResponseUpdate update in agent.RunStreamingAsync(messages, session))
{
    ChatResponseUpdate chatUpdate = update.AsChatResponseUpdate();
    threadId ??= chatUpdate.ConversationId;
    runId      = update.ResponseId;

    if (isFirst && threadId is not null && runId is not null)
    {
        Console.WriteLine($"[Run Started] Thread: {threadId}, Run: {runId}");
        isFirst = false;
    }

    foreach (var c in update.Contents)
    {
        if (c is TextContent t) { Console.Write(t.Text); }
    }
}

Console.WriteLine($"\n[Run Finished] Thread: {threadId}, Run: {runId}");
```

- `ConversationId` is exposed via `update.AsChatResponseUpdate()` (it lives on `ChatResponseUpdate`, not on the wrapping `AgentResponseUpdate`).
- `ResponseId` is exposed directly on `AgentResponseUpdate` and serves as the AG-UI `runId`.

## Multi-Turn Conversations

Reuse the same `AgentSession`. Don't replay history — the session carries it server-side:

```csharp
AgentSession session = await agent.CreateSessionAsync();
List<ChatMessage> turn = [new(ChatRole.System, "You are a helpful assistant.")];

while (true)
{
    Console.Write("\nUser (:q to exit): ");
    string? line = Console.ReadLine();
    if (string.IsNullOrWhiteSpace(line) || line is ":q" or "quit") { break; }

    turn.Add(new(ChatRole.User, line));

    await foreach (AgentResponseUpdate u in agent.RunStreamingAsync(turn, session))
    {
        foreach (var c in u.Contents)
        {
            if (c is TextContent t) { Console.Write(t.Text); }
        }
    }
    turn.Clear();   // session carries history; only send the new turn
}
```

## Non-Streaming Calls

If you don't need incremental rendering, `RunAsync` returns the consolidated response:

```csharp
AgentResponse response = await agent.RunAsync(messages, session, cancellationToken: ct);
Console.WriteLine(response.Text);

foreach (var message in response.Messages)
{
    foreach (var content in message.Contents)
    {
        // inspect FunctionCallContent / FunctionResultContent / DataContent / ErrorContent
    }
}
```

Under the hood this is still SSE — the SDK aggregates the stream for you.

## Cancellation

`HttpClient.Timeout` is the upper bound on the entire request. For mid-run cancellation pass a `CancellationToken`:

```csharp
using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));

await foreach (var u in agent.RunStreamingAsync(messages, session, cancellationToken: cts.Token))
{
    // ...
    if (userWantsToStop) { cts.Cancel(); }
}
```

The SDK closes the SSE connection cleanly; the server agent observes the cancellation via its own `CancellationToken`.

## Error Handling

AG-UI errors arrive as `ErrorContent` inside the stream — they don't throw. The exception path is reserved for transport failures (network, deserialization).

```csharp
try
{
    await foreach (var u in agent.RunStreamingAsync(messages, session, cancellationToken: ct))
    {
        foreach (var c in u.Contents)
        {
            if (c is ErrorContent err)
            {
                string? code = err.AdditionalProperties?["Code"] as string;
                Console.Error.WriteLine($"[{code ?? "Unknown"}] {err.Message}");
            }
        }
    }
}
catch (HttpRequestException ex)
{
    Console.Error.WriteLine($"Transport failure: {ex.Message}");
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("Cancelled.");
}
```

Wrap each frontend tool body in try/catch and return a structured error — exceptions thrown inside an `AIFunctionFactory.Create` delegate surface to the model as opaque strings.
