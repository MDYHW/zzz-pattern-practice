"""Compare provisional windows with recorded observations without inventing boundaries."""
import argparse
import hashlib
import itertools
import json
import math
from pathlib import Path

DEFAULT_INPUT = Path(__file__).with_name('window_observations.json')


def bracket(value):
    if not (isinstance(value, list) and len(value) == 2
            and all(type(n) in (int, float) and math.isfinite(n) for n in value)
            and value[0] <= value[1]):
        raise ValueError('Expected a finite ordered bracket')
    return value


def map_observation(observation):
    press = bracket(observation['overlay_onset'])
    alignment = observation['alignment']
    if alignment['kind'] == 'excluded':
        return None
    if alignment['kind'] == 'identity':
        return press
    if alignment['kind'] != 'translation':
        raise ValueError('Unsupported alignment')
    source = bracket(alignment['source'])
    reference = bracket(alignment['reference'])
    values = [p - s + r for p, s, r in itertools.product(press, source, reference)]
    return [min(values), max(values)]


def classify(observed, window):
    low, high = bracket(observed)
    start, end = bracket(window)
    if high < start:
        return 'before'
    if low > end:
        return 'after'
    if low >= start and high <= end:
        return 'inside'
    return 'overlaps_boundary'


def audit(data):
    if data.get('schema_version') != 1:
        raise ValueError('Unsupported schema')
    fps = data['recording_fps']
    if type(fps) not in (int, float) or not math.isfinite(fps) or fps <= 0:
        raise ValueError('Expected positive recording fps')
    windows = [bracket(w) for w in data['baseline_clip_windows']]
    if len(windows) != 4 or any(s >= e for s, e in windows):
        raise ValueError('Expected four positive baseline windows')
    width = sum(e - s for s, e in windows) / len(windows)
    common = [[(s + e - width) / 2, (s + e + width) / 2] for s, e in windows]
    trim = data['reference_trim_frame']
    if type(trim) is not int or trim < 0:
        raise ValueError('Expected nonnegative integer trim frame')
    rows = []
    for observation in data['observations']:
        attack = observation['attack']
        if type(attack) is not int or not 1 <= attack <= 4:
            raise ValueError('Expected attack 1..4')
        mapped = map_observation(observation)
        row = {key: observation[key] for key in ('case', 'attack', 'outcome')}
        if mapped is None:
            row['comparison'] = 'excluded'
            row['reason'] = observation['alignment']['reason']
        else:
            clip = [(value - trim) / fps for value in mapped]
            row.update(reference_frame_range=mapped, reference_clip_range=clip,
                       baseline=classify(clip, windows[attack - 1]),
                       mean_width_candidate=classify(clip, common[attack - 1]))
        rows.append(row)
    readiness = []
    for key, observed in data['reference_ready_brackets'].items():
        i = int(key) - 1
        ready = bracket(observed)
        if not 0 <= i < 4:
            raise ValueError('Expected ready attack 1..4')
        readiness.append({'attack': i + 1,
                          'baseline_start_minus_ready_ms': [(windows[i][0] * fps + trim - r) * 1000 / fps for r in reversed(ready)],
                          'candidate_start_minus_ready_ms': [(common[i][0] * fps + trim - r) * 1000 / fps for r in reversed(ready)]})
    return {'mean_width_ms': width * 1000, 'rows': rows,
            'readiness_comparison': readiness, 'limits': data['limits']}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('observations', type=Path, nargs='?', default=DEFAULT_INPUT)
    args = parser.parse_args()
    raw = args.observations.read_bytes()
    result = audit(json.loads(raw))
    result['input_sha256'] = hashlib.sha256(raw).hexdigest()
    print(json.dumps(result, ensure_ascii=False, indent=2, allow_nan=False))


if __name__ == '__main__':
    main()
