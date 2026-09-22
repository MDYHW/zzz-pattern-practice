import json
import unittest

import audit_windows as windows


class WindowAuditTests(unittest.TestCase):
    def test_uncertainty_is_preserved_instead_of_forcing_a_pass(self):
        data = json.loads(windows.DEFAULT_INPUT.read_text(encoding='utf-8'))
        rows = windows.audit(data)['rows']
        r3 = [row for row in rows if row['case'] == 'R3']
        self.assertEqual(r3[0]['reference_frame_range'], [490, 498])
        self.assertEqual(r3[0]['baseline'], 'overlaps_boundary')
        self.assertEqual(r3[0]['mean_width_candidate'], 'overlaps_boundary')
        self.assertEqual(r3[1]['reference_frame_range'], [582, 585])
        self.assertEqual(r3[1]['baseline'], 'before')
        self.assertEqual(r3[1]['mean_width_candidate'], 'before')
        self.assertEqual([r['comparison'] for r in r3[2:]], ['excluded', 'excluded'])
        self.assertTrue(all(r['baseline'] == r['mean_width_candidate'] == 'inside' for r in rows[:8]))

    def test_translation_uses_independent_events_not_outcome(self):
        observation = {'overlay_onset': [9, 10], 'outcome': 'success',
                       'alignment': {'kind': 'translation', 'source': [12, 14], 'reference': [100, 103]}}
        self.assertEqual(windows.map_observation(observation), [95, 101])
        observation['outcome'] = 'failure'
        self.assertEqual(windows.map_observation(observation), [95, 101])

    def test_inclusive_edges_and_partial_overlap_are_distinct(self):
        for values, expected in [([1, 2], 'inside'), ([0, 1], 'overlaps_boundary'),
                                 ([2, 3], 'overlaps_boundary'), ([0, 0.9], 'before'),
                                 ([2.1, 3], 'after')]:
            self.assertEqual(windows.classify(values, [1, 2]), expected)

    def test_invalid_brackets_cannot_produce_a_classification(self):
        for values in ([2, 1], [1, float('nan')], [False, 2], [1]):
            with self.assertRaises(ValueError):
                windows.classify(values, [1, 2])


if __name__ == '__main__':
    unittest.main()
