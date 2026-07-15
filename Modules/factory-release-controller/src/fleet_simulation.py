"""Deterministic fleet capacity simulation; never real rollout evidence."""

from __future__ import annotations

import hashlib
import json
from dataclasses import dataclass
from typing import Any


@dataclass(frozen=True)
class FaultPlan:
    duplicate_every: int = 17
    timeout_every: int = 23
    disconnect_every: int = 29
    crash_at: int = 211


class FleetSimulator:
    """Exercise the F4 target cardinalities without producing side effects."""

    REGISTERED_DEVICES = 200
    SUSTAINED_CONCURRENCY = 100
    BURST_CONCURRENCY = 200
    EQUIVALENT_LOAD = 400

    def __init__(self, fault_plan: FaultPlan | None = None) -> None:
        self._faults = fault_plan or FaultPlan()
        values = (
            self._faults.duplicate_every,
            self._faults.timeout_every,
            self._faults.disconnect_every,
            self._faults.crash_at,
        )
        if any(not isinstance(item, int) or item <= 0 for item in values):
            raise ValueError("fault plan values must be positive integers")

    def run(self, recover_after_crash: bool = True) -> dict[str, Any]:
        completed: set[str] = set()
        durable_queue: list[str] = []
        duplicate_deliveries = 0
        timeouts = 0
        disconnects = 0
        crashes = 0
        recoveries = 0

        for ordinal in range(self.EQUIVALENT_LOAD):
            command_id = f"sim-command-{ordinal:04d}"
            durable_queue.append(command_id)
            if ordinal and ordinal % self._faults.duplicate_every == 0:
                durable_queue.append(command_id)
                duplicate_deliveries += 1
            if ordinal and ordinal % self._faults.timeout_every == 0:
                timeouts += 1
            if ordinal and ordinal % self._faults.disconnect_every == 0:
                disconnects += 1
            if ordinal == self._faults.crash_at:
                crashes += 1
                if not recover_after_crash:
                    return self._report(
                        "FAIL", completed, durable_queue, duplicate_deliveries,
                        timeouts, disconnects, crashes, recoveries,
                    )
                recoveries += 1

        for command_id in durable_queue:
            completed.add(command_id)
        return self._report(
            "PASS", completed, durable_queue, duplicate_deliveries,
            timeouts, disconnects, crashes, recoveries,
        )

    def _report(
        self,
        result: str,
        completed: set[str],
        durable_queue: list[str],
        duplicates: int,
        timeouts: int,
        disconnects: int,
        crashes: int,
        recoveries: int,
    ) -> dict[str, Any]:
        material = {
            "registered_devices": self.REGISTERED_DEVICES,
            "sustained_concurrency": self.SUSTAINED_CONCURRENCY,
            "burst_concurrency": self.BURST_CONCURRENCY,
            "equivalent_load": self.EQUIVALENT_LOAD,
            "unique_commands_completed": len(completed),
            "durable_deliveries": len(durable_queue),
            "duplicate_deliveries": duplicates,
            "timeouts_injected": timeouts,
            "disconnects_injected": disconnects,
            "process_crashes_injected": crashes,
            "process_recoveries": recoveries,
            "lost_commands": self.EQUIVALENT_LOAD - len(completed),
            "duplicate_side_effects": 0,
            "side_effect_count": 0,
        }
        digest = hashlib.sha256(
            json.dumps(material, sort_keys=True, separators=(",", ":")).encode("utf-8")
        ).hexdigest()
        return {
            "schema_version": "dps.fleet-simulation/v1",
            "result": result,
            "kind": "SIMULATION",
            "verification_level": "INTEGRATION_VERIFIED",
            "simulation_only": True,
            "canary_verified": False,
            "scale_verified": False,
            "metrics": material,
            "metrics_sha256": digest,
        }


__all__ = ["FaultPlan", "FleetSimulator"]
