import importlib.util
import sys
import unittest
from pathlib import Path


MODULE_ROOT = Path(__file__).resolve(strict=True).parents[1]
SOURCE_ROOT = MODULE_ROOT / "src"
SOURCE_PATH = SOURCE_ROOT / "fleet_simulation.py"
SUBJECT_NAME = "_dps_factory_release_controller_fleet_simulation_subject"


def load_subject():
    if SOURCE_ROOT.is_symlink() or SOURCE_PATH.is_symlink():
        raise ImportError("fleet simulation subject path must not contain a symbolic link")
    source_root = SOURCE_ROOT.resolve(strict=True)
    source_path = SOURCE_PATH.resolve(strict=True)
    if source_root.parent != MODULE_ROOT or source_path.parent != source_root:
        raise ImportError("fleet simulation subject escaped the module-owned src directory")

    existing = sys.modules.get(SUBJECT_NAME)
    if existing is not None:
        existing_path = Path(getattr(existing, "__file__", "")).resolve(strict=True)
        if existing_path != source_path:
            raise ImportError("fleet simulation subject module name is already bound elsewhere")
        return existing

    spec = importlib.util.spec_from_file_location(SUBJECT_NAME, source_path)
    if spec is None or spec.loader is None:
        raise ImportError("unable to create the fleet simulation subject module spec")
    subject = importlib.util.module_from_spec(spec)
    sys.modules[SUBJECT_NAME] = subject
    try:
        spec.loader.exec_module(subject)
    except BaseException:
        sys.modules.pop(SUBJECT_NAME, None)
        raise
    return subject


FleetSimulator = load_subject().FleetSimulator


class FleetSimulationTests(unittest.TestCase):
    def test_exact_f4_capacity_targets_recover_without_side_effects(self):
        report = FleetSimulator().run()
        self.assertEqual("PASS", report["result"])
        self.assertEqual("SIMULATION", report["kind"])
        self.assertEqual("INTEGRATION_VERIFIED", report["verification_level"])
        self.assertTrue(report["simulation_only"])
        self.assertFalse(report["canary_verified"])
        self.assertFalse(report["scale_verified"])
        metrics = report["metrics"]
        self.assertEqual(200, metrics["registered_devices"])
        self.assertEqual(100, metrics["sustained_concurrency"])
        self.assertEqual(200, metrics["burst_concurrency"])
        self.assertEqual(400, metrics["equivalent_load"])
        self.assertEqual(400, metrics["unique_commands_completed"])
        self.assertGreater(metrics["duplicate_deliveries"], 0)
        self.assertGreater(metrics["timeouts_injected"], 0)
        self.assertGreater(metrics["disconnects_injected"], 0)
        self.assertEqual(1, metrics["process_crashes_injected"])
        self.assertEqual(1, metrics["process_recoveries"])
        self.assertEqual(0, metrics["lost_commands"])
        self.assertEqual(0, metrics["duplicate_side_effects"])
        self.assertEqual(0, metrics["side_effect_count"])

    def test_unrecovered_crash_is_a_simulation_failure(self):
        report = FleetSimulator().run(recover_after_crash=False)
        self.assertEqual("FAIL", report["result"])
        self.assertTrue(report["simulation_only"])
        self.assertFalse(report["canary_verified"])
        self.assertFalse(report["scale_verified"])
        self.assertGreater(report["metrics"]["lost_commands"], 0)


if __name__ == "__main__":
    unittest.main()
