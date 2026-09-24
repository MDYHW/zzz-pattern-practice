import { useCallback, useLayoutEffect, useRef } from 'react';
import { usePracticeSession } from './session';
import { FloatingHeader } from './FloatingHeader';
import './practice-layout.css';
import type { PracticeContent, Phase } from './session';
import { ActionIcon, RhythmGuide } from './RhythmGuide';
import './rhythm-guide.css';
import type { PracticeKey } from './judge';
import { Results } from './Results';
import { TouchControls, useTouchLayout } from './TouchControls';

const phaseLabels: Record<Phase, string> = {
  loading: '영상 준비 중', ready: '시작을 기다리고 있어요', starting: '재생 준비 중',
  running: '연습 중', finished: '연습 완료', interrupted: '연습 중단', error: '영상을 확인해 주세요',
  paused: '일시정지', resuming: '1초 후 이어서 합니다',
};
const feedbackLabels = {
  none: '타이밍에 맞춰 한 번씩 눌러 주세요.', outside: '',
  wrong: '',
  success: '대응 성공', miss: '대응 놓침 · 다음 대응을 계속하세요.',
};
const clock = (seconds: number) => `${Math.floor(seconds / 60).toString().padStart(2, '0')}:${Math.floor(seconds % 60).toString().padStart(2, '0')}`;

function ControlIcon({ kind }: { kind: 'play' | 'pause' | 'restart' }) {
  return <svg viewBox="0 0 24 24" aria-hidden="true" focusable="false">
    {kind === 'play' ? <path d="M8 4 21 12 8 20Z" fill="currentColor" />
      : kind === 'pause' ? <path d="M6 4h4v16H6zM14 4h4v16h-4z" fill="currentColor" />
        : <path d="M4 10a8 8 0 1 1 1 7M4 4v6h6" fill="none" stroke="currentColor" strokeWidth="2.3" strokeLinecap="round" strokeLinejoin="round" />}
  </svg>;
}

export function Practice({ content, choices = [content], onSelect, onEntryChange, onPrepareEntry, display, onDisplayChange, laneLayout = 'selected', onLaneLayoutChange, onLastKeyChange, floatingMenuOpen, onFloatingMenuChange }: {
  floatingMenuOpen: boolean; onFloatingMenuChange: (open: boolean) => void;
  content: PracticeContent; choices?: readonly PracticeContent[]; onSelect?: (id: string) => void;
  onEntryChange: (key: PracticeKey) => void; onPrepareEntry: () => void;
  display: { guide: boolean; hideRecordedInput: boolean; hideGameInput: boolean; hideIcons: boolean };
  onDisplayChange: (value: { guide: boolean; hideRecordedInput: boolean; hideGameInput: boolean; hideIcons: boolean }) => void;
  laneLayout?: 'selected' | 'overlap'; onLaneLayoutChange?: (layout: 'selected' | 'overlap') => void;
  onLastKeyChange?: (key: PracticeKey) => void;
}) {
  const session = usePracticeSession(content);
  const touch = useTouchLayout();
  const pauseRef = useRef(session.pause);
  pauseRef.current = session.pause;
  const setMenuOpen = useCallback((open: boolean) => {
    if (touch && open) pauseRef.current();
    onFloatingMenuChange(open);
  }, [touch, onFloatingMenuChange]);
  const practiceRef = useRef<HTMLElement>(null);
  const { guide, hideRecordedInput, hideGameInput } = display;
  const active = session.phase === 'running' || session.phase === 'starting' || session.phase === 'resuming';
  const paused = session.phase === 'paused';
  const finished = session.phase === 'finished';
  const canChooseEntry = session.phase === 'ready' || session.phase === 'loading' || session.phase === 'error';
  const entryName = content.cues[0].key === 'Space' ? 'Space' : '우클릭';
  const hasEntryChoice = (content.entryOptions?.length ?? 0) > 1;
  const lastName = content.cues.at(-1)!.key === 'MouseRight' ? '우클릭' : 'Space';
  const responseSummary = onLastKeyChange
    ? `진입은 우클릭, 1~3타는 Space, 마지막은 ${laneLayout === 'overlap' ? 'Space 또는 우클릭' : lastName}입니다.` : undefined;
  const hasPreparationMovement = !!content.preparation?.movements.length;
  const preparationGuide = content.preparation && <>준비 회피 {content.preparation.dodges.length}회{hasPreparationMovement ? '와 이동은' : '는'} 영상 경로 안내이며, 진입부터 {content.cues.length - 1}타까지 채점합니다.</>;
  const bossId = content.bossId ?? 'vesper';
  const bossLabel = content.bossLabel ?? '베스퍼';
  const bosses = [...new Map(choices.map(choice => [choice.bossId ?? 'vesper', choice.bossLabel ?? '베스퍼'])).entries()];
  const bossSkills = choices.filter(choice => (choice.bossId ?? 'vesper') === bossId);
  const successes = session.attempt.results.filter(result => result.status === 'success').length;
  const complete = successes === content.cues.length;
  const status = session.reason || (finished ? (complete ? '전체 대응 성공' : '놓친 대응을 확인하고 다시 연습해 보세요.')
    : session.phase === 'running' ? feedbackLabels[session.attempt.feedback] : phaseLabels[session.phase]);
  const toggleGuide = () => {
    onDisplayChange({ ...display, guide: !guide });
    if (active) session.surfaceRef.current?.focus({ preventScroll: true });
  };

  useLayoutEffect(() => {
    const main = practiceRef.current!;
    const surface = session.surfaceRef.current!;
    const measure = () => main.style.setProperty('--surface-height', `${surface.offsetHeight}px`);
    const observer = new ResizeObserver(measure);
    observer.observe(surface);
    measure();
    return () => observer.disconnect();
  }, [session.surfaceRef]);

  useLayoutEffect(() => {
    const main = practiceRef.current!;
    const rhythm = main.querySelector<HTMLElement>('.guide');
    if (!rhythm) return;
    const measure = () => main.style.setProperty('--guide-height', `${rhythm.offsetHeight}px`);
    const observer = new ResizeObserver(measure);
    observer.observe(rhythm);
    measure();
    return () => observer.disconnect();
  }, [guide, finished]);

  useLayoutEffect(() => {
    const main = practiceRef.current!;
    const result = main.querySelector<HTMLElement>('.practice-workspace > .results');
    if (!result) return;
    const measure = () => {
      const chart = result.querySelector<HTMLElement>('.result-scroll')!;
      const baseHeight = chart.getBoundingClientRect().bottom - result.getBoundingClientRect().top
        + parseFloat(getComputedStyle(result).paddingBottom);
      main.style.setProperty('--result-base-height', `${baseHeight}px`);
    };
    const observer = new ResizeObserver(measure);
    observer.observe(result);
    measure();
    return () => observer.disconnect();
  }, [finished]);

  return (
    <main ref={practiceRef} className={`practice practice-layout${guide ? '' : ' expanded'}${touch ? ' touch-layout' : ''}`}>
      <FloatingHeader touch={touch} open={floatingMenuOpen} setOpen={setMenuOpen}>
      <header className="masthead">
        <h1>패턴 연습<span> / ZZZ</span></h1>
        <div className="toolbar">
          <div className="selection">
            <label>보스<select aria-label="보스" value={bossId} disabled={active} onChange={event => {
              const firstSkill = choices.find(choice => (choice.bossId ?? 'vesper') === event.target.value);
              if (firstSkill) onSelect?.(firstSkill.id);
            }}>{bosses.map(([id, label]) => <option key={id} value={id}>{label}</option>)}</select></label>
            <label>제어스킬<select aria-label="제어스킬" value={content.id} disabled={active} onChange={event => onSelect?.(event.target.value)}>{bossSkills.map(choice => <option key={choice.id} value={choice.id}>{choice.title}</option>)}</select></label>
          </div>
          <div className="display-options">
            {onLaneLayoutChange && <label className="lane-choice">tile type<select aria-label="tile type" value={laneLayout} disabled={active}
              onChange={event => { if (!active) onLaneLayoutChange(event.target.value as 'selected' | 'overlap'); }}>
              <option value="selected">A · 대응 선택</option><option value="overlap">B · 겹침 레인</option>
            </select></label>}
            {onLaneLayoutChange && laneLayout === 'overlap' && <button className="quiet" aria-label="레인 아이콘 표시" aria-pressed={!display.hideIcons}
              onClick={() => { onDisplayChange({ ...display, hideIcons: !display.hideIcons }); if (active) session.surfaceRef.current?.focus({ preventScroll: true }); }}>
              레인 아이콘 <span>{display.hideIcons ? 'OFF' : 'ON'}</span></button>}
            <button className="quiet" aria-label="리듬 안내 표시" aria-pressed={guide} onClick={toggleGuide}>리듬 안내 <span>{guide ? 'ON' : 'OFF'}</span></button>
            {!!content.recordedInputMasks?.length && <button className="quiet" aria-pressed={hideRecordedInput} onClick={() => {
              onDisplayChange({ ...display, hideRecordedInput: !hideRecordedInput });
              if (active) session.surfaceRef.current?.focus({ preventScroll: true });
            }}>녹화 입력 가리기 <span>{hideRecordedInput ? 'ON' : 'OFF'}</span></button>}
            {!!content.gameInputMasks?.length && <button className="quiet" aria-pressed={hideGameInput} onClick={() => {
              onDisplayChange({ ...display, hideGameInput: !hideGameInput });
              if (active) session.surfaceRef.current?.focus({ preventScroll: true });
            }}>인게임 입력 가리기 <span>{hideGameInput ? 'ON' : 'OFF'}</span></button>}
          </div>
        </div>
      </header>
      </FloatingHeader>
      <div className={`practice-workspace${finished ? ' is-finished' : ''}`}>
      <section className="practice-surface" ref={session.surfaceRef} tabIndex={0} aria-label="연습 입력 영역" aria-describedby="input-summary"
        data-phase={session.phase} data-last-input-time={session.attempt.lastInputTime}>
        <div className="video-with-control">
        <div className="video-stage">
          <video ref={session.videoRef} src={`${import.meta.env.BASE_URL}${content.video}`} poster={`${import.meta.env.BASE_URL}${content.poster}`}
            preload="auto" playsInline disablePictureInPicture aria-label="성공 대응 참고 영상" />
          {hideRecordedInput && content.recordedInputMasks?.map((mask, index) => <div key={index} className="recorded-input-mask" aria-hidden="true"
            style={{ left: `${mask.x}%`, top: `${mask.y}%`, width: `${mask.width}%`, height: `${mask.height}%` }} />)}
          {(hideGameInput || touch) && content.gameInputMasks?.map((mask, index) => <div key={index} className="game-input-mask" aria-hidden="true"
            style={{ left: `${mask.x}%`, top: `${mask.y}%`, width: `${mask.width}%`, height: `${mask.height}%` }} />)}
          <span className="video-label">{onLastKeyChange
            ? `참고 영상: 우클릭 진입 · 마지막 ${content.recordedFinalKey === 'MouseRight' ? '우클릭 → QTE' : 'Space'}`
            : <>영상: {content.preparation && (hasPreparationMovement ? '준비 회피·이동 → ' : '준비 회피 → ')}우클릭 진입 → 패링 · 내 진입: {entryName}</>}</span>
          <span className="video-time" aria-label="영상 진행 시간">{clock(session.time)} <span>/ {clock(content.duration)}</span></span>
          {session.phase === 'running' && <button className="video-pause icon-button" aria-label="일시정지" title="일시정지 (ESC)" aria-keyshortcuts="Escape" onClick={session.pause}><ControlIcon kind="pause" /></button>}
          {touch && !['finished', 'interrupted', 'error'].includes(session.phase) && <TouchControls
            videoRef={session.videoRef} fixed={hideGameInput} enabled={session.phase === 'running'} input={session.input} />}
          {session.phase !== 'running' && <div className="video-cover" role="group" aria-label="영상 조작">
            {session.phase !== 'ready' && <p className="cover-title">{session.phase === 'resuming' ? '1' : phaseLabels[session.phase]}</p>}
            {session.reason && <p className="cover-note">{session.reason}</p>}
            <div className="cover-actions">
              {paused ? <>
                <button className="primary icon-button" aria-label="이어서 하기" title="이어서 하기 (ESC)" aria-keyshortcuts="Escape" onClick={session.resume}><ControlIcon kind="play" /></button>
                <button className="secondary icon-button" aria-label="처음부터" title="처음부터" onClick={session.start}><ControlIcon kind="restart" /></button>
              </> : session.phase === 'error' ? <button className="primary icon-button" aria-label="새로고침" title="새로고침" onClick={() => window.location.reload()}><ControlIcon kind="restart" /></button>
                : !active && <button className="primary icon-button" aria-label={finished || session.phase === 'interrupted' ? '다시 연습' : '연습 시작'} title={finished || session.phase === 'interrupted' ? '다시 연습' : '연습 시작'} disabled={session.phase === 'loading'} onClick={session.start}>
                  <ControlIcon kind={finished || session.phase === 'interrupted' ? 'restart' : 'play'} />
                </button>}
            </div>
            <div className="entry-selection">
              {onLastKeyChange && laneLayout === 'selected' && <label>마지막 대응<select aria-label="마지막 대응" value={content.cues.at(-1)!.key} disabled={!canChooseEntry}
                onChange={event => { if (canChooseEntry) onLastKeyChange(event.target.value as PracticeKey); }}>
                <option value="Space">Space</option><option value="MouseRight">우클릭</option>
              </select></label>}
              {hasEntryChoice ? <><label>진입 입력 <select aria-label="진입 입력" value={content.cues[0].key} disabled={!canChooseEntry}
                onChange={event => { if (canChooseEntry) onEntryChange(event.target.value as PracticeKey); }}>
                {content.entryOptions?.map(option => <option key={option.key} value={option.key}>{option.key === 'Space' ? 'Space' : '우클릭'}</option>)}
              </select></label>
              <p>{preparationGuide ?? <>선택한 입력의 구간으로 연습합니다. 이후 대응은 Space입니다.</>}</p></> : <p>{responseSummary ?? preparationGuide ?? <>진입은 {entryName}, 이후 대응은 Space입니다.</>}</p>}
              {(hasEntryChoice || onLaneLayoutChange) && (finished || session.phase === 'interrupted') && <button className="quiet" onClick={onPrepareEntry}>입력 선택으로</button>}
            </div>
            {paused && <kbd className="cover-shortcut">ESC</kbd>}
          </div>}
        </div>
        {!guide && !finished && <button className="guide-edge-toggle guide-expand" aria-label="리듬 바 펼치기"
          title="리듬 바 펼치기" aria-expanded="false" onClick={toggleGuide}>
          <svg viewBox="0 0 24 24" aria-hidden="true"><path d="m6 9 6 6 6-6" /></svg>
        </button>}
        </div>
        <p className="sr-only" role="status" aria-live="polite">{status}</p>
        <p className="sr-only" id="input-summary">우클릭·Space로 입력하고, ESC로 일시정지하거나 이어서 합니다.</p>
        {guide && !finished && <div className="guide-with-control"><RhythmGuide cues={content.cues} attempt={session.attempt} time={session.time} preparation={content.preparation}
          layout={laneLayout} hideIcons={laneLayout === 'overlap' && display.hideIcons} />
          <button className="guide-edge-toggle guide-collapse" aria-label="리듬 바 접기" title="리듬 바 접기"
            aria-expanded="true" onClick={toggleGuide}>
            <svg viewBox="0 0 24 24" aria-hidden="true"><path d="m6 15 6-6 6 6" /></svg>
          </button>
        </div>}
      </section>
      {finished && <Results content={content} attempt={session.attempt} compact={touch} />}
      </div>
      {onLaneLayoutChange && laneLayout === 'overlap' && <p className="lane-preview-note">Space는 단색, 우클릭은 내부 사선 무늬입니다. 마지막 타는 각 키의 구간에서 하나만 성공하면 됩니다. 영상은 우클릭 대응 예시이며 내 입력에 따라 바뀌지 않습니다.</p>}
      {session.phase === 'interrupted' && <section className="results results-interrupted" aria-labelledby="results-title">
        <h2 id="results-title">중단된 시도</h2><p>이번 시도는 채점하지 않습니다. ↻로 처음부터 다시 연습하세요.</p>
      </section>}
      <details className="help" onToggle={event => { if (event.currentTarget.open && active) session.pause(); }}>
        <summary>조작 안내</summary>
        <div id="input-help">
          <p>공개 사이트는 Cloudflare Web Analytics로 방문 및 화면 성능 통계를 수집합니다. 보스별 연습 기록과 입력·채점 결과는 전송하지 않습니다. <a href="https://www.cloudflare.com/web-analytics/">통계 서비스 안내</a></p>
          <p><ActionIcon action="dodge" /><kbd>우클릭</kbd> <span className="separator">/</span> <ActionIcon action="assist" /><kbd>Space</kbd></p>
          <ul>
            <li>{touch ? '터치 버튼은 누르는 순간 입력됩니다. 인게임 입력 가리기를 끄면 녹화 영상의 아이콘 상태를 확대하고, 켜면 고정 아이콘을 표시합니다. 영상의 색·쿨타임 변화는 녹화 당시 상태이며 내 터치 결과가 아닙니다. 내 터치는 바깥 테두리로 표시합니다.' : '인게임 입력 가리기는 영상 오른쪽 아래 입력 아이콘 줄 전체를 하나의 직사각형으로 가립니다. 녹화 입력 가리기와 따로 켜고 끌 수 있습니다.'}</li>
            {onLaneLayoutChange && <li>A는 마지막 대응을 미리 고르고, B는 Space 단색·우클릭 내부 사선 무늬를 함께 표시합니다. 진입은 측정된 우클릭만 제공합니다. 일시정지·종료 상태에서도 상단에서 A/B를 바로 바꿀 수 있으며, 전환하면 새 연습을 처음부터 준비합니다.</li>}
            <li>{responseSummary ?? (hasEntryChoice ? <>진입은 시작 전에 선택한 {entryName}, 이후 대응은 Space를 사용합니다. 선택은 패턴별로 기억합니다.</> : <>진입은 {entryName}, 이후 대응은 Space를 사용합니다.</>)}</li>
            <li>타일의 구간 띠가 고정 입력선과 겹치는 동안 해당 키를 한 번 누르세요. 아이콘 중앙을 맞출 필요는 없습니다.</li>
            <li>키는 놓은 뒤 다시 누르세요. 선택과 다른 키를 눌러도 구간 안에서 다시 대응할 수 있고, Space·우클릭의 추가 입력만 기록합니다.</li>
            <li><kbd>ESC</kbd> 또는 Ⅱ로 일시정지, ▶로 시작·이어서 하기, ↻로 처음부터 연습합니다. 재개 전 1초의 준비 시간이 있습니다.</li>
            <li>{onLastKeyChange ? <>참고 영상은 마지막 대응에 따라 구분합니다. B는 우클릭·QTE 영상을 사용합니다. QTE는 영상만 보여주며 추가로 채점하지 않습니다. </> : <>영상은 우클릭 진입과 패링의 성공 예시입니다. {content.entryOptions?.some(option => option.key === 'Space') && 'Space 진입을 선택하면 영상의 동작과 다를 수 있습니다. '}</>}내 결과는 아이콘 테두리와 타일의 색, 종료 결과로 확인하세요. 안내를 숨겨도 판정은 같습니다.</li>
            <li>현재 {bossLabel} {content.title}의 진입과 {content.cues.length - 1}타를 연습합니다. 연습 구간은 원작의 판정 경계를 재현한 것이 아닙니다.</li>
          </ul>
        </div>
      </details>
    </main>
  );
}
