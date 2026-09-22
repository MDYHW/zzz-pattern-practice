import { useLayoutEffect, useMemo, useRef, useState } from 'react';
import { skills, vesperExecute02, withEntryKey, withMirageLastResponse } from './content/skills';
import { Practice } from './practice/Practice';
import type { PracticeKey } from './practice/judge';

const entryStorageKey = 'zzz-entry-choices';
const mirageLayoutStorageKey = 'zzz-mirage-lane-layout';
type LaneLayout = 'selected' | 'overlap';
function readMirageLayout(): LaneLayout {
  try { return localStorage.getItem(mirageLayoutStorageKey) === 'overlap' ? 'overlap' : 'selected'; }
  catch { return 'selected'; }
}
const mirageLastKeyStorageKey = 'zzz-mirage-last-key';
function readMirageLastKey(): PracticeKey {
  try { return localStorage.getItem(mirageLastKeyStorageKey) === 'MouseRight' ? 'MouseRight' : 'Space'; }
  catch { return 'Space'; }
}
function readEntryChoices(): Record<string, PracticeKey> {
  try {
    const stored: unknown = JSON.parse(localStorage.getItem(entryStorageKey) ?? '{}');
    if (!stored || typeof stored !== 'object') return {};
    return Object.fromEntries(Object.entries(stored).filter(([id, key]) =>
      skills.some(skill => skill.id === id && skill.entryOptions?.some(option => option.key === key)))) as Record<string, PracticeKey>;
  } catch { return {}; }
}

export function App() {
  const [selected, setSelected] = useState(vesperExecute02.id);
  const [entryChoices, setEntryChoices] = useState(readEntryChoices);
  const [mirageLayout, setMirageLayout] = useState(readMirageLayout);
  const [mirageLastKey, setMirageLastKey] = useState(readMirageLastKey);
  const [preparation, setPreparation] = useState(0);
  const [display, setDisplay] = useState({ guide: true, hideRecordedInput: true, hideIcons: false });
  const baseContent = skills.find(skill => skill.id === selected)!;
  const availableEntries = baseContent.entryOptions ?? [];
  const entryKey = availableEntries.find(option => option.key === entryChoices[selected])?.key
    ?? availableEntries[0]?.key ?? baseContent.cues[0].key;
  const hasLaneChoice = baseContent.id === 'mirage-archer-unit-attack-09';
  const laneLayout = hasLaneChoice ? mirageLayout : 'selected';
  const content = useMemo(() => hasLaneChoice ? withMirageLastResponse(laneLayout, mirageLastKey)
    : baseContent.entryOptions?.length ? withEntryKey(baseContent, entryKey) : baseContent,
    [baseContent, entryKey, hasLaneChoice, laneLayout, mirageLastKey]);
  const mountKey = `${content.id}:${entryKey}:${laneLayout}:${hasLaneChoice ? mirageLastKey : ''}:${preparation}`;
  const chooseLastKey = (key: PracticeKey) => {
    setMirageLastKey(key);
    try { localStorage.setItem(mirageLastKeyStorageKey, key); } catch { /* Storage is optional. */ }
  };
  const chooseLayout = (layout: LaneLayout) => {
    setMirageLayout(layout);
    try { localStorage.setItem(mirageLayoutStorageKey, layout); } catch { /* Storage is optional. */ }
  };
  const chooseEntry = (key: PracticeKey) => {
    if (!availableEntries.some(option => option.key === key)) return;
    const next = { ...entryChoices, [selected]: key };
    setEntryChoices(next);
    try { localStorage.setItem(entryStorageKey, JSON.stringify(next)); } catch { /* Storage is optional. */ }
  };
  const scrollRef = useRef<HTMLDivElement>(null);
  const initialized = useRef(false);
  const headerHeight = useRef(0);

  useLayoutEffect(() => {
    const scroll = scrollRef.current!;
    const header = scroll.querySelector<HTMLElement>('.masthead')!;
    const align = () => {
      const height = header.getBoundingClientRect().height;
      const atPractice = Math.abs(scroll.scrollTop - headerHeight.current) < 2;
      if (!initialized.current) scroll.scrollTop = height;
      else if (atPractice && headerHeight.current > 0) scroll.scrollTop = height;
      headerHeight.current = height;
      initialized.current = true;
    };
    align();
    let lastStoppedWheel = -Infinity;
    const wheel = (event: WheelEvent) => {
      if (event.ctrlKey || Math.abs(event.deltaX) > Math.abs(event.deltaY)
        || (event.target instanceof Element && event.target.closest('select, input, textarea'))) return;
      if (event.deltaY <= 0) { lastStoppedWheel = -Infinity; return; }
      const now = performance.now();
      // Treat a continuous trackpad/wheel burst as one gesture. Its inertia
      // must not carry the viewport past the header-only stop.
      if (now - lastStoppedWheel < 140) {
        event.preventDefault(); lastStoppedWheel = now; return;
      }
      const delta = event.deltaY * (event.deltaMode === 1 ? 16 : event.deltaMode === 2 ? scroll.clientHeight : 1);
      if (scroll.scrollTop < headerHeight.current - 1 && scroll.scrollTop + delta >= headerHeight.current) {
        event.preventDefault();
        scroll.scrollTop = headerHeight.current;
        lastStoppedWheel = now;
      }
    };
    scroll.addEventListener('wheel', wheel, { passive: false });
    // Keep the hidden-header stop aligned when responsive controls wrap.
    const observer = new ResizeObserver(align);
    observer.observe(header);
    return () => { observer.disconnect(); scroll.removeEventListener('wheel', wheel); };
  }, [mountKey]);

  return <div className="practice-scroll" ref={scrollRef} aria-label="연습 화면 스크롤">
    <Practice key={mountKey} content={content} choices={skills} onSelect={setSelected}
      onEntryChange={chooseEntry} onPrepareEntry={() => setPreparation(value => value + 1)}
      laneLayout={laneLayout} onLaneLayoutChange={hasLaneChoice ? chooseLayout : undefined}
      onLastKeyChange={hasLaneChoice ? chooseLastKey : undefined}
      display={display} onDisplayChange={setDisplay} />
  </div>;
}
