import { createElement } from 'react';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { afterEach, expect, test, vi } from 'vitest';
import { Results } from './Results';
import { groupExtraInputs, resultTimeline } from './result-timeline';
import { cueWindows, createAttempt, finish, press } from './judge';
import type { Attempt, Cue } from './judge';
import { skills, vesperExecute01, vesperExecute02, withEntryKey } from '../content/skills';

afterEach(() => { cleanup(); vi.unstubAllGlobals(); });

test('idle time folds while each window and its nearby input offsets keep one scale', () => {
  for (const content of skills) {
    const timeline = resultTimeline(content.cues, content.duration);
    expect(timeline.size).toBeLessThan(content.duration * .6);
    expect(timeline.position(0)).toBe(0);
    expect(timeline.position(content.duration)).toBe(timeline.size);
    content.cues.forEach(cue => cueWindows(cue).forEach(window => {
      expect(timeline.position(window.end) - timeline.position(window.start)).toBeCloseTo(window.end - window.start, 10);
      expect(timeline.position(window.start) - timeline.position(window.start - .25)).toBeCloseTo(.25, 10);
      expect(timeline.position(window.end + .25) - timeline.position(window.end)).toBeCloseTo(.25, 10);
    }));
    const edges = timeline.segments.flatMap(segment => [segment.start, segment.end]);
    const positions = edges.map(timeline.position);
    expect(positions).toEqual([...positions].sort((a, b) => a - b));
  }
});

test('timeline keeps the entire multi-key envelope and the gap between windows at one scale', () => {
  const cue: Cue = {
    id: 'entry', label: '진입', key: 'MouseRight', start: 1, end: 1.5, reference: 1.25,
    alternateWindow: { key: 'Space', start: 2, end: 3, reference: 2.5 },
  };
  const timeline = resultTimeline([cue], 4);
  expect(timeline.position(3) - timeline.position(1)).toBeCloseTo(2, 10);
  expect(timeline.segments.filter(segment => !segment.compressed)).toHaveLength(1);
});

test('nearby focus ranges merge without stretching an overlapping neighborhood', () => {
  const cues: Cue[] = [
    { id: 'a', label: 'a', key: 'Space', start: 1, end: 1.2, reference: 1.1 },
    { id: 'b', label: 'b', key: 'Space', start: 1.5, end: 1.7, reference: 1.6 },
  ];
  const timeline = resultTimeline(cues, 3);
  expect(timeline.position(2) - timeline.position(.7)).toBeCloseTo(1.3);
  expect(timeline.segments.filter(segment => !segment.compressed)).toHaveLength(1);
});

test('folded leading, middle and trailing inputs retain every record and never mix keys', () => {
  const timeline = resultTimeline(vesperExecute02.cues, vesperExecute02.duration);
  const inputs: Attempt['extraInputs'] = [
    { time: 0, key: 'Space', reason: 'outside' },
    { time: .01, key: 'MouseRight', reason: 'outside' },
    { time: .02, key: 'Space', reason: 'outside' },
    { time: 5.2, key: 'Space', reason: 'outside' },
    { time: vesperExecute02.duration, key: 'MouseRight', reason: 'outside' },
  ];
  for (const width of [636, 850, 1200]) {
    const groups = groupExtraInputs(inputs, timeline, width);
    expect(groups[0].indices).toEqual([0, 2]);
    expect(groups.flatMap(group => group.indices).sort((a, b) => a - b)).toEqual([0, 1, 2, 3, 4]);
    groups.forEach(group => expect(group.indices.every(index => inputs[index].key === group.key)).toBe(true));
  }
  expect(groupExtraInputs([], timeline, 600)).toEqual([]);
});

test('key rows and folded markers expose original records without assigning extras to attacks', () => {
  vi.stubGlobal('ResizeObserver', class { observe() {} disconnect() {} });
  const content = vesperExecute02;
  let attempt = createAttempt(content.cues);
  for (const [key, time] of [['Space', 0], ['MouseRight', .01], ['Space', .02], ['Other', .03]] as const) {
    attempt = press(content.cues, attempt, key, time);
  }
  render(createElement(Results, { content, attempt: finish(content.cues, attempt) }));
  expect(screen.getByTestId('extras')).toHaveTextContent('3');
  expect(document.querySelectorAll('.result-key-row')).toHaveLength(2);
  const space = screen.getByRole('button', { name: /추가 입력 Space 2회/ });
  fireEvent.click(space);
  expect(document.querySelectorAll('.extra-records li')).toHaveLength(2);
  expect(document.querySelector('.extra-records')).toHaveTextContent('0.00초Space구간 밖');
  expect(document.querySelector('.extra-records')).not.toHaveTextContent('우클릭');
  fireEvent.click(space);
  expect(document.querySelector('.result-detail')).toBeNull();
  expect(screen.getByRole('button', { name: /추가 입력 우클릭 1회/ })).toBeInTheDocument();
  expect(screen.queryByRole('button', { name: /추가 입력 기타 키/ })).not.toBeInTheDocument();
});

test('selected Space entry success is shown on the actual Space row and in the detail', () => {
  vi.stubGlobal('ResizeObserver', class { observe() {} disconnect() {} });
  const content = withEntryKey(vesperExecute02, 'Space');
  const attempt = press(content.cues, createAttempt(content.cues), 'Space', content.cues[0].reference);
  render(createElement(Results, { content, attempt: finish(content.cues, attempt) }));
  expect(document.querySelector('.result-hit')).toHaveAttribute('data-key', 'Space');
  expect(document.querySelectorAll('.result-hit')).toHaveLength(1);
  fireEvent.click(screen.getByRole('button', { name: '1. 진입 성공' }));
  expect(document.querySelector('.result-detail')).toHaveTextContent('실제 입력 Space');
});

test('multi-key result draws each row at its own width and describes both intervals', () => {
  vi.stubGlobal('ResizeObserver', class { observe() {} disconnect() {} });
  const cue: Cue = {
    id: 'entry', label: '진입', key: 'MouseRight', start: 1, end: 1.5, reference: 1.25,
    alternateWindow: { key: 'Space', start: 2, end: 3, reference: 2.5 },
  };
  const content = { ...vesperExecute02, duration: 4, cues: [cue] };
  const attempt = createAttempt(content.cues);
  render(createElement(Results, { content, attempt }));

  expect(attempt.results).toHaveLength(1);
  expect(screen.getByLabelText('0 / 1 대응 성공')).toBeInTheDocument();
  const mouseWindow = document.querySelector<HTMLElement>('.result-window[data-key="MouseRight"]')!;
  const spaceWindow = document.querySelector<HTMLElement>('.result-window[data-key="Space"]')!;
  expect(parseFloat(spaceWindow.style.width) / parseFloat(mouseWindow.style.width)).toBeCloseTo(2, 10);
  expect(spaceWindow.style.left).not.toBe(mouseWindow.style.left);
  expect(document.querySelectorAll('.result-cue .action-icon')).toHaveLength(1);
  expect(document.querySelector('.result-icon-dual')).toBeNull();
  expect(document.querySelector('.result-cue .action-icon')).toHaveAttribute('src', expect.stringContaining('zzz-dodge.png'));

  fireEvent.click(screen.getByRole('button', { name: '1. 진입 대기' }));
  expect(document.querySelector('.result-detail')).toHaveTextContent(
    '우클릭 구간 1.00초–1.50초 / Space 구간 2.00초–3.00초',
  );
});

test('success in a distinct alternate interval is shown on that key row', () => {
  vi.stubGlobal('ResizeObserver', class { observe() {} disconnect() {} });
  const cue: Cue = {
    id: 'entry', label: '진입', key: 'MouseRight', start: 1, end: 1.5, reference: 1.25,
    alternateWindow: { key: 'Space', start: 2, end: 3, reference: 2.5 },
  };
  const content = { ...vesperExecute02, duration: 4, cues: [cue] };
  const attempt = press(content.cues, createAttempt(content.cues), 'Space', 2.5);
  render(createElement(Results, { content, attempt }));

  expect(document.querySelector('.result-hit')).toHaveAttribute('data-key', 'Space');
  fireEvent.click(screen.getByRole('button', { name: '1. 진입 성공' }));
  expect(document.querySelector('.result-detail')).toHaveTextContent('실제 입력 Space 2.50초');
});
