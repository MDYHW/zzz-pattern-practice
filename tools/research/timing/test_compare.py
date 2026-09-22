import copy
import json
import math
import tempfile
import unittest
from pathlib import Path

import compare


class ComparisonTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.fixture = json.loads(compare.DEFAULT_INPUT.read_text(encoding="utf-8"))

    def test_documented_ranges_and_repeated_image_sensitivity(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "observations.json"
            path.write_text(json.dumps(self.fixture), encoding="utf-8")
            result = compare.run(path)
        original, expanded = result["scenarios"]
        original_129 = original["residuals"][0]
        expanded_129 = expanded["residuals"][0]
        self.assertTrue(math.isclose(original_129["midpoint_residual_video_frames"],
                                      7.594059405940584, abs_tol=1e-9))
        self.assertEqual(original_129["corner_count"], 64)
        self.assertFalse(original_129["zero_within_corner_range"])
        for actual, expected in zip(original_129["corner_residual_video_frames"],
                                    [4.607260726072582, 10.580858085808586]):
            self.assertAlmostEqual(actual, expected)
        self.assertTrue(expanded_129["zero_within_corner_range"])
        for actual, expected in zip(expanded_129["corner_residual_video_frames"],
                                    [-4.678807947019891, 15.287128712871322]):
            self.assertAlmostEqual(actual, expected)
        self.assertTrue(expanded["residuals"][1]["zero_within_corner_range"])

    def test_changed_observation_changes_comparison(self):
        scenario = copy.deepcopy(self.fixture["scenarios"][0])
        original = compare.compare_scenario(scenario, [57, 129, 209, 300], 60)
        scenario["R2"][1] = [628, 629]
        changed = compare.compare_scenario(scenario, [57, 129, 209, 300], 60)
        self.assertAlmostEqual(changed["residuals"][0]["midpoint_residual_video_frames"] -
                               original["residuals"][0]["midpoint_residual_video_frames"], 5)

    def test_rejects_malformed_observations(self):
        invalid = [
            (lambda d: d.update(schema_version=2), "schema_version"),
            (lambda d: d.update(recording_fps=0), "recording_fps"),
            (lambda d: d.update(internal_effect_coordinates=[57, 129, float("nan"), 300]),
             "internal_effect_coordinates"),
            (lambda d: d["scenarios"][0]["T3"].__setitem__(1, [295, 300]), "ordered"),
            (lambda d: d["scenarios"][0]["R2"].__setitem__(0, [526, 525]), "ordered"),
            (lambda d: d["scenarios"][0]["R2"].__setitem__(0, [True, 527]), "integers"),
            (lambda d: d["provenance"]["sources"][0].update(sha256="bad"), "sha256"),
        ]
        for change, message in invalid:
            with self.subTest(message=message):
                data = copy.deepcopy(self.fixture)
                change(data)
                with self.assertRaisesRegex(ValueError, message):
                    compare.validate(data)

    def test_rejects_nonfinite_json_constant(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "bad.json"
            path.write_text('{"schema_version": NaN}', encoding="utf-8")
            with self.assertRaisesRegex(ValueError, "non-finite"):
                compare.run(path)


if __name__ == "__main__":
    unittest.main()
