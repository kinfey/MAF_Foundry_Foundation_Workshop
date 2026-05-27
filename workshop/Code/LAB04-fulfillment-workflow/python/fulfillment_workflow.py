"""LAB 4 — ZavaShop order fulfillment workflow.

DAG:
    intake ──┬──► stock_check ──┐
             └──► shipping_quote ┴──► allocator ──► approval ──► dispatch ──► finance

Demonstrates:
- Fan-out / fan-in: stock_check and shipping_quote run concurrently
- HITL: approval pauses with ``ctx.request_info`` when total ≥ $1000
- FileCheckpointStorage: every super-step persists state to ``./.checkpoints``
- Resume:  ``workflow.run(checkpoint_id=..., responses={req_id: ...})``
- ``workflow.as_agent("ZavaFulfillment")`` — single-agent surface for LAB 5

Run:
    conda activate agentdev
    python fulfillment_workflow.py             # both scenarios + as_agent demo
    python fulfillment_workflow.py --scenario a
    python fulfillment_workflow.py --scenario b
"""

import argparse
import asyncio
import dataclasses
import os
import sys
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any, Literal, Never

from agent_framework import (
    Executor,
    FileCheckpointStorage,
    Message,
    WorkflowBuilder,
    WorkflowContext,
    executor,
    handler,
    response_handler,
)

# ---------------------------------------------------------------------------
# Shared ZavaShop fixtures
# ---------------------------------------------------------------------------

_HERE = Path(__file__).resolve().parent
_DATA = _HERE.parent / "data"
sys.path.insert(0, str(_DATA))

from zava_data import (  # noqa: E402  (sys.path must be set first)
    find_order,
    find_stock,
    load_carriers,
    load_skus,
    load_warehouses,
)

HITL_THRESHOLD_USD = 1000.0
CHECKPOINT_DIR = _HERE / ".checkpoints"

# Heuristic: which carrier-lane region does a ship-to city live in.
_CITY_REGION: dict[str, str] = {
    "seattle": "US", "boston": "US", "new york": "US", "san francisco": "US",
    "los angeles": "US", "chicago": "US",
    "london": "EU", "berlin": "EU", "paris": "EU", "madrid": "EU",
    "shanghai": "APAC", "tokyo": "APAC", "singapore": "APAC", "beijing": "APAC",
    "dubai": "META", "riyadh": "META",
    "são paulo": "LATAM", "sao paulo": "LATAM",
}

# Warehouse region → lane prefix used in carriers.json.
_WAREHOUSE_PREFIX: dict[str, str] = {
    "US-West": "US",
    "EU": "EU",
    "APAC": "APAC",
    "META": "META",
    "LATAM": "LATAM",
}


def _load_dotenv() -> None:
    """Lightweight .env loader (no python-dotenv dependency)."""
    for candidate in (_HERE / ".env", _DATA.parent / ".env"):
        if not candidate.exists():
            continue
        for line in candidate.read_text(encoding="utf-8").splitlines():
            line = line.strip()
            if not line or line.startswith("#") or "=" not in line:
                continue
            key, _, value = line.partition("=")
            key = key.strip()
            value = value.strip().strip('"').strip("'")
            if key and key not in os.environ:
                os.environ[key] = value
        return


def _lookup_warehouse(code: str) -> dict[str, Any]:
    for w in load_warehouses():
        if w["code"] == code:
            return w
    raise ValueError(f"warehouse {code} not found in warehouses.json")


def _lookup_sku(sku_id: str) -> dict[str, Any]:
    for s in load_skus():
        if s["sku"] == sku_id:
            return s
    raise ValueError(f"sku {sku_id} not found in skus.json")


def _resolve_lane(ship_to_warehouse: str, ship_to_city: str) -> str:
    """Map (warehouse, destination city) → a carriers.json lane string."""
    src_region = _lookup_warehouse(ship_to_warehouse)["region"]
    src = _WAREHOUSE_PREFIX.get(src_region, src_region)
    dst = _CITY_REGION.get(ship_to_city.lower(), src)
    return f"{src}-domestic" if src == dst else f"{src}-{dst}"


# ---------------------------------------------------------------------------
# Typed messages flowing between executors
# ---------------------------------------------------------------------------


@dataclass
class OrderRecord:
    order_id: str
    customer_id: str
    lines: list[dict[str, Any]]
    ship_to_city: str
    ship_to_warehouse: str
    total_usd: float


@dataclass
class StockLine:
    sku: str
    qty: int
    on_hand: int
    available: int
    sufficient: bool


@dataclass
class StockReport:
    order_id: str
    warehouse: str
    lines: list[StockLine]
    all_in_stock: bool


@dataclass
class CarrierQuote:
    carrier_id: str
    name: str
    price_usd: float
    transit_days: int


@dataclass
class FreightQuote:
    order_id: str
    lane: str
    total_kg: float
    quotes: list[CarrierQuote]
    cheapest: CarrierQuote


@dataclass
class LegResult:
    """Union message produced by stock_check / shipping_quote → allocator."""

    kind: Literal["stock", "freight"]
    order: OrderRecord
    stock: StockReport | None = None
    freight: FreightQuote | None = None


@dataclass
class AllocationPlan:
    order: OrderRecord
    stock: StockReport
    freight: FreightQuote
    total_usd: float


@dataclass
class ApprovalRequest:
    order_id: str
    customer_id: str
    total_usd: float
    reason: str


@dataclass
class ApprovalResponse:
    approved: bool
    reason: str = ""


@dataclass
class DispatchResult:
    order_id: str
    warehouse: str
    supervisor: str
    carrier: str
    eta_days: int


# ---------------------------------------------------------------------------
# Executors (one responsibility each)
# ---------------------------------------------------------------------------


def _coerce_order_id(value: object) -> str:
    """Accept a raw string, a single Message, or a list[Message] and return the order id."""
    if isinstance(value, str):
        return value.strip()
    if isinstance(value, Message):
        return (value.text or "").strip()
    if isinstance(value, list) and value and isinstance(value[-1], Message):
        return (value[-1].text or "").strip()
    raise ValueError(f"Unsupported intake input: {value!r}")


class IntakeExecutor(Executor):
    """Start node — resolves an order id via ``find_order``.

    Two ``@handler`` methods give the workflow both interfaces it needs:
    - ``str`` for direct ``workflow.run("ORD-...")`` calls
    - ``list[Message]`` so ``workflow.as_agent("ZavaFulfillment")`` can dispatch user prompts
    """

    def __init__(self, id: str = "intake") -> None:
        super().__init__(id=id)

    async def _emit(self, order_id: str, ctx: WorkflowContext[OrderRecord]) -> None:
        raw = find_order(order_id)
        if raw is None:
            raise ValueError(f"Unknown order: {order_id}")
        order = OrderRecord(
            order_id=raw["order_id"],
            customer_id=raw["customer_id"],
            lines=list(raw["lines"]),
            ship_to_city=raw["ship_to_city"],
            ship_to_warehouse=raw["ship_to_warehouse"],
            total_usd=float(raw["total_usd"]),
        )
        print(
            f"[intake] {order.order_id} → {order.ship_to_warehouse} "
            f"({order.ship_to_city}) goods=${order.total_usd}"
        )
        await ctx.send_message(order)

    @handler
    async def from_string(self, order_id: str, ctx: WorkflowContext[OrderRecord]) -> None:
        await self._emit(_coerce_order_id(order_id), ctx)

    @handler
    async def from_messages(
        self,
        messages: list[Message],
        ctx: WorkflowContext[OrderRecord],
    ) -> None:
        await self._emit(_coerce_order_id(messages), ctx)


@executor(id="stock_check")
async def stock_check(order: OrderRecord, ctx: WorkflowContext[LegResult]) -> None:
    lines: list[StockLine] = []
    for ln in order.lines:
        row = find_stock(ln["sku"], order.ship_to_warehouse)
        on_hand = int(row["on_hand"]) if row else 0
        reserved = int(row["reserved"]) if row else 0
        available = max(on_hand - reserved, 0)
        lines.append(
            StockLine(
                sku=ln["sku"],
                qty=int(ln["qty"]),
                on_hand=on_hand,
                available=available,
                sufficient=available >= int(ln["qty"]),
            )
        )
    report = StockReport(
        order_id=order.order_id,
        warehouse=order.ship_to_warehouse,
        lines=lines,
        all_in_stock=all(l.sufficient for l in lines),
    )
    summary = ", ".join(f"{l.sku}:{l.available}/{l.qty}" for l in lines)
    print(f"[stock_check] {order.order_id} all_in_stock={report.all_in_stock} [{summary}]")
    await ctx.send_message(LegResult(kind="stock", order=order, stock=report))


@executor(id="shipping_quote")
async def shipping_quote(order: OrderRecord, ctx: WorkflowContext[LegResult]) -> None:
    lane = _resolve_lane(order.ship_to_warehouse, order.ship_to_city)
    total_kg = 0.0
    for ln in order.lines:
        sku = _lookup_sku(ln["sku"])
        total_kg += float(sku["weight_kg"]) * int(ln["qty"])
    quotes: list[CarrierQuote] = []
    for c in load_carriers():
        if lane in c["lanes"]:
            price = round(c["base_usd"] + c["per_kg_usd"] * total_kg, 2)
            quotes.append(
                CarrierQuote(
                    carrier_id=c["carrier_id"],
                    name=c["name"],
                    price_usd=price,
                    transit_days=int(c["transit_days_typical"]),
                )
            )
    if not quotes:
        raise RuntimeError(f"No carrier covers lane {lane!r} for {order.order_id}")
    quotes.sort(key=lambda q: q.price_usd)
    quotes = quotes[:3]
    cheapest = quotes[0]
    fq = FreightQuote(
        order_id=order.order_id,
        lane=lane,
        total_kg=round(total_kg, 2),
        quotes=quotes,
        cheapest=cheapest,
    )
    print(
        f"[shipping_quote] {order.order_id} lane={lane} kg={fq.total_kg} "
        f"cheapest={cheapest.carrier_id}@${cheapest.price_usd}"
    )
    await ctx.send_message(LegResult(kind="freight", order=order, freight=fq))


@executor(id="allocator")
async def allocator(
    legs: list[LegResult],
    ctx: WorkflowContext[AllocationPlan],
) -> None:
    stock_leg = next(l for l in legs if l.kind == "stock")
    freight_leg = next(l for l in legs if l.kind == "freight")
    assert stock_leg.stock is not None and freight_leg.freight is not None
    order = stock_leg.order
    plan = AllocationPlan(
        order=order,
        stock=stock_leg.stock,
        freight=freight_leg.freight,
        total_usd=round(order.total_usd + freight_leg.freight.cheapest.price_usd, 2),
    )
    print(
        f"[allocator] {order.order_id} plan_total=${plan.total_usd} "
        f"(goods=${order.total_usd} + freight=${freight_leg.freight.cheapest.price_usd})"
    )
    await ctx.send_message(plan)


def _plan_from_dict(d: dict[str, Any]) -> AllocationPlan:
    order = OrderRecord(**d["order"])
    stock = StockReport(
        order_id=d["stock"]["order_id"],
        warehouse=d["stock"]["warehouse"],
        lines=[StockLine(**l) for l in d["stock"]["lines"]],
        all_in_stock=d["stock"]["all_in_stock"],
    )
    freight = FreightQuote(
        order_id=d["freight"]["order_id"],
        lane=d["freight"]["lane"],
        total_kg=d["freight"]["total_kg"],
        quotes=[CarrierQuote(**q) for q in d["freight"]["quotes"]],
        cheapest=CarrierQuote(**d["freight"]["cheapest"]),
    )
    return AllocationPlan(order=order, stock=stock, freight=freight, total_usd=d["total_usd"])


class ApprovalExecutor(Executor):
    """Conditional HITL gate: pauses the workflow when total ≥ HITL_THRESHOLD_USD."""

    def __init__(self, id: str = "approval") -> None:
        super().__init__(id=id)
        self._pending: dict[str, Any] | None = None  # serialized AllocationPlan

    @handler
    async def gate(
        self,
        plan: AllocationPlan,
        ctx: WorkflowContext[AllocationPlan, dict[str, Any]],
    ) -> None:
        if plan.total_usd < HITL_THRESHOLD_USD:
            print(f"[approval] {plan.order.order_id} auto-approved (${plan.total_usd} < ${HITL_THRESHOLD_USD})")
            await ctx.send_message(plan)
            return

        reason = "stock_shortage" if not plan.stock.all_in_stock else "amount_over_threshold"
        self._pending = dataclasses.asdict(plan)
        print(
            f"[approval] {plan.order.order_id} requesting supervisor approval "
            f"(${plan.total_usd}, reason={reason}) — workflow PAUSES here"
        )
        await ctx.request_info(
            request_data=ApprovalRequest(
                order_id=plan.order.order_id,
                customer_id=plan.order.customer_id,
                total_usd=plan.total_usd,
                reason=reason,
            ),
            response_type=ApprovalResponse,
        )

    @response_handler
    async def resume(
        self,
        original: ApprovalRequest,
        reply: ApprovalResponse,
        ctx: WorkflowContext[AllocationPlan, dict[str, Any]],
    ) -> None:
        if self._pending is None:
            raise RuntimeError("approval resume called but no pending plan was saved")
        plan = _plan_from_dict(self._pending)
        self._pending = None

        if not reply.approved:
            print(f"[approval] {plan.order.order_id} REJECTED: {reply.reason or '(no reason)'}")
            await ctx.yield_output(
                {
                    "status": "rejected",
                    "order_id": plan.order.order_id,
                    "reason": reply.reason or "supervisor declined",
                }
            )
            return

        print(f"[approval] {plan.order.order_id} APPROVED: {reply.reason or '(no reason)'}")
        await ctx.send_message(plan)

    async def on_checkpoint_save(self) -> dict[str, Any]:
        return {"pending": self._pending}

    async def on_checkpoint_restore(self, state: dict[str, Any]) -> None:
        self._pending = state.get("pending")


@executor(id="dispatch")
async def dispatch(plan: AllocationPlan, ctx: WorkflowContext[DispatchResult]) -> None:
    wh = _lookup_warehouse(plan.order.ship_to_warehouse)
    result = DispatchResult(
        order_id=plan.order.order_id,
        warehouse=wh["code"],
        supervisor=wh["supervisor"],
        carrier=plan.freight.cheapest.carrier_id,
        eta_days=plan.freight.cheapest.transit_days,
    )
    print(
        f"[dispatch] {result.order_id} → {result.warehouse} "
        f"(supervisor {result.supervisor}) via {result.carrier}, ETA {result.eta_days}d"
    )
    await ctx.send_message(result)


@executor(id="finance")
async def finance(
    result: DispatchResult,
    ctx: WorkflowContext[Never, dict[str, Any]],
) -> None:
    voucher = {
        "status": "shipped",
        "order_id": result.order_id,
        "warehouse": result.warehouse,
        "supervisor": result.supervisor,
        "carrier": result.carrier,
        "eta_days": result.eta_days,
    }
    print(f"[finance] {result.order_id} voucher issued — shipped via {result.carrier}")
    await ctx.yield_output(voucher)


# ---------------------------------------------------------------------------
# Workflow assembly + exported as_agent surface
# ---------------------------------------------------------------------------


def build_workflow() -> tuple[Any, FileCheckpointStorage]:
    CHECKPOINT_DIR.mkdir(parents=True, exist_ok=True)

    # Checkpoint serializer is type-allowlisted; register our dataclasses so a
    # paused workflow can be re-loaded from disk (and also from LAB 5 once it
    # imports this module under the name 'fulfillment_workflow').
    user_types = (
        "OrderRecord", "StockLine", "StockReport", "CarrierQuote",
        "FreightQuote", "LegResult", "AllocationPlan", "ApprovalRequest",
        "ApprovalResponse", "DispatchResult",
    )
    allowed_modules = {__name__, "__main__", "fulfillment_workflow"}
    allowed_checkpoint_types = sorted({f"{m}:{t}" for m in allowed_modules for t in user_types})

    storage = FileCheckpointStorage(
        storage_path=str(CHECKPOINT_DIR),
        allowed_checkpoint_types=allowed_checkpoint_types,
    )
    intake = IntakeExecutor(id="intake")
    approval = ApprovalExecutor(id="approval")
    wf = (
        WorkflowBuilder(
            start_executor=intake,
            checkpoint_storage=storage,
            name="ZavaFulfillment",
            output_from=[finance, approval],
        )
        .add_fan_out_edges(intake, [stock_check, shipping_quote])
        .add_fan_in_edges([stock_check, shipping_quote], allocator)
        .add_edge(allocator, approval)
        .add_edge(approval, dispatch)
        .add_edge(dispatch, finance)
        .build()
    )
    return wf, storage


# Module-level singletons so LAB 5 can do:
#     from fulfillment_workflow import fulfillment_agent
workflow, checkpoint_storage = build_workflow()
fulfillment_agent = workflow.as_agent("ZavaFulfillment")


# ---------------------------------------------------------------------------
# Demo driver
# ---------------------------------------------------------------------------


async def _stream_events(
    wf: Any,
    *,
    title: str,
    message: Any | None = None,
    checkpoint_id: str | None = None,
    responses: dict[str, Any] | None = None,
    stop_on_request_info: bool = False,
) -> tuple[set[str], dict[str, ApprovalRequest], list[int], list[dict[str, Any]]]:
    """Drive a streaming workflow run and return summary collections."""
    print("\n" + "─" * 72)
    print(f"  {title}")
    print("─" * 72)

    invoked: set[str] = set()
    pending: dict[str, ApprovalRequest] = {}
    supersteps: list[int] = []
    outputs: list[dict[str, Any]] = []
    invoke_order: list[str] = []

    kwargs: dict[str, Any] = {"stream": True, "include_status_events": False}
    if message is not None:
        kwargs["message"] = message
    if checkpoint_id is not None:
        kwargs["checkpoint_id"] = checkpoint_id
    if responses is not None:
        kwargs["responses"] = responses

    async for event in wf.run(**kwargs):
        etype = getattr(event, "type", None)
        # event.source_executor_id raises unless type=="request_info"; stick to executor_id.
        eid = getattr(event, "executor_id", None)

        if etype == "executor_invoked" and eid:
            invoked.add(eid)
            invoke_order.append(eid)
            print(f"  ► invoked    {eid}")
        elif etype == "executor_completed" and eid:
            print(f"  ✓ completed  {eid}")
        elif etype == "superstep_completed":
            it = getattr(event, "iteration", None)
            if it is not None:
                supersteps.append(it)
            print(f"  ◆ superstep  #{it}")
        elif etype == "request_info":
            req_id = getattr(event, "request_id", None)
            data = getattr(event, "data", None)
            if req_id is not None:
                pending[req_id] = data  # type: ignore[assignment]
            print(f"  ⏸  request_info req_id={req_id}")
            print(f"       data = {data}")
            # When ``stop_on_request_info`` is True we let the stream drain so
            # the superstep-completed checkpoint that carries this pending
            # request is flushed to disk; the iteration ends naturally because
            # the workflow has nothing else to do until responses arrive.
        elif etype == "output":
            data = getattr(event, "data", None)
            if isinstance(data, dict):
                outputs.append(data)
            print(f"  ★ output     {data}")
        elif etype == "error":
            print(f"  ✗ error      {getattr(event, 'data', None)}")

    # Concurrency assertion: stock_check + shipping_quote in same superstep
    if "stock_check" in invoke_order and "shipping_quote" in invoke_order:
        idx_s = invoke_order.index("stock_check")
        idx_q = invoke_order.index("shipping_quote")
        # Both should be invoked back-to-back inside the same superstep (no allocator between).
        between = invoke_order[min(idx_s, idx_q) + 1 : max(idx_s, idx_q)]
        assert all(x in {"stock_check", "shipping_quote"} for x in between), (
            f"stock_check and shipping_quote were not concurrent — saw {invoke_order!r}"
        )

    return invoked, pending, supersteps, outputs


async def run_scenario_a(wf: Any) -> None:
    invoked, pending, supersteps, outputs = await _stream_events(
        wf,
        title="Scenario A — ORD-20260524-001 ($196.50, under $1000 → no HITL)",
        message="ORD-20260524-001",
    )
    assert not pending, "Scenario A must NOT raise request_info"
    assert outputs and outputs[-1].get("status") == "shipped", "Scenario A must ship"
    assert {"stock_check", "shipping_quote"}.issubset(invoked), "Both legs must execute"
    print(f"\n[A] supersteps written: {supersteps}")
    print(f"[A] final voucher    : {outputs[-1]}")


async def run_scenario_b_with_resume(wf: Any, storage: FileCheckpointStorage) -> None:
    # Phase 1: run until HITL fires, then simulate Ctrl+C
    _, pending, _, _ = await _stream_events(
        wf,
        title="Scenario B — ORD-20260524-002 ($1500.00, over $1000 → HITL)",
        message="ORD-20260524-002",
        stop_on_request_info=True,
    )
    assert pending, "Scenario B must pause at approval with request_info"

    # Phase 2: locate the most recent checkpoint and resume from it.
    # A workflow that paused mid-run is still flagged "running" on the original
    # instance, so we build a fresh Workflow (same definition, same storage)
    # to represent a brand-new process attaching to the paused state.
    latest = await storage.get_latest(workflow_name=wf.name)
    assert latest is not None, "expected at least one checkpoint to be written"
    ckpt_ids = await storage.list_checkpoint_ids(workflow_name=wf.name)
    print(f"\n[B] checkpoint files on disk: {len(ckpt_ids)}")
    print(f"[B] resuming from checkpoint_id={latest.checkpoint_id}")

    fresh_wf, _ = build_workflow()
    responses = {
        req_id: ApprovalResponse(approved=True, reason="supervisor: ship it (demo)")
        for req_id in pending
    }
    _, _, _, outputs = await _stream_events(
        fresh_wf,
        title="Scenario B — resume from checkpoint with supervisor approval",
        checkpoint_id=latest.checkpoint_id,
        responses=responses,
    )
    assert outputs and outputs[-1].get("status") == "shipped", "Scenario B must ship after approval"
    print(f"\n[B] final voucher    : {outputs[-1]}")


async def run_as_agent_demo(agent: Any) -> None:
    print("\n" + "─" * 72)
    print("  workflow.as_agent('ZavaFulfillment') — single-agent surface")
    print("─" * 72)
    response = await agent.run("ORD-20260524-001")
    text = getattr(response, "text", str(response))
    print(f"  agent reply (truncated): {text[:300]}")


async def main() -> None:
    parser = argparse.ArgumentParser(description="ZavaShop fulfillment workflow (LAB 4)")
    parser.add_argument(
        "--scenario",
        choices=["a", "b", "both", "agent"],
        default="both",
        help="a=under threshold; b=HITL+resume; agent=as_agent demo; both=a+b",
    )
    args = parser.parse_args()

    _load_dotenv()
    print(f"[init] checkpoint dir: {CHECKPOINT_DIR}")
    print(f"[init] workflow name : {workflow.name}")

    if args.scenario in {"a", "both"}:
        await run_scenario_a(workflow)
    if args.scenario in {"b", "both"}:
        await run_scenario_b_with_resume(workflow, checkpoint_storage)
    if args.scenario in {"agent", "both"}:
        await run_as_agent_demo(fulfillment_agent)

    print("\n[OK] LAB 4 acceptance criteria covered.")


if __name__ == "__main__":
    asyncio.run(main())
