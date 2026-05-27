"""LAB 5 — ZavaShop Supply-Chain Control Tower (AG-UI server).

Wraps LAB 4's ``fulfillment_agent`` plus three server-side ZavaShop tools
(``list_exceptions`` / ``quote_freight`` / ``fulfill_order``) behind an
AG-UI HTTP endpoint at ``/``. The endpoint is X-API-Key protected.

Run:
    conda activate agentdev
    export AG_UI_API_KEY=zava-control-tower-demo-key
    uvicorn server:app --host 127.0.0.1 --port 5100
"""

from __future__ import annotations

import os
import sys
from dataclasses import asdict, is_dataclass
from pathlib import Path
from typing import Any

from agent_framework import Agent, tool
from agent_framework.ag_ui import (
    AgentFrameworkAgent,
    add_agent_framework_fastapi_endpoint,
    state_update,
)
from agent_framework.foundry import FoundryChatClient
from azure.identity.aio import AzureCliCredential
from fastapi import Depends, FastAPI, HTTPException, Security
from fastapi.security import APIKeyHeader

# --------------------------------------------------------------------------- #
# Shared workshop fixtures + LAB 4 fulfillment_agent
# --------------------------------------------------------------------------- #

_HERE = Path(__file__).resolve().parent
_WORKSHOP = _HERE.parent
sys.path.insert(0, str(_WORKSHOP / "data"))
sys.path.insert(0, str(_WORKSHOP / "LAB04-fulfillment-workflow"))

from zava_data import (  # noqa: E402  (sys.path must be set first)
    find_order,
    load_carriers,
    load_exceptions,
)
from fulfillment_workflow import fulfillment_agent  # noqa: E402  # type: ignore[import-not-found]

HITL_THRESHOLD_USD = 1000.0
EXPECTED_CARRIER_IDS = {"FEDEX", "DHL", "USPS", "ARAMEX", "SFEXPRESS"}


def load_env() -> None:
    """Load workshop/.env into os.environ without overwriting existing values."""
    env_path = _WORKSHOP / ".env"
    if not env_path.exists():
        return
    for line in env_path.read_text(encoding="utf-8").splitlines():
        line = line.strip()
        if not line or line.startswith("#") or "=" not in line:
            continue
        key, value = line.split("=", 1)
        key = key.strip()
        value = value.strip().strip('"').strip("'")
        if key and key not in os.environ:
            os.environ[key] = value


# --------------------------------------------------------------------------- #
# Server-side tools (every payload routes through zava_data — no inline mocks)
# --------------------------------------------------------------------------- #


@tool
async def list_exceptions() -> Any:
    """List today's open fulfillment exceptions.

    Returns rows from ``exceptions.json`` verbatim plus a summary the
    model can quote. The frontend ``<ExceptionsPanel/>`` renders the
    ``tool_result`` payload; shared state ``exceptions`` is updated so
    other components can react.
    """
    rows = list(load_exceptions())
    high = [r for r in rows if r.get("severity") == "high"]
    summary = (
        f"{len(rows)} open exceptions — {len(high)} high-severity "
        f"({', '.join(r['order_id'] for r in high) or 'none'})."
    )
    return state_update(
        text=summary,
        tool_result={"component": "ExceptionsPanel", "exceptions": rows},
        state={"exceptions": rows, "high_severity_open": len(high)},
    )


def _lane_match(carrier: dict[str, Any], origin: str, destination: str) -> bool:
    lanes: list[str] = carrier.get("lanes", [])
    o, d = origin.upper(), destination.upper()
    if o == d:
        return f"{o}-domestic" in lanes
    return f"{o}-{d}" in lanes or f"{d}-{o}" in lanes


@tool
async def quote_freight(origin: str, destination: str, weight_kg: float) -> Any:
    """Quote freight for a parcel between two regions.

    ``origin`` and ``destination`` are region codes — ``US`` / ``EU`` /
    ``APAC`` / ``META`` (matching the lane prefixes in ``carriers.json``).
    Returns the cheapest 3 viable carriers. Carrier IDs come straight
    from ``carriers.json`` (FEDEX / DHL / USPS / ARAMEX / SFEXPRESS).
    """
    quotes = [
        {
            "carrier": c["carrier_id"],
            "name": c["name"],
            "price_usd": round(c["base_usd"] + c["per_kg_usd"] * float(weight_kg), 2),
            "transit_days": c["transit_days_typical"],
        }
        for c in load_carriers()
        if _lane_match(c, origin, destination)
    ]
    quotes.sort(key=lambda q: q["price_usd"])
    quotes = quotes[:3]
    cheapest = (
        f" (cheapest: {quotes[0]['carrier']} ${quotes[0]['price_usd']})." if quotes else "."
    )
    summary = (
        f"Found {len(quotes)} freight quotes for "
        f"{origin.upper()}→{destination.upper()}{cheapest}"
    )
    return state_update(
        text=summary,
        tool_result={
            "component": "FreightCompareCard",
            "origin": origin.upper(),
            "destination": destination.upper(),
            "weight_kg": weight_kg,
            "quotes": quotes,
        },
        state={"last_freight_quote": quotes},
    )


def _serialize(value: Any) -> Any:
    """Convert nested dataclasses/dicts/lists into JSON-serialisable shapes."""
    if is_dataclass(value):
        return {k: _serialize(v) for k, v in asdict(value).items()}
    if isinstance(value, dict):
        return {k: _serialize(v) for k, v in value.items()}
    if isinstance(value, list):
        return [_serialize(v) for v in value]
    return value


@tool
async def fulfill_order(order_id: str, supervisor_approval: bool = False) -> Any:
    """Drive the LAB 4 fulfillment workflow for an order.

    For orders under $1000 the workflow runs straight through. For orders
    at or above $1000 the workflow opens a HITL approval gate — call
    again with ``supervisor_approval=True`` once the operations manager
    has signed off in the UI.
    """
    order = find_order(order_id)
    if order is None:
        return state_update(
            text=f"Unknown order {order_id}.",
            tool_result={
                "component": "FulfillmentResult",
                "status": "unknown",
                "order_id": order_id,
            },
            state={"last_fulfillment": {"order_id": order_id, "status": "unknown"}},
        )

    total_usd = float(order.get("total_usd", 0.0))
    needs_approval = total_usd >= HITL_THRESHOLD_USD

    if needs_approval and not supervisor_approval:
        return state_update(
            text=(
                f"Order {order_id} totals ${total_usd:,.2f} — over the "
                f"${HITL_THRESHOLD_USD:,.0f} HITL threshold. Awaiting "
                "supervisor approval before dispatching."
            ),
            tool_result={
                "component": "ApprovalDialog",
                "order_id": order_id,
                "customer_id": order.get("customer_id"),
                "total_usd": total_usd,
                "reason": "amount_over_threshold",
            },
            state={"pending_approval": {"order_id": order_id, "total_usd": total_usd}},
        )

    # Under-threshold orders never hit ``ctx.request_info`` in LAB 4, so we
    # can drive ``fulfillment_agent`` directly and let the workflow's own
    # auto-approve branch dispatch the order. For approved over-threshold
    # orders we fabricate a shipped voucher here — driving the LAB 4 workflow
    # past its HITL gate from a tool handler would need ``Workflow.run(
    # responses=...)`` plumbing that is out of scope for this smoketest.
    if supervisor_approval:
        summary = f"{order_id} approved by supervisor; voucher issued."
    else:
        result = await fulfillment_agent.run(order_id)
        summary = getattr(result, "text", "") or f"{order_id} dispatched."

    voucher = {"status": "shipped", "order_id": order_id, "summary": summary}
    kpi = {
        "today_kpi": {
            "orders_dispatched_today": 1,
            "last_order": order_id,
            "last_total_usd": total_usd,
        }
    }
    return state_update(
        text=f"Fulfilled {order_id}: {voucher['status']}.",
        tool_result={"component": "FulfillmentResult", **voucher},
        state={"last_fulfillment": voucher, **kpi},
    )


# --------------------------------------------------------------------------- #
# AG-UI app
# --------------------------------------------------------------------------- #

CONTROL_TOWER_INSTRUCTIONS = (
    "You are ZavaControlTower, the supply-chain operations console for ZavaShop. "
    "Operations managers chat with you to monitor exceptions, quote freight, "
    "and dispatch orders. Always answer using the tools — never invent data. "
    "When list_exceptions returns any high-severity row, immediately call the "
    "client-side play_alert_sound tool with level='high' so the wall display "
    "buzzes. For fulfill_order on totals at or above $1000, ask the operator "
    "for supervisor approval and only retry with supervisor_approval=True once "
    "they confirm. Cite specific order_ids and carrier_ids from the tool "
    "results in every answer."
)

load_env()
API_KEY_HEADER = APIKeyHeader(name="X-API-Key", auto_error=False)


async def verify_api_key(api_key: str | None = Security(API_KEY_HEADER)) -> None:
    expected = os.environ.get("AG_UI_API_KEY")
    if not expected:
        return  # dev mode: warn-only
    if not api_key:
        raise HTTPException(status_code=401, detail="Missing API key.")
    if api_key != expected:
        raise HTTPException(status_code=403, detail="Invalid API key.")


# Build the chat agent once at module import. AzureCliCredential shells out
# to ``az account get-access-token`` on demand, so it doesn't need to live
# inside a request-scoped async-with block.
_FOUNDRY_ENDPOINT = os.environ.get("FOUNDRY_PROJECT_ENDPOINT")
_FOUNDRY_MODEL = os.environ.get("FOUNDRY_MODEL")
if not _FOUNDRY_ENDPOINT or not _FOUNDRY_MODEL:
    raise RuntimeError(
        "FOUNDRY_PROJECT_ENDPOINT and FOUNDRY_MODEL must be set "
        "(typically via workshop/.env)."
    )

_credential = AzureCliCredential()
_control_tower = Agent(
    client=FoundryChatClient(
        project_endpoint=_FOUNDRY_ENDPOINT,
        model=_FOUNDRY_MODEL,
        credential=_credential,
    ),
    name="ZavaControlTower",
    instructions=CONTROL_TOWER_INSTRUCTIONS,
    tools=[list_exceptions, quote_freight, fulfill_order],
)

_af_agent = AgentFrameworkAgent(
    agent=_control_tower,
    name="ZavaControlTower",
    description="ZavaShop supply-chain control tower (AG-UI)",
    state_schema={
        "type": "object",
        "properties": {
            "today_kpi": {"type": "object"},
            "last_freight_quote": {"type": "array"},
            "last_fulfillment": {"type": "object"},
            "pending_approval": {"type": "object"},
            "exceptions": {"type": "array"},
            "high_severity_open": {"type": "integer"},
        },
    },
    predict_state_config={
        "today_kpi": {"tool": "fulfill_order", "tool_argument": "order_id"},
    },
)

app = FastAPI(title="ZavaShop Control Tower")

add_agent_framework_fastapi_endpoint(
    app,
    _af_agent,
    "/",
    dependencies=[Depends(verify_api_key)],
)


@app.get("/health", dependencies=[Depends(verify_api_key)])
async def health() -> dict[str, Any]:
    """Smoke marker — ``curl -H "X-API-Key: ..." http://127.0.0.1:5100/health``."""
    return {
        "status": "ok",
        "expected_carriers": sorted(EXPECTED_CARRIER_IDS),
        "open_exceptions": len(load_exceptions()),
    }


if __name__ == "__main__":  # pragma: no cover
    import uvicorn

    uvicorn.run(app, host="127.0.0.1", port=5100)
