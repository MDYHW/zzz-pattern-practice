import { StrictMode } from 'react';
import { act, cleanup, fireEvent, render, screen } from '@testing-library/react';
import { afterEach, beforeEach, expect, test, vi } from 'vitest';
import { usePracticeSession } from './session';
import type { PracticeContent } from './session';

const content: PracticeContent = {
  id: 'test', title: 'Test skill', video: '/test.mp4', poster: '/test.jpg', duration: 4,
  cues: [
    { id: 'entry', label: 'Entry', key: 'MouseRight', start: 1, end: 1.2, reference: 1.1 },
    { id: 'first', label: 'First', key: 'Space', start: 2, end: 2.2, reference: 2.1 },
    { id: 'second', label: 'Second', key: 'Space', start: 3, end: 3.2, reference: 3.1 },
  ],
};

type Session = ReturnType<typeof usePracticeSession>;
type Snapshot = Pick<Session, 'phase' | 'time' | 'attempt' | 'reason'>;

function Harness({ content: practiceContent = content }: { content?: PracticeContent }) {
  const session = usePracticeSession(practiceContent);
  return (
    <section ref={session.surfaceRef} tabIndex={-1} data-testid="surface">
      <video ref={session.videoRef} src={practiceContent.video} data-testid="video" />
      <button onClick={session.start}>Start</button>
      <button onClick={session.pause}>Pause</button><button onClick={session.resume}>Resume</button>
      <button onClick={() => session.input('MouseRight')}>Touch dodge</button>
      <button onClick={() => session.input('Space')}>Touch assist</button>
      <output data-testid="snapshot">{JSON.stringify({
        phase: session.phase, time: session.time, attempt: session.attempt, reason: session.reason,
      })}</output>
    </section>
  );
}

function deferred() {
  let resolve!: () => void;
  let reject!: (error: Error) => void;
  const promise = new Promise<void>((yes, no) => { resolve = yes; reject = no; });
  return { promise, resolve, reject };
}

const videoPrototype = HTMLVideoElement.prototype;
const frameDescriptor = Object.getOwnPropertyDescriptor(videoPrototype, 'requestVideoFrameCallback');
const cancelDescriptor = Object.getOwnPropertyDescriptor(videoPrototype, 'cancelVideoFrameCallback');
let readyState: number;
let bufferedUntil: number;
let frames: Map<number, VideoFrameRequestCallback>;
let plays: ReturnType<typeof deferred>[];
let requestFrame: ReturnType<typeof vi.fn<(callback: VideoFrameRequestCallback) => number>>;
let cancelFrame: ReturnType<typeof vi.fn<(id: number) => void>>;

beforeEach(() => {
  readyState = 0;
  bufferedUntil = content.duration;
  frames = new Map();
  plays = [];
  let nextFrame = 0;
  vi.spyOn(HTMLMediaElement.prototype, 'readyState', 'get').mockImplementation(() => readyState);
  vi.spyOn(HTMLMediaElement.prototype, 'duration', 'get').mockReturnValue(content.duration);
  vi.spyOn(HTMLMediaElement.prototype, 'buffered', 'get').mockImplementation(() => ({
    length: 1, start: () => 0, end: () => bufferedUntil,
  }));
  vi.spyOn(HTMLMediaElement.prototype, 'pause').mockImplementation(() => {});
  vi.spyOn(HTMLMediaElement.prototype, 'play').mockImplementation(() => {
    const play = deferred();
    plays.push(play);
    return play.promise;
  });
  requestFrame = vi.fn((callback: VideoFrameRequestCallback) => {
    frames.set(++nextFrame, callback);
    return nextFrame;
  });
  cancelFrame = vi.fn((id: number) => { frames.delete(id); });
  Object.defineProperty(videoPrototype, 'requestVideoFrameCallback', { configurable: true, value: requestFrame });
  Object.defineProperty(videoPrototype, 'cancelVideoFrameCallback', { configurable: true, value: cancelFrame });
});

afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
  vi.unstubAllGlobals();
  vi.useRealTimers();
  for (const [name, descriptor] of [
    ['requestVideoFrameCallback', frameDescriptor], ['cancelVideoFrameCallback', cancelDescriptor],
  ] as const) {
    if (descriptor) Object.defineProperty(videoPrototype, name, descriptor);
    else Reflect.deleteProperty(videoPrototype, name);
  }
});

const snapshot = () => JSON.parse(screen.getByTestId('snapshot').textContent!) as Snapshot;
const video = () => screen.getByTestId('video') as HTMLVideoElement;
const surface = () => screen.getByTestId('surface');
const start = () => fireEvent.click(screen.getByRole('button', { name: 'Start' }));
const statuses = () => snapshot().attempt.results.map(result => result.status);

function ready() {
  readyState = 4;
  fireEvent.canPlay(video());
}

function metadata(time: number): VideoFrameCallbackMetadata {
  return {
    mediaTime: time, presentationTime: 0, expectedDisplayTime: 0,
    width: 640, height: 360, presentedFrames: 1, processingDuration: 0,
  };
}

function frame(time: number) {
  const next = frames.entries().next().value;
  if (!next) throw new Error('No video frame was requested.');
  const [id, callback] = next;
  frames.delete(id);
  video().currentTime = time;
  act(() => callback(0, metadata(time)));
}

async function resolvePlay(index = plays.length - 1) {
  await act(async () => { plays[index].resolve(); await plays[index].promise; });
}

async function running() {
  ready();
  start();
  await resolvePlay();
  frame(0);
  expect(snapshot().phase).toBe('running');
}

function key(code: string, time: number, release = true) {
  video().currentTime = time;
  fireEvent.keyDown(surface(), { code });
  if (release) fireEvent.keyUp(surface(), { code });
}

function right(time: number, release = true) {
  video().currentTime = time;
  fireEvent.mouseDown(surface(), { button: 2 });
  if (release) fireEvent.mouseUp(surface(), { button: 2 });
}

test('explicit touch command shares judgment and rejects inputs outside running', async () => {
  render(<Harness />);
  const tap = (name: string, time: number) => { video().currentTime = time; fireEvent.click(screen.getByRole('button', { name })); };
  tap('Touch dodge', 1.1);
  expect(snapshot().attempt.lastInputTime).toBeUndefined();
  await running();
  tap('Touch dodge', 1.1);
  expect(statuses()[0]).toBe('success');
  fireEvent.click(screen.getByRole('button', { name: 'Pause' }));
  const paused = snapshot().attempt;
  tap('Touch assist', 2.1);
  expect(snapshot().attempt).toEqual(paused);
});

test('loading and pending play cannot accept inputs; a first frame alone does not start the attempt', async () => {
  render(<Harness />);
  start();
  right(1.1);
  expect(snapshot().phase).toBe('loading');
  expect(plays).toHaveLength(0);
  ready();
  start();
  right(1.1);
  frame(0);
  expect(snapshot().phase).toBe('starting');
  expect(snapshot().attempt.extras).toBe(0);
  expect(statuses()).toEqual(['pending', 'pending', 'pending']);
  await resolvePlay();
  expect(snapshot().phase).toBe('running');
  right(1.1);
  expect(statuses()[0]).toBe('success');
  fireEvent.ended(video());
  const finished = snapshot();
  right(1.1);
  key('Space', 2.1);
  expect(snapshot()).toEqual(finished);
  expect(finished.phase).toBe('finished');
  expect(statuses()).toEqual(['success', 'miss', 'miss']);
});

test('early and wrong inputs can recover; holding and repeated keydown do not create new presses', async () => {
  render(<Harness />);
  await running();
  right(0.5);
  key('Space', 1.1);
  right(1.1);
  expect(statuses()[0]).toBe('success');
  expect(snapshot().attempt.extras).toBe(2);
  key('Space', 2.1, false);
  video().currentTime = 3.1;
  fireEvent.keyDown(surface(), { code: 'Space', repeat: true });
  fireEvent.keyDown(surface(), { code: 'Space' });
  expect(statuses()).toEqual(['success', 'success', 'pending']);
  expect(snapshot().attempt.extras).toBe(2);
  fireEvent.keyUp(surface(), { code: 'Space' });
  key('Space', 3.1);
  expect(statuses()).toEqual(['success', 'success', 'success']);
});

test('preparation RMB is ignored only through its release boundary', async () => {
  const preparation: PracticeContent = {
    ...content,
    preparation: {
      end: .75,
      dodges: [{ id: 'first', label: '준비 회피1', time: .3 }, { id: 'second', label: '준비 회피2', time: .6 }],
      movements: [{ key: 'D', start: .2, end: .5 }, { key: 'A', start: .5, end: 1 }],
    },
  };
  render(<Harness content={preparation} />);
  await running();
  right(.5);
  expect(snapshot().attempt.extras).toBe(0);
  expect(snapshot().attempt.lastInputTime).toBeUndefined();
  expect(statuses()).toEqual(['pending', 'pending', 'pending']);
  key('Space', .5);
  expect(snapshot().attempt.extras).toBe(1);
  right(.8);
  expect(snapshot().attempt.extras).toBe(2);
  right(1.1);
  expect(statuses()[0]).toBe('success');
  right(1.15);
  expect(snapshot().attempt.extras).toBe(3);
});

test('a held mouse button and Space used to activate Start require release before practice input', async () => {
  render(<Harness />);
  ready();
  right(0, false);
  const button = screen.getByRole('button', { name: 'Start' });
  fireEvent.keyDown(button, { code: 'Space' });
  // jsdom does not perform the browser's keyboard activation default action.
  fireEvent.click(button);
  await resolvePlay();
  frame(0);
  right(1.1, false);
  expect(statuses()[0]).toBe('pending');
  fireEvent.mouseUp(surface(), { button: 2 });
  right(1.1);
  key('Space', 2.1, false);
  expect(statuses()[1]).toBe('pending');
  expect(snapshot().attempt.extras).toBe(0);
  fireEvent.keyUp(surface(), { code: 'Space' });
  key('Space', 2.1);
  expect(statuses().slice(0, 2)).toEqual(['success', 'success']);
});

test('manual retry resets the attempt and calls play once when ready, then waits for its first frame', async () => {
  render(<Harness />);
  await running();
  right(0.5);
  right(1.1);
  fireEvent.ended(video());
  expect(plays).toHaveLength(1);
  readyState = 2;
  start();
  expect(snapshot().phase).toBe('starting');
  expect(video().currentTime).toBe(0);
  expect(snapshot().time).toBe(0);
  expect(snapshot().attempt.extras).toBe(0);
  expect(statuses()).toEqual(['pending', 'pending', 'pending']);
  fireEvent.canPlay(video());
  expect(plays).toHaveLength(1);
  ready();
  fireEvent.canPlay(video());
  fireEvent.seeked(video());
  expect(plays).toHaveLength(2);
  await resolvePlay();
  expect(snapshot().phase).toBe('starting');
  frame(0);
  expect(snapshot().phase).toBe('running');
});

test('late play and frame completion after blur cannot revive a prior attempt or lose the new frame handle', async () => {
  render(<Harness />);
  ready();
  start();
  const oldFrame = frames.values().next().value!;
  fireEvent.blur(window);
  expect(snapshot().phase).toBe('paused');
  expect(frames.size).toBe(0);
  start();
  const newFrameId = frames.keys().next().value!;
  act(() => oldFrame(0, metadata(3.5)));
  await resolvePlay(0);
  expect(snapshot().phase).toBe('starting');
  expect(snapshot().time).toBe(0);
  expect(statuses()).toEqual(['pending', 'pending', 'pending']);
  fireEvent.click(screen.getByRole('button', { name: 'Pause' }));
  expect(cancelFrame).toHaveBeenCalledWith(newFrameId);
  expect(frames.size).toBe(0);
  await resolvePlay(1);
  expect(snapshot().phase).toBe('paused');
});

test('buffering interrupts without assigning missed attacks and ignores subsequent input', async () => {
  render(<Harness />);
  await running();
  frame(0.5);
  fireEvent.waiting(video());
  expect(snapshot().phase).toBe('interrupted');
  expect(snapshot().reason).toContain('로딩');
  expect(statuses()).toEqual(['pending', 'pending', 'pending']);
  const interrupted = snapshot();
  right(1.1);
  fireEvent.ended(video());
  expect(snapshot()).toEqual(interrupted);
  expect(frames.size).toBe(0);
});

test('fatal media errors leave an explicit reload state instead of an unplayable retry', async () => {
  render(<Harness />);
  await running();
  readyState = 0;
  fireEvent.error(video());
  expect(snapshot().phase).toBe('error');
  expect(snapshot().reason).toContain('새로고침');
  expect(statuses()).toEqual(['pending', 'pending', 'pending']);
  expect(frames.size).toBe(0);
  const count = plays.length;
  start();
  expect(snapshot().phase).toBe('error');
  expect(plays).toHaveLength(count);
});

test('held Space prevents page scrolling without generating another judged input', async () => {
  render(<Harness />);
  await running();
  frame(2.1);
  fireEvent.keyDown(surface(), { code: 'Space', cancelable: true });
  const previous = snapshot();
  expect(fireEvent.keyDown(surface(), { code: 'Space', repeat: true, cancelable: true })).toBe(false);
  expect(snapshot()).toEqual(previous);
  expect(fireEvent.keyDown(screen.getByRole('button', { name: 'Pause' }), { code: 'Space', repeat: true, cancelable: true })).toBe(true);
});

test('unrelated keys and non-right mouse buttons leave the attempt and native defaults alone', async () => {
  render(<Harness />);
  await running();
  frame(1.1);
  const before = snapshot();
  for (const code of ['KeyA', 'KeyE', 'Digit1', 'ArrowUp']) {
    expect(fireEvent.keyDown(surface(), { code, cancelable: true })).toBe(true);
    fireEvent.keyUp(surface(), { code });
  }
  for (const button of [0, 1, 3, 4]) {
    expect(fireEvent.mouseDown(surface(), { button, cancelable: true })).toBe(true);
    fireEvent.mouseUp(surface(), { button });
  }
  expect(snapshot()).toEqual(before);
  right(1.1);
  expect(statuses()[0]).toBe('success');
});

test('late frame metadata cannot move the guide and expiration behind the input playback clock', async () => {
  render(<Harness />);
  await running();
  const [id, callback] = frames.entries().next().value!;
  frames.delete(id);
  video().currentTime = 2.1;
  act(() => callback(0, metadata(0.5)));
  expect(snapshot().time).toBe(2.1);
  expect(statuses()).toEqual(['miss', 'pending', 'pending']);
  key('Space', 2.1);
  expect(statuses()).toEqual(['miss', 'success', 'pending']);
});

test('a partially buffered clip cannot begin an attempt even when its first frames can play', () => {
  render(<Harness />);
  bufferedUntil = 0.5;
  ready();
  expect(snapshot().phase).toBe('loading');
  start();
  expect(plays).toHaveLength(0);
  bufferedUntil = content.duration;
  fireEvent.progress(video());
  expect(snapshot().phase).toBe('ready');
  start();
  expect(plays).toHaveLength(1);
});

test('play rejection and a hidden document stop starting attempts without scoring them', async () => {
  render(<Harness />);
  ready();
  start();
  await act(async () => {
    plays[0].reject(new Error('Playback denied'));
    await plays[0].promise.catch(() => {});
  });
  expect(snapshot().phase).toBe('interrupted');
  expect(snapshot().reason).toContain('재생');
  expect(statuses()).toEqual(['pending', 'pending', 'pending']);
  start();
  vi.spyOn(document, 'hidden', 'get').mockReturnValue(true);
  fireEvent(document, new Event('visibilitychange'));
  await resolvePlay();
  expect(snapshot().phase).toBe('paused');
  expect(snapshot().reason).toContain('창');
  expect(statuses()).toEqual(['pending', 'pending', 'pending']);
  expect(frames.size).toBe(0);
});

test('StrictMode releases every input subscription and unmount cancels pending playback work', async () => {
  const add = vi.spyOn(window, 'addEventListener');
  const remove = vi.spyOn(window, 'removeEventListener');
  const view = render(<StrictMode><Harness /></StrictMode>);
  ready();
  start();
  const pendingFrame = frames.values().next().value!;
  const requestCount = requestFrame.mock.calls.length;
  view.unmount();
  expect(frames.size).toBe(0);
  for (const event of ['keydown', 'keyup', 'mousedown', 'mouseup', 'blur']) {
    const added = add.mock.calls.filter(call => call[0] === event);
    expect(added).toHaveLength(2);
    for (const call of added) expect(remove).toHaveBeenCalledWith(event, call[1]);
  }
  await resolvePlay();
  act(() => pendingFrame(0, metadata(1.1)));
  fireEvent.keyDown(window, { code: 'Space' });
  expect(requestFrame).toHaveBeenCalledTimes(requestCount);
  expect(frames.size).toBe(0);
});

test('Escape toggles pause and delayed resume, preserving time and results and requiring key release', async () => {
  vi.useFakeTimers();
  render(<Harness />);
  ready();
  key('Escape', 0);
  expect(snapshot().phase).toBe('ready');
  await running();
  right(1.1);
  frame(1.15);
  fireEvent.keyDown(surface(), { code: 'Escape' });
  const frozen = snapshot();
  expect(frozen.phase).toBe('paused');
  expect(frozen.time).toBe(1.15);
  expect(frames.size).toBe(0);
  fireEvent.keyDown(surface(), { code: 'Escape', repeat: true });
  fireEvent.keyDown(surface(), { code: 'Escape' });
  fireEvent.keyDown(surface(), { code: 'Space' });
  fireEvent.mouseDown(surface(), { button: 2 });
  expect(snapshot()).toEqual(frozen);
  fireEvent.keyUp(surface(), { code: 'Escape' });
  key('Escape', 1.15);
  expect(snapshot().phase).toBe('resuming');
  act(() => vi.advanceTimersByTime(999));
  expect(plays).toHaveLength(1);
  expect(snapshot().time).toBe(1.15);
  expect(snapshot().attempt).toEqual(frozen.attempt);
  act(() => vi.advanceTimersByTime(1));
  expect(plays).toHaveLength(2);
  await resolvePlay();
  frame(1.15);
  expect(snapshot().phase).toBe('running');
  expect(snapshot().attempt).toEqual(frozen.attempt);
  key('Space', 2.1, false);
  right(2.1, false);
  expect(snapshot().attempt).toEqual(frozen.attempt);
  fireEvent.keyUp(surface(), { code: 'Space' });
  key('Space', 2.1);
  expect(statuses()).toEqual(['success', 'success', 'pending']);
  fireEvent.ended(video());
  const result = snapshot();
  key('Escape', 4);
  expect(snapshot()).toEqual(result);
});

test('Escape and window departure cancel resume preparation; focus never resumes automatically', async () => {
  vi.useFakeTimers();
  render(<Harness />);
  await running();
  frame(.5);
  key('Escape', .5);
  fireEvent.click(screen.getByRole('button', { name: 'Resume' }));
  act(() => vi.advanceTimersByTime(400));
  key('Escape', .5);
  act(() => vi.advanceTimersByTime(1000));
  expect(snapshot().phase).toBe('paused');
  expect(plays).toHaveLength(1);
  fireEvent.click(screen.getByRole('button', { name: 'Resume' }));
  fireEvent.blur(window);
  fireEvent.focus(window);
  act(() => vi.advanceTimersByTime(1000));
  expect(snapshot().phase).toBe('paused');
  expect(plays).toHaveLength(1);
  expect(snapshot().time).toBe(.5);
});

test('restart replaces a paused attempt and unmount cancels resume preparation', async () => {
  vi.useFakeTimers();
  const mounted = render(<Harness />);
  await running();
  right(.5);
  right(1.1);
  key('Escape', 1.1);
  start();
  expect(snapshot().time).toBe(0);
  expect(snapshot().attempt.extras).toBe(0);
  expect(statuses()).toEqual(['pending', 'pending', 'pending']);
  await resolvePlay();
  frame(0);
  key('Escape', 0);
  fireEvent.click(screen.getByRole('button', { name: 'Resume' }));
  mounted.unmount();
  act(() => vi.advanceTimersByTime(1000));
  expect(plays).toHaveLength(2);
  expect(frames.size).toBe(0);
});

test('a fatal error during resume preparation cancels pending playback', async () => {
  vi.useFakeTimers();
  render(<Harness />);
  await running();
  key('Escape', .5);
  fireEvent.click(screen.getByRole('button', { name: 'Resume' }));
  fireEvent.error(video());
  act(() => vi.advanceTimersByTime(1000));
  expect(snapshot().phase).toBe('error');
  expect(plays).toHaveLength(1);
});

test('suspended partial preload finishes as local media and releases it on unmount', async () => {
  const network = vi.spyOn(HTMLMediaElement.prototype, 'networkState', 'get').mockReturnValue(1);
  vi.stubGlobal('fetch', vi.fn().mockResolvedValue({ ok: true, blob: async () => new Blob(['media']) }));
  const create = vi.spyOn(URL, 'createObjectURL').mockReturnValue('blob:complete-clip');
  const revoke = vi.spyOn(URL, 'revokeObjectURL').mockImplementation(() => {});
  bufferedUntil = .5;
  const { unmount } = render(<Harness />);
  await act(async () => { ready(); fireEvent.suspend(video()); });
  expect(fetch).toHaveBeenCalledTimes(1);
  expect(create).toHaveBeenCalledTimes(1);
  expect(video().getAttribute('src')).toBe('blob:complete-clip');
  expect(snapshot().phase).toBe('loading');
  fireEvent.canPlay(video());
  expect(snapshot().phase).toBe('ready');
  const signal = vi.mocked(fetch).mock.calls[0][1]!.signal!;
  unmount();
  expect(signal.aborted).toBe(true);
  expect(revoke).toHaveBeenCalledWith('blob:complete-clip');
  network.mockRestore();
  vi.unstubAllGlobals();
});

test('failed fallback media download reports an error without starting', async () => {
  vi.spyOn(HTMLMediaElement.prototype, 'networkState', 'get').mockReturnValue(1);
  vi.stubGlobal('fetch', vi.fn().mockResolvedValue({ ok: false, status: 404 }));
  bufferedUntil = .5;
  render(<Harness />);
  await act(async () => { ready(); });
  expect(snapshot().phase).toBe('error');
  expect(plays).toHaveLength(0);
  vi.unstubAllGlobals();
});

test('a late fallback download cannot replace media after native preload starts an attempt', async () => {
  vi.spyOn(HTMLMediaElement.prototype, 'networkState', 'get').mockReturnValue(1);
  const pending = deferred();
  vi.stubGlobal('fetch', vi.fn().mockImplementation(async () => {
    await pending.promise;
    return { ok: true, blob: async () => new Blob(['media']) };
  }));
  const create = vi.spyOn(URL, 'createObjectURL');
  bufferedUntil = .5;
  render(<Harness />);
  ready();
  bufferedUntil = content.duration;
  await running();
  await act(async () => { pending.resolve(); });
  expect(create).not.toHaveBeenCalled();
  expect(video().getAttribute('src')).toBe(content.video);
  expect(snapshot().phase).toBe('running');
});
