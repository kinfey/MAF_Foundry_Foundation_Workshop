"""
Shared ZavaShop data loader used by every LAB.

All LAB scripts import from this module so they share the same fixture set
under `workshop/data/`. Each loader returns plain Python lists / dicts —
no SDK types — so the data is reusable from function tools, evaluation
inputs, AG-UI server payloads, and workflow executors alike.
"""

from __future__ import annotations

import json
from functools import lru_cache
from pathlib import Path
from typing import Any

DATA_DIR = Path(__file__).parent


def _read_json(name: str) -> Any:
    return json.loads((DATA_DIR / name).read_text(encoding="utf-8"))


def _read_jsonl(name: str) -> list[dict[str, Any]]:
    path = DATA_DIR / name
    return [json.loads(line) for line in path.read_text(encoding="utf-8").splitlines() if line.strip()]


@lru_cache(maxsize=1)
def load_warehouses() -> list[dict[str, Any]]:
    return _read_json("warehouses.json")


@lru_cache(maxsize=1)
def load_skus() -> list[dict[str, Any]]:
    return _read_json("skus.json")


@lru_cache(maxsize=1)
def load_inventory() -> list[dict[str, Any]]:
    return _read_json("inventory.json")


@lru_cache(maxsize=1)
def load_purchase_orders() -> list[dict[str, Any]]:
    return _read_json("purchase_orders.json")


@lru_cache(maxsize=1)
def load_suppliers() -> list[dict[str, Any]]:
    return _read_json("suppliers.json")


@lru_cache(maxsize=1)
def load_contracts() -> list[dict[str, Any]]:
    return _read_json("contracts.json")


@lru_cache(maxsize=1)
def load_customers() -> list[dict[str, Any]]:
    return _read_json("customers.json")


@lru_cache(maxsize=1)
def load_orders() -> list[dict[str, Any]]:
    return _read_json("orders.json")


@lru_cache(maxsize=1)
def load_carriers() -> list[dict[str, Any]]:
    return _read_json("carriers.json")


@lru_cache(maxsize=1)
def load_exceptions() -> list[dict[str, Any]]:
    return _read_json("exceptions.json")


@lru_cache(maxsize=1)
def load_eval_queries() -> list[dict[str, Any]]:
    return _read_jsonl("eval_queries.jsonl")


# --------- small convenience lookups (used across labs) --------- #


def find_stock(sku: str, warehouse: str) -> dict[str, Any] | None:
    for row in load_inventory():
        if row["sku"] == sku and row["warehouse"] == warehouse:
            return row
    return None


def find_po(po_number: str) -> dict[str, Any] | None:
    for row in load_purchase_orders():
        if row["po_number"] == po_number:
            return row
    return None


def find_supplier(supplier_id_or_name: str) -> dict[str, Any] | None:
    needle = supplier_id_or_name.lower()
    for row in load_suppliers():
        if row["supplier_id"].lower() == needle or row["name"].lower() == needle:
            return row
    return None


def find_contract(supplier_id: str) -> dict[str, Any] | None:
    for row in load_contracts():
        if row["supplier_id"] == supplier_id:
            return row
    return None


def find_customer(customer_id: str) -> dict[str, Any] | None:
    for row in load_customers():
        if row["customer_id"] == customer_id:
            return row
    return None


def find_order(order_id: str) -> dict[str, Any] | None:
    for row in load_orders():
        if row["order_id"] == order_id:
            return row
    return None
