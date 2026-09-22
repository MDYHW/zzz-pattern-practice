import { useEffect, useRef, useState } from 'react';
import { flushSync } from 'react-dom';
import { advance, createAttempt, finish, press } from './judge';
import type { Attempt, Cue, PracticeKey } from './judge';

export interface PracticeContent {
  id: string;
  title: string;
  bossId?: string;
  bossLabel?: string;
  video: string;
  poster: string;
  duration: number;
  cues: readonly Cue[];
  /** Available entry choices; each run resolves exactly one before playback. */
  entryOptions?: readonly Pick<Cue, 'key' | 'start' | 'end' | 'reference'>[];
  /** Recorded final action, when the clip has selectable final-response variants. */
  recordedFinalKey?: PracticeKey;
  /** Non-scored route guidance that precedes the measured entry and follow-up cues. */
  preparation?: {
    /** Last preparation dodge release; RMB becomes a normal judged input after this time. */
    end: number;
    dodges: readonly { id: string; label: string; time: number }[];
    movements: readonly { key: 'W' | 'A' | 'S' | 'D'; start: number; end: number }[];
  };
  /** Recorded input overlay rectangles as percentages of the video frame. */
  recordedInputMasks?: readonly { x: number; y: number; width: number; height: number }[];
  /** In-game dodge/support HUD rectangles as percentages of the video frame. */
  gameInputMasks?: readonly { x: number; y: number; width: number; height: number }[];
}

export type Phase = 'loading' | 'ready' | 'starting' | 'running' | 'paused' | 'resuming' | 'finished' | 'interrupted' | 'error';
interface View {
  phase: Phase;
  time: number;
  attempt: Attempt;
  reason: string;
}

/** Each mounted video owns its subscriptions and one cancellable attempt. */
export function usePracticeSession(content: PracticeContent) {
  const videoRef = useRef<HTMLVideoElement>(null);
  const surfaceRef = useRef<HTMLElement>(null);
  const commands = useRef({ start() {}, pause() {}, resume() {} });
  const [view, setView] = useState<View>(() => ({
    phase: 'loading', time: 0, attempt: createAttempt(content.cues), reason: '',
  }));

  useEffect(() => {
    const video = videoRef.current!;
    const surface = surfaceRef.current!;
    let state: View = { phase: 'loading', time: 0, attempt: createAttempt(content.cues), reason: '' };
    let disposed = false;
    let version = 0;
    let frameId: number | undefined;
    let playRequested = false;
    let playSucceeded = false;
    let firstFrame = false;
    let resumeTimer: ReturnType<typeof setTimeout> | undefined;
    const held = new Set<string>();
    const download = new AbortController();
    let downloading = false;
    let downloadedUrl: string | undefined;
    // Short practice clips are fully buffered before offering a timed attempt.
    const prepared = () => video.readyState >= 4 && Number.isFinite(video.duration)
      && video.buffered.length > 0 && video.buffered.start(0) <= 0.02
      && (downloadedUrl !== undefined || video.buffered.end(0) >= video.duration - 0.02);
    const active = () => state.phase === 'starting' || state.phase === 'running';
    const publish = (next: Partial<View>) => {
      state = { ...state, ...next };
      if (!disposed) setView(state);
    };
    const cancelFrame = () => {
      if (frameId !== undefined) video.cancelVideoFrameCallback(frameId);
      frameId = undefined;
    };
    const cancelResume = () => {
      clearTimeout(resumeTimer);
      resumeTimer = undefined;
    };
    const pause = (reason = '') => {
      if (!active() && state.phase !== 'resuming') return;
      const wasRunning = state.phase === 'running';
      version++;
      cancelFrame();
      cancelResume();
      video.pause();
      publish({ phase: 'paused', time: video.currentTime, reason,
        attempt: wasRunning ? advance(content.cues, state.attempt, video.currentTime) : state.attempt });
      surface.focus({ preventScroll: true });
    };
    const interrupt = (reason: string) => {
      if (!active()) return;
      version++;
      cancelFrame();
      cancelResume();
      publish({ phase: 'interrupted', reason });
      video.pause();
    };
    const maybeRunning = () => {
      if (state.phase === 'starting' && playSucceeded && firstFrame) publish({ phase: 'running' });
    };
    const queueFrame = (token: number) => {
      frameId = video.requestVideoFrameCallback(() => {
        if (disposed || token !== version || !active()) return;
        frameId = undefined;
        firstFrame = true;
        maybeRunning();
        // Read the same playback clock used by input. A delivered frame's PTS can be
        // older than the currently presented frame when callbacks are delayed.
        const time = video.currentTime;
        flushSync(() => publish({
          time,
          attempt: state.phase === 'running' ? advance(content.cues, state.attempt, time) : state.attempt,
        }));
        queueFrame(token);
      });
    };
    const playWhenReady = () => {
      if (state.phase !== 'starting' || playRequested || video.seeking || !prepared()) return;
      playRequested = true;
      const token = version;
      queueFrame(token);
      void video.play().then(() => {
        if (disposed || token !== version || !active()) return;
        playSucceeded = true;
        maybeRunning();
      }).catch(() => {
        if (!disposed && token === version) interrupt('영상을 재생하지 못했습니다. 다시 시도해 주세요.');
      });
    };
    const ready = () => {
      if (state.phase === 'loading' && prepared()) publish({ phase: 'ready' });
      // Browsers may stop preload=auto before the end even on a short clip.
      // Finish the download explicitly instead of waiting for a progress event
      // that will never arrive. Blob media needs no network during the attempt.
      if (state.phase === 'loading' && !downloading && !downloadedUrl
        && video.networkState === 1 && video.readyState >= 2) {
        downloading = true;
        void fetch(video.currentSrc || video.src, { signal: download.signal }).then(async response => {
          if (!response.ok) throw new Error(`Media HTTP ${response.status}`);
          const blob = await response.blob();
          if (disposed || state.phase !== 'loading') return;
          downloadedUrl = URL.createObjectURL(blob);
          video.src = downloadedUrl;
        }).catch(() => { if (!disposed) error(); });
      }
      playWhenReady();
    };
    const resume = () => {
      if (disposed || state.phase !== 'paused' || document.hidden) return;
      const token = ++version;
      playRequested = false;
      playSucceeded = false;
      firstFrame = false;
      publish({ phase: 'resuming', reason: '' });
      surface.focus({ preventScroll: true });
      resumeTimer = setTimeout(() => {
        resumeTimer = undefined;
        if (disposed || token !== version || state.phase !== 'resuming') return;
        publish({ phase: 'starting' });
        playWhenReady();
      }, 1000);
    };
    const start = () => {
      if (disposed || active() || state.phase === 'loading' || state.phase === 'error' || document.hidden) return;
      version++;
      cancelFrame();
      cancelResume();
      video.pause();
      playRequested = false;
      playSucceeded = false;
      firstFrame = false;
      publish({ phase: 'starting', time: 0, attempt: createAttempt(content.cues), reason: '' });
      surface.focus({ preventScroll: true });
      // Avoid manufacturing a seek/waiting cycle on the already prepared first frame.
      if (video.currentTime !== 0) video.currentTime = 0;
      playWhenReady();
    };
    const ended = () => {
      if (!active()) return;
      version++;
      cancelFrame();
      publish({ phase: 'finished', time: video.duration, attempt: finish(content.cues, state.attempt) });
    };
    const error = () => {
      version++;
      cancelFrame();
      cancelResume();
      video.pause();
      publish({ phase: 'error', reason: '영상을 읽지 못했습니다. 새로고침 후 다시 시도해 주세요.' });
    };
    const waiting = () => {
      if (state.phase === 'running') interrupt('영상 로딩으로 중단했습니다. 이번 시도는 채점하지 않습니다.');
    };
    const blur = () => { held.clear(); pause('창을 벗어나 일시정지했습니다. 준비되면 이어서 하세요.'); };
    const visibility = () => { if (document.hidden) blur(); };
    const uiTarget = (target: EventTarget | null) => !(target instanceof Element)
      || !surface.contains(target) || Boolean(target.closest('button, a, input, select, textarea, [contenteditable="true"]'));
    const input = (key: PracticeKey) => {
      if (state.phase !== 'running') return;
      // Preparation dodges describe the recorded route only. They cannot prove a
      // spatial dodge, so do not turn their RMB into either a success or an extra.
      if (key === 'MouseRight' && content.preparation && video.currentTime < content.preparation.end) return;
      publish({ attempt: press(content.cues, state.attempt, key, video.currentTime) });
    };
    const keyDown = (event: KeyboardEvent) => {
      const down = held.has(event.code);
      held.add(event.code);
      if (event.code === 'Escape' && !event.ctrlKey && !event.metaKey && !event.altKey
        && !(event.target instanceof Element && event.target.closest('input, select, textarea, [contenteditable="true"]'))) {
        if (active() || state.phase === 'paused' || state.phase === 'resuming') {
          event.preventDefault();
          if (!down && !event.repeat) {
            if (state.phase === 'paused') resume();
            else pause();
          }
        }
        return;
      }
      if (event.code !== 'Space' || uiTarget(event.target) || event.ctrlKey || event.metaKey || event.altKey) return;
      if (state.phase === 'running' || state.phase === 'paused' || state.phase === 'resuming') {
        event.preventDefault();
        if (!down && !event.repeat) input('Space');
      }
    };
    const keyUp = (event: KeyboardEvent) => { held.delete(event.code); };
    const mouseDown = (event: MouseEvent) => {
      const code = `mouse:${event.button}`;
      const down = held.has(code);
      held.add(code);
      if (event.button !== 2 || down || uiTarget(event.target) || event.ctrlKey || event.metaKey || event.altKey) return;
      surface.focus({ preventScroll: true });
      if (state.phase === 'running') {
        event.preventDefault();
        input('MouseRight');
      }
    };
    const mouseUp = (event: MouseEvent) => { held.delete(`mouse:${event.button}`); };
    const contextMenu = (event: Event) => { event.preventDefault(); };
    const events: [string, EventListener][] = [
      ['canplay', ready], ['canplaythrough', ready], ['progress', ready], ['suspend', ready], ['seeked', playWhenReady],
      ['ended', ended], ['error', error], ['waiting', waiting],
    ];
    events.forEach(([name, listener]) => video.addEventListener(name, listener));
    window.addEventListener('keydown', keyDown);
    window.addEventListener('keyup', keyUp);
    window.addEventListener('mousedown', mouseDown);
    window.addEventListener('mouseup', mouseUp);
    window.addEventListener('blur', blur);
    document.addEventListener('visibilitychange', visibility);
    surface.addEventListener('contextmenu', contextMenu);
    commands.current = { start, pause, resume };
    if (typeof video.requestVideoFrameCallback !== 'function') {
      publish({ phase: 'error', reason: '이 브라우저는 영상 프레임 동기화를 지원하지 않습니다. 최신 Chromium 브라우저를 사용해 주세요.' });
    } else if (video.error) error();
    else { publish({}); ready(); }
    return () => {
      disposed = true;
      download.abort();
      if (downloadedUrl) URL.revokeObjectURL(downloadedUrl);
      version++;
      cancelFrame();
      cancelResume();
      video.pause();
      events.forEach(([name, listener]) => video.removeEventListener(name, listener));
      window.removeEventListener('keydown', keyDown);
      window.removeEventListener('keyup', keyUp);
      window.removeEventListener('mousedown', mouseDown);
      window.removeEventListener('mouseup', mouseUp);
      window.removeEventListener('blur', blur);
      document.removeEventListener('visibilitychange', visibility);
      surface.removeEventListener('contextmenu', contextMenu);
      commands.current = { start() {}, pause() {}, resume() {} };
    };
  }, [content]);

  return { ...view, videoRef, surfaceRef, start: () => commands.current.start(), pause: () => commands.current.pause(), resume: () => commands.current.resume() };
}
