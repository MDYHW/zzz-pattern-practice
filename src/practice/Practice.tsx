import { usePracticeSession } from './session';
import type { PracticeContent, Phase } from './session';
import { ActionIcon, RhythmGuide } from './RhythmGuide';
import './rhythm-guide.css';
import type { PracticeKey } from './judge';
import { Results } from './Results';

const phaseLabels: Record<Phase, string> = {
  loading: '영상 준비 중', ready: '시작을 기다리고 있어요', starting: '재생 준비 중',
  running: '연습 중', finished: '연습 완료', interrupted: '연습 중단', error: '영상을 확인해 주세요',
  paused: '일시정지', resuming: '1초 후 이어서 합니다',
};
const feedbackLabels = {
  none: '타이밍에 맞춰 한 번씩 눌러 주세요.', outside: '입력 구간 밖 · 다음 구간을 기다려 주세요.',
  wrong: '선택한 대응과 다른 입력 · 선택한 키로 다시 눌러 주세요.',
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

export function Practice({ content, choices = [content], onSelect, onEntryChange, onPrepareEntry, display, onDisplayChange, laneLayout = 'selected', onLaneLayoutChange, onLastKeyChange }: {
  content: PracticeContent; choices?: readonly PracticeContent[]; onSelect?: (id: string) => void;
  onEntryChange: (key: PracticeKey) => void; onPrepareEntry: () => void;
  display: { guide: boolean; hideRecordedInput: boolean; hideGameInput: boolean; hideIcons: boolean };
  onDisplayChange: (value: { guide: boolean; hideRecordedInput: boolean; hideGameInput: boolean; hideIcons: boolean }) => void;
  laneLayout?: 'selected' | 'overlap'; onLaneLayoutChange?: (layout: 'selected' | 'overlap') => void;
  onLastKeyChange?: (key: PracticeKey) => void;
}) {
  const session = usePracticeSession(content);
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
  const preparationGuide = content.preparation && <>준비 회피 {content.preparation.dodges.length}회와 이동은 영상 경로 안내이며, 진입부터 {content.cues.length - 1}타까지 채점합니다.</>;
  const bossId = content.bossId ?? 'vesper';
  const bossLabel = content.bossLabel ?? '베스퍼';
  const bosses = [...new Map(choices.map(choice => [choice.bossId ?? 'vesper', choice.bossLabel ?? '베스퍼'])).entries()];
  const bossSkills = choices.filter(choice => (choice.bossId ?? 'vesper') === bossId);
  const successes = session.attempt.results.filter(result => result.status === 'success').length;
  const complete = successes === content.cues.length;
  const status = session.reason || (finished ? (complete ? '전체 대응 성공' : '놓친 대응을 확인하고 다시 연습해 보세요.')
    : session.phase === 'running' ? feedbackLabels[session.attempt.feedback] : phaseLabels[session.phase]);
  const feedback = session.phase === 'running'
    ? ({ none: '', success: '', miss: '', wrong: '선택과 다른 입력', outside: '구간 밖' }[session.attempt.feedback]) : '';
  const toggleGuide = () => {
    onDisplayChange({ ...display, guide: !guide });
    if (active) session.surfaceRef.current?.focus({ preventScroll: true });
  };

  return (
    <main className={`practice${guide ? '' : ' expanded'}`}>
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
              레인 아이콘 <span>{display.hideIcons ? '꺼짐' : '켜짐'}</span></button>}
            <button className="quiet" aria-label="리듬 안내 표시" aria-pressed={guide} onClick={toggleGuide}>리듬 안내 <span>{guide ? '켜짐' : '꺼짐'}</span></button>
            {!!content.recordedInputMasks?.length && <button className="quiet" aria-pressed={hideRecordedInput} onClick={() => {
              onDisplayChange({ ...display, hideRecordedInput: !hideRecordedInput });
              if (active) session.surfaceRef.current?.focus({ preventScroll: true });
            }}>녹화 입력 가리기 <span>{hideRecordedInput ? '켜짐' : '꺼짐'}</span></button>}
            {!!content.gameInputMasks?.length && <button className="quiet" aria-pressed={hideGameInput} onClick={() => {
              onDisplayChange({ ...display, hideGameInput: !hideGameInput });
              if (active) session.surfaceRef.current?.focus({ preventScroll: true });
            }}>인게임 입력 가리기 <span>{hideGameInput ? '켜짐' : '꺼짐'}</span></button>}
          </div>
        </div>
      </header>
      <section className="practice-surface" ref={session.surfaceRef} tabIndex={0} aria-label="연습 입력 영역" aria-describedby="input-summary"
        data-phase={session.phase} data-last-input-time={session.attempt.lastInputTime}>
        <div className="video-stage">
          <video ref={session.videoRef} src={`${import.meta.env.BASE_URL}${content.video}`} poster={`${import.meta.env.BASE_URL}${content.poster}`}
            preload="auto" playsInline disablePictureInPicture aria-label="성공 대응 참고 영상" />
          {hideRecordedInput && content.recordedInputMasks?.map((mask, index) => <div key={index} className="recorded-input-mask" aria-hidden="true"
            style={{ left: `${mask.x}%`, top: `${mask.y}%`, width: `${mask.width}%`, height: `${mask.height}%` }} />)}
          {hideGameInput && content.gameInputMasks?.map((mask, index) => <div key={index} className="game-input-mask" aria-hidden="true"
            style={{ left: `${mask.x}%`, top: `${mask.y}%`, width: `${mask.width}%`, height: `${mask.height}%` }} />)}
          <span className="video-label">{onLastKeyChange
            ? `참고 영상: 우클릭 진입 · 마지막 ${content.recordedFinalKey === 'MouseRight' ? '우클릭 → QTE' : 'Space'}`
            : <>영상: {content.preparation && '준비 회피·이동 → '}우클릭 진입 → 패링 · 내 진입: {entryName}</>}</span>
          <span className="video-time" aria-label="영상 진행 시간">{clock(session.time)} <span>/ {clock(content.duration)}</span></span>
          {feedback && <div className={`video-progress feedback-${session.attempt.feedback}`}>
            <span className="input-feedback">{feedback}</span>
          </div>}
          {session.phase === 'running' && <button className="video-pause icon-button" aria-label="일시정지" title="일시정지 (ESC)" aria-keyshortcuts="Escape" onClick={session.pause}><ControlIcon kind="pause" /></button>}
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
        <p className="sr-only" role="status" aria-live="polite">{status}</p>
        <p className="sr-only" id="input-summary">우클릭·Space로 입력하고, ESC로 일시정지하거나 이어서 합니다.</p>
        {guide && !finished && <RhythmGuide cues={content.cues} attempt={session.attempt} time={session.time} preparation={content.preparation}
          layout={laneLayout} hideIcons={laneLayout === 'overlap' && display.hideIcons} />}
      </section>
      {onLaneLayoutChange && laneLayout === 'overlap' && <p className="lane-preview-note">Space는 단색, 우클릭은 내부 사선 무늬입니다. 마지막 타는 각 키의 구간에서 하나만 성공하면 됩니다. 영상은 우클릭 대응 예시이며 내 입력에 따라 바뀌지 않습니다.</p>}
      {finished && <Results content={content} attempt={session.attempt} />}
      {session.phase === 'interrupted' && <section className="results results-interrupted" aria-labelledby="results-title">
        <h2 id="results-title">중단된 시도</h2><p>이번 시도는 채점하지 않습니다. ↻로 처음부터 다시 연습하세요.</p>
      </section>}
      <details className="help" onToggle={event => { if (event.currentTarget.open && active) session.pause(); }}>
        <summary>조작 안내</summary>
        <div id="input-help">
          <p><ActionIcon action="dodge" /><kbd>우클릭</kbd> <span className="separator">/</span> <ActionIcon action="assist" /><kbd>Space</kbd></p>
          <ul>
            <li>인게임 입력 가리기는 영상 오른쪽 아래의 Space·우클릭 아이콘과 키 표시를 가립니다. 녹화 입력 가리기와 따로 켜고 끌 수 있습니다.</li>
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
