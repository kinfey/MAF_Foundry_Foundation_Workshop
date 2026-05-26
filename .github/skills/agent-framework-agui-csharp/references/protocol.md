# AG-UI Protocol Reference

A field guide for the wire format of [`Microsoft.Agents.AI.AGUI`](https://www.nuget.org/packages/Microsoft.Agents.AI.AGUI) / `Microsoft.Agents.AI.Hosting.AGUI.AspNetCore`. You usually don't need to know this — `AGUIChatClient` and `MapAGUI` hide it — but it helps when:

- writing a non-.NET client (web, mobile, CLI),
- debugging with `curl` or a REST Client,
- adding middleware (auth, logging, rate limiting) around the endpoint.

## Transport

- **Method:** `POST`
- **Content-Type (request):** `application/json`
- **Response body:** `text/event-stream` (Server-Sent Events)
- **Path:** whatever you passed to `MapAGUI(path, ...)`.

## Request Shape

The client sends a JSON object with four fields:

```json
{
  "threadId": "thread_123",
  "runId": "run_456",
  "messages": [
    { "role": "user", "content": "What is the capital of France?" }
  ],
  "context": {}
}
```

| Field | Type | Notes |
|-------|------|-------|
| `threadId` | string | Conversation/session identifier. The server looks this up in its session store. |
| `runId` | string | Unique per run; surfaces in `AgentResponseUpdate.ResponseId` on the client. |
| `messages` | array | Chat messages for **this turn only**. Prior turns are recovered from the session. |
| `context` | object | Free-form metadata; on the .NET server this becomes `ChatOptions.AdditionalProperties` (e.g. `ag_ui_state`). |

A `curl` equivalent:

```bash
curl -N -X POST http://localhost:5100/ \
    -H "Content-Type: application/json" \
    -d '{
      "threadId": "thread_123",
      "runId":    "run_456",
      "messages": [{"role":"user","content":"hi"}],
      "context":  {}
    }'
```

`.http` form for the VS Code REST Client extension:

```http
@host = http://localhost:5100

### Send a message to the AG-UI agent
POST {{host}}/
Content-Type: application/json

{
  "threadId": "thread_123",
  "runId": "run_456",
  "messages": [
    { "role": "user", "content": "What is the capital of France?" }
  ],
  "context": {}
}
```

## Response Stream

The response is a chunked SSE stream. Each event is an AG-UI envelope; the .NET SDK rehydrates them into `AgentResponseUpdate.Contents` items. The cheat sheet:

| AG-UI event family | Surfaces in `.Contents` as | When it appears |
|--------------------|----------------------------|----------------|
| Run started / metadata | First update with `ConversationId` + `ResponseId` populated | Once per run, at the beginning. |
| Text deltas | `TextContent` | While the model is streaming a response. |
| Tool call (frontend) | `FunctionCallContent` (`Name`, `Arguments`) | When the server-side model decides to call a client tool. |
| Tool result | `FunctionResultContent` (`Result` or `Exception`) | After a tool finishes (frontend or backend). |
| State snapshot | `DataContent` with `MediaType = "application/json"` | When the server emits shared-state / predictive-state events. |
| Error | `ErrorContent` (`Message`, `AdditionalProperties["Code"]`) | Server-side error during the run. |
| Run finished | Last `AgentResponseUpdate` in the stream | Once per run. |

A canonical traversal:

```csharp
await foreach (AgentResponseUpdate update in agent.RunStreamingAsync(messages, session))
{
    ChatResponseUpdate chat = update.AsChatResponseUpdate();
    // chat.ConversationId == threadId
    // update.ResponseId    == runId

    foreach (AIContent c in update.Contents)
    {
        switch (c)
        {
            case TextContent t:            /* delta */                  break;
            case FunctionCallContent fc:   /* client must execute */    break;
            case FunctionResultContent fr: /* completed */              break;
            case DataContent d when d.MediaType == "application/json":
                                           /* state snapshot */         break;
            case ErrorContent err:         /* show error */              break;
        }
    }
}
```

## Threads, Runs, Sessions

Three closely-related ids — keep them straight:

| Term | Lives on | Identifier surface |
|------|----------|---------------------|
| **Thread** | Server session store. Persistent across runs. | `threadId` in request; `ConversationId` on `ChatResponseUpdate`. |
| **Run** | Single `RunAsync` / `RunStreamingAsync` invocation. | `runId` in request; `ResponseId` on `AgentResponseUpdate`. |
| **Session** | Client handle around a thread. | `AgentSession` returned by `agent.CreateSessionAsync()`. |

Same `AgentSession` → same `threadId` → server reuses conversation history. New `RunStreamingAsync` call → new `runId`.

## State Snapshots (`DataContent`)

When the server emits a `DataContent("application/json")`, the payload is a JSON document representing whatever structured state the agent author chose. For example, the shared-state pattern emits a complete recipe object:

```json
{
  "title": "Pasta Primavera",
  "skillLevel": "Intermediate",
  "cookingTime": "30m",
  "preferences": ["Vegetarian"],
  "ingredients": [{ "name": "pasta", "amount": "200g" }],
  "instructions": ["Boil water", "Cook pasta"]
}
```

The predictive-state pattern emits multiple incremental snapshots of the same conceptual document:

```json
{ "document": "Once upon" }
{ "document": "Once upon a time" }
{ "document": "Once upon a time, in a" }
```

The client renders each snapshot directly into the UI. There is no diff/patch — every snapshot is the complete current state.

## Error Envelope

```csharp
case ErrorContent err:
    string? code   = err.AdditionalProperties?["Code"] as string;
    string  reason = err.Message;
    // Map `code` to a UI surface; show `reason` to the user (after sanitizing).
    break;
```

Errors are part of the stream — they don't throw on the client. The stream then ends normally.

Transport-level failures (HTTP 5xx, broken connection, malformed SSE) bubble out of `RunStreamingAsync` as `HttpRequestException` / `IOException`.

## Mapping AG-UI ↔ .NET Types Cheat Sheet

| AG-UI concept | .NET type |
|--------------|-----------|
| `threadId` | `ChatResponseUpdate.ConversationId` |
| `runId` | `AgentResponseUpdate.ResponseId` |
| `messages[].role`/`content` | `ChatMessage` (`ChatRole`, `TextContent`) |
| `context` | `ChatClientAgentRunOptions.ChatOptions.AdditionalProperties` |
| text delta | `TextContent` |
| tool call | `FunctionCallContent` (`Name`, `Arguments` dictionary) |
| tool result | `FunctionResultContent` (`Result` or `Exception`) |
| state snapshot | `DataContent` (`MediaType = "application/json"`, `Data` = `byte[]`) |
| error | `ErrorContent` (`Message`, `AdditionalProperties["Code"]`) |
