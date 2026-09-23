import { useEffect, useRef, useState, type RefObject } from 'react';
import type { PracticeKey } from './judge';
import { ActionIcon } from './RhythmGuide';
import './touch-controls.css';

// The six service recordings share a 1280×720 HUD. Coordinates include the
// cooldown ring, but exclude the keyboard labels below each icon.
const controls = [
  { key: 'MouseRight', action: 'dodge', label: '회피', x: 1043, y: 645 },
  { key: 'Space', action: 'assist', label: '지원', x: 1185, y: 645 },
] as const;

export function useTouchLayout() {
  const [enabled, setEnabled] = useState(() => new URLSearchParams(location.search).get('mobile') === '1'
    || window.matchMedia?.('(any-pointer: coarse)').matches === true);
  useEffect(() => {
    const query = window.matchMedia?.('(any-pointer: coarse)');
    if (!query) return;
    const update = () => setEnabled(new URLSearchParams(location.search).get('mobile') === '1' || query.matches);
    query.addEventListener('change', update);
    return () => query.removeEventListener('change', update);
  }, []);
  return enabled;
}

export function TouchControls({ videoRef, fixed, enabled, input }: {
  videoRef: RefObject<HTMLVideoElement | null>; fixed: boolean; enabled: boolean;
  input: (key: PracticeKey) => void;
}) {
  const canvases = useRef<(HTMLCanvasElement | null)[]>([]);
  const contacts = useRef(new Map<number, PracticeKey>());
  const [pressed, setPressed] = useState<PracticeKey[]>([]);
  useEffect(() => {
    const clear = () => { contacts.current.clear(); setPressed([]); };
    clear();
    window.addEventListener('blur', clear);
    document.addEventListener('visibilitychange', clear);
    return () => { window.removeEventListener('blur', clear); document.removeEventListener('visibilitychange', clear); };
  }, [enabled]);
  useEffect(() => {
    if (fixed) return;
    const video = videoRef.current!;
    let frame: number | undefined;
    const draw = () => {
      if (video.readyState < 2 || !video.videoWidth) return;
      const scaleX = video.videoWidth / 1280;
      const scaleY = video.videoHeight / 720;
      controls.forEach((control, i) => {
        const canvas = canvases.current[i];
        canvas?.getContext('2d')?.drawImage(video,
          (control.x - 38) * scaleX, (control.y - 38) * scaleY, 76 * scaleX, 76 * scaleY,
          0, 0, canvas.width, canvas.height);
      });
    };
    const next = () => { draw(); frame = video.requestVideoFrameCallback(next); };
    draw();
    if (typeof video.requestVideoFrameCallback === 'function') frame = video.requestVideoFrameCallback(next);
    video.addEventListener('loadeddata', draw);
    video.addEventListener('seeked', draw);
    return () => {
      if (frame !== undefined) video.cancelVideoFrameCallback(frame);
      video.removeEventListener('loadeddata', draw); video.removeEventListener('seeked', draw);
    };
  }, [fixed, videoRef]);
  const release = (id: number) => {
    contacts.current.delete(id);
    setPressed([...contacts.current.values()]);
  };
  return <div className="touch-controls" role="group" aria-label="터치 입력" data-icon-mode={fixed ? 'fixed' : 'video'}>
    {controls.map((control, i) => <button key={control.key} type="button"
      className={`touch-action${pressed.includes(control.key) ? ' is-pressed' : ''}`}
      aria-label={control.label} disabled={!enabled}
      onContextMenu={event => event.preventDefault()}
      onPointerDown={event => {
        if (!enabled || event.button !== 0 || contacts.current.has(event.pointerId)) return;
        event.preventDefault();
        const alreadyHeld = [...contacts.current.values()].includes(control.key);
        contacts.current.set(event.pointerId, control.key);
        event.currentTarget.setPointerCapture(event.pointerId);
        setPressed([...contacts.current.values()]);
        if (!alreadyHeld) input(control.key);
      }}
      onPointerUp={event => release(event.pointerId)} onPointerCancel={event => release(event.pointerId)}
      onLostPointerCapture={event => release(event.pointerId)}
      onKeyDown={event => { if (event.repeat && (event.key === ' ' || event.key === 'Enter')) event.preventDefault(); }}
      onClick={event => { if (event.detail === 0) input(control.key); }}>
      <ActionIcon action={control.action} />
      {!fixed && <canvas ref={node => { canvases.current[i] = node; }} width={152} height={152} aria-hidden="true" />}
    </button>)}
  </div>;
}
