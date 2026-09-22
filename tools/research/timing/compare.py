"""Recompute video-effect alignment residuals from small human-observation brackets.

The values are video frame indices. Internal effect coordinates only label the
four effects; they are never converted to seconds or treated as input windows.
"""

import argparse
import hashlib
import itertools
import json
import math
import sys
from pathlib import Path


DEFAULT_INPUT = Path(__file__).with_name("observations.json")


def require(condition, message):
    if not condition:
        raise ValueError(message)


def validate_brackets(brackets, label):
    require(isinstance(brackets, list) and len(brackets) == 4,
            f"{label}: expected four brackets")
    previous_high = -1
    for index, bracket in enumerate(brackets):
        require(isinstance(bracket, list) and len(bracket) == 2,
                f"{label}[{index}]: expected [low, high]")
        low, high = bracket
        require(all(type(value) is int and value >= 0 for value in bracket),
                f"{label}[{index}]: frames must be nonnegative integers")
        require(previous_high < low <= high,
                f"{label}[{index}]: brackets must be ordered and disjoint")
        previous_high = high


def validate(data):
    require(isinstance(data, dict) and data.get("schema_version") == 1,
            "schema_version must be 1")
    require(type(data.get("recording_fps")) is int and data["recording_fps"] > 0,
            "recording_fps must be a positive integer")
    coordinates = data.get("internal_effect_coordinates")
    require(isinstance(coordinates, list) and len(coordinates) == 4 and
            all(type(value) in (int, float) and math.isfinite(value)
                for value in coordinates) and
            all(a < b for a, b in zip(coordinates, coordinates[1:])),
            "internal_effect_coordinates must be four finite, increasing numbers")
    provenance = data.get("provenance")
    require(isinstance(provenance, dict) and
            isinstance(provenance.get("kind"), str) and provenance["kind"] and
            isinstance(provenance.get("sources"), list) and provenance["sources"],
            "provenance kind and sources are required")
    for source in provenance["sources"]:
        require(isinstance(source, dict) and
                all(isinstance(source.get(field), str) and source[field]
                    for field in ("path", "role")),
                "each provenance source needs path and role")
        if "sha256" in source:
            value = source["sha256"]
            require(isinstance(value, str) and len(value) == 64 and
                    all(char in "0123456789abcdef" for char in value),
                    "provenance sha256 must be 64 lowercase hex characters")
    scenarios = data.get("scenarios")
    require(isinstance(scenarios, list) and scenarios,
            "scenarios must be a nonempty list")
    ids = set()
    for scenario in scenarios:
        require(isinstance(scenario, dict) and
                isinstance(scenario.get("id"), str) and scenario["id"] and
                isinstance(scenario.get("meaning"), str) and scenario["meaning"],
                "each scenario needs id and meaning")
        require(scenario["id"] not in ids, "scenario ids must be unique")
        ids.add(scenario["id"])
        validate_brackets(scenario.get("T3"), f"{scenario['id']}.T3")
        validate_brackets(scenario.get("R2"), f"{scenario['id']}.R2")


def residual(x, y, index):
    prediction = y[0] + (y[3] - y[0]) * (x[index] - x[0]) / (x[3] - x[0])
    return y[index] - prediction


def compare_scenario(scenario, coordinates, fps):
    result = []
    for index in (1, 2):
        x_brackets, y_brackets = scenario["T3"], scenario["R2"]
        midpoint_x = [(low + high) / 2 for low, high in x_brackets]
        midpoint_y = [(low + high) / 2 for low, high in y_brackets]
        midpoint = residual(midpoint_x, midpoint_y, index)
        values = []
        # The six selected endpoint choices are x0,x3,xi,y0,y3,yi.
        for choices in itertools.product((0, 1), repeat=6):
            x = [None] * 4
            y = [None] * 4
            for bracket_index, choice in zip((0, 3, index), choices[:3]):
                x[bracket_index] = x_brackets[bracket_index][choice]
            for bracket_index, choice in zip((0, 3, index), choices[3:]):
                y[bracket_index] = y_brackets[bracket_index][choice]
            values.append(residual(x, y, index))
        low, high = min(values), max(values)
        result.append({
            "internal_effect_coordinate": coordinates[index],
            "midpoint_residual_video_frames": midpoint,
            "corner_residual_video_frames": [low, high],
            "corner_residual_ms_at_recording_fps": [low * 1000 / fps, high * 1000 / fps],
            "zero_within_corner_range": low <= 0 <= high,
            "corner_count": len(values),
        })
    return {"id": scenario["id"], "meaning": scenario["meaning"], "residuals": result}


def run(path):
    raw = path.read_bytes()
    data = json.loads(raw, parse_constant=lambda value: (_ for _ in ()).throw(
        ValueError(f"non-finite JSON constant: {value}")))
    validate(data)
    return {
        "schema_version": 1,
        "input": str(path),
        "input_sha256": hashlib.sha256(raw).hexdigest(),
        "recording_fps": data["recording_fps"],
        "units": "recorded video frames; milliseconds use recording fps only",
        "provenance": data["provenance"],
        "method": "T3 first/last effects align to R2 first/last; enumerate 64 bracket corners per middle effect",
        "scenarios": [compare_scenario(item, data["internal_effect_coordinates"],
                                       data["recording_fps"])
                      for item in data["scenarios"]],
        "limit": "Human visual brackets and repeated images are not engine-event bounds or physical-key acceptance windows.",
    }


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("observations", nargs="?", type=Path, default=DEFAULT_INPUT)
    args = parser.parse_args(argv)
    try:
        result = run(args.observations)
    except (OSError, ValueError, json.JSONDecodeError) as exc:
        parser.exit(2, f"timing comparison: {exc}\n")
    json.dump(result, sys.stdout, ensure_ascii=False, indent=2, allow_nan=False)
    sys.stdout.write("\n")


if __name__ == "__main__":
    main()
