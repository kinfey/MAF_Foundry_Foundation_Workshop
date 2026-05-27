# LAB 5 — Control Tower Frontend (component spec)

The Python smoketest in [client_smoketest.py](../client_smoketest.py) exercises every wire-level feature, but a real ops console renders the JSON payloads as React components. This file is the spec a frontend developer (or a generative-UI agent) reads to wire each AG-UI capability into the screen.

## 7 features → 7 React components

| # | AG-UI feature              | What the server does                                                                  | Backing wire payload                                | React component        |
|---|----------------------------|---------------------------------------------------------------------------------------|-----------------------------------------------------|------------------------|
| 1 | Agentic chat               | Streams `TextMessage*` chunks over SSE on the default `/` path.                       | `RUN_STARTED` → `TEXT_MESSAGE_*` → `RUN_FINISHED`   | `<ChatStream/>`        |
| 2 | Backend tool rendering     | Server-side `@tool` (`list_exceptions`, `quote_freight`, `fulfill_order`) emits a `ToolCall*` envelope visible to the UI before the result. | `TOOL_CALL_START` / `TOOL_CALL_ARGS` / `TOOL_CALL_END` | `<ToolCallCard/>`      |
| 3 | Human-in-the-loop          | `fulfill_order` over $1000 returns an `ApprovalDialog` `tool_result` and stores `pending_approval` in shared state until the operator retries with `supervisor_approval=True`. | `tool_result.component == "ApprovalDialog"` + `state.pending_approval` | `<ApprovalDialog/>`    |
| 4 | Agentic generative UI      | The agent streams Markdown summaries (cheapest carrier, exception highlights, dispatch confirmations) inline with the conversation. | `TEXT_MESSAGE_CONTENT` deltas containing Markdown   | `<StreamingMarkdown/>` |
| 5 | Tool-based UI              | Every server tool returns `state_update(tool_result={"component": "...", ...})`, so the frontend renders a card keyed off `component`.        | `state_update.tool_result.component`               | `<DynamicComponent/>`  |
| 6 | Shared state               | `state_update(state={...})` syncs `exceptions`, `last_freight_quote`, `last_fulfillment`, `pending_approval`, and `high_severity_open` to every connected client. | `STATE_SNAPSHOT` + `STATE_DELTA` events            | `<StatePanel/>`        |
| 7 | Predictive state updates   | `AgentFrameworkAgent(..., predict_state_config={"today_kpi": {"tool": "fulfill_order", "tool_argument": "order_id"}})` ships optimistic `today_kpi` deltas before the workflow finishes. | `STATE_DELTA` driven by tool-arg streaming         | `<KpiTicker/>`         |

## Wiring rules

- **All component cards key off `tool_result.component`.** Build a `componentRegistry` keyed by string id → React component, and pass the rest of the payload as props. No client-side mocks — `<FreightCompareCard/>` reads the `quotes` array as-is from `quote_freight`, and `<ExceptionsPanel/>` iterates `exceptions` with the schema from [`exceptions.json`](../../data/exceptions.json) (`order_id`, `customer_id`, `reason`, `detail`, `severity`, `opened_at`).
- **`<ApprovalDialog/>` retries via the same chat turn**, not a side channel. When the operator clicks *Approve*, send a follow-up user message such as *"Approved — re-run fulfill_order with supervisor_approval=True"*. The agent re-invokes the tool, the workflow resumes the LAB 4 HITL gate via injected `ApprovalResponse`, and the next `state_update` clears `pending_approval`.
- **`<StatePanel/>` is read-only and reactive.** Subscribe to `STATE_SNAPSHOT` / `STATE_DELTA` events and rebuild the panel from the latest state — never store derived totals locally.
- **`<KpiTicker/>` reads only `state.today_kpi.*`.** Predictive deltas land before `fulfill_order` returns; the ticker should debounce updates (e.g. 200 ms) so the optimistic preview replaces cleanly when the final state arrives.
- **Client-side tools belong to the React shell, not the server.** `play_alert_sound` (smoketest example) must be registered on `AGUIChatClient`-derived `Agent.tools=[...]` in the browser, so the LLM can dispatch UI-only side effects (Web Audio bell, toast notifications, focus-stealing dialogs) without the server ever knowing.

## Sample registry sketch (TSX)

```tsx
const componentRegistry: Record<string, React.FC<any>> = {
  ExceptionsPanel,
  FreightCompareCard,
  ApprovalDialog,
  FulfillmentResult,
};

function DynamicComponent({ payload }: { payload: { component: string } }) {
  const Cmp = componentRegistry[payload.component];
  return Cmp ? <Cmp {...payload} /> : <pre>{JSON.stringify(payload, null, 2)}</pre>;
}
```

The server keeps frontend evolution decoupled — adding a new tool means adding a new `component` id and a matching React component in the registry; no protocol change required.
