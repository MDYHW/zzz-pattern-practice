import { cueEnvelope, cueWindows, type Attempt, type Cue } from './judge';
import type { PracticeContent } from './session';

export const GUIDE_SECONDS = 3;

export function ActionIcon({ action }: { action: 'dodge' | 'assist' }) {
  return <img className="action-icon" src={`${import.meta.env.BASE_URL}icons/zzz-${action}.png`} alt="" width={23} height={23} />;
}

export function RhythmGuide({ cues, attempt, time, preparation, hideIcons = false, layout = 'selected' }: {
  cues: readonly Cue[]; attempt: Attempt; time: number; preparation?: PracticeContent['preparation']; hideIcons?: boolean;
  layout?: 'selected' | 'overlap';
}) {
  // Motion follows the video clock, so pausing also freezes the feedback.
  const inputAge = attempt.lastInputTime === undefined ? Infinity : Math.max(0, time - attempt.lastInputTime);
  const inputSuccess = attempt.lastInputTime !== undefined && attempt.results.some(result => result.inputTime === attempt.lastInputTime);
  const timelineLabel = preparation
    ? '준비 회피와 이동은 영상 경로 안내이며 채점하지 않습니다. 타일이 왼쪽으로 이동합니다. 고정된 입력선과 겹치는 동안 해당 키를 한 번 누르세요'
    : '타일이 왼쪽으로 이동합니다. 고정된 입력선과 겹치는 동안 해당 키를 한 번 누르세요';
  return <div className="guide"
    aria-label="리듬 안내" data-time={time.toFixed(6)}>
    <div className="timeline" role="img" aria-label={timelineLabel}>
      {preparation?.movements.map((movement, index) => <div key={`${movement.key}-${movement.start}-${index}`}
        className="preparation-movement" data-movement-key={movement.key}
        style={{ left: `${20 + (movement.start - time) / GUIDE_SECONDS * 100}%`, width: `${(movement.end - movement.start) / GUIDE_SECONDS * 100}%` }}>
        <kbd>{movement.key}</kbd>
      </div>)}
      {preparation?.dodges.map(dodge => <div key={dodge.id} className="preparation-dodge" data-preparation-id={dodge.id}
        style={{ left: `${20 + (dodge.time - time) / GUIDE_SECONDS * 100}%` }}>
        <span className="preparation-label">{dodge.label}</span><kbd>우클릭</kbd><i aria-hidden="true" />
      </div>)}
      {cues.map((cue, index) => {
        const result = attempt.results[index];
        const windows = cueWindows(cue);
        const bounds = cueEnvelope(cue);
        const dual = !!cue.alternateWindow;
        const baseTile = layout === 'overlap' && (cue.key === 'Space' || dual);
        const current = windows.some(window => time >= window.start && time <= window.end) && result.status === 'pending';
        const age = result.status === 'success' && result.inputTime !== undefined
          ? Math.max(0, time - result.inputTime) : current ? time - bounds.start : Infinity;
        const lift = age < .22 ? Math.sin(age / .22 * Math.PI) * (result.status === 'success' ? 3 : 2) : 0;
        return <div key={cue.id} data-cue-id={cue.id} className={`cue cue-${result.status}${current ? ' cue-current' : ''}${baseTile ? ' cue-base-tile' : ''}${hideIcons ? ' cue-hide-icon' : ''}`}
          style={{ left: `${20 + (bounds.start - time) / GUIDE_SECONDS * 100}%`, width: `${(bounds.end - bounds.start) / GUIDE_SECONDS * 100}%` }}>
          <span>{dual || cue.alternateKey ? 'Space 또는 우클릭' : cue.key === 'MouseRight' ? '우클릭' : 'Space'}</span>
          {baseTile && [...windows].sort((a, b) => Number(a.key === 'MouseRight') - Number(b.key === 'MouseRight')).map(window => <div key={window.key} data-window-key={window.key}
            className={`cue-window ${window.key === 'Space' ? 'cue-window-base' : 'cue-window-hatched'}`}
            style={{ left: `${(window.start - bounds.start) / (bounds.end - bounds.start || 1) * 100}%`, width: `${(window.end - window.start) / (bounds.end - bounds.start || 1) * 100}%` }}
            aria-hidden="true" />)}
          <i aria-hidden="true">{!hideIcons && <span className={`cue-symbol${cue.alternateKey ? ' cue-symbol-dual' : ''}`} style={{ translate: `0 ${-lift}px` }}>
            {(cue.alternateKey ? ['Space', 'MouseRight'] : [cue.key]).map(key => <span className="cue-choice" data-key={key} key={key}>
              <span className="cue-choice-icon"><ActionIcon action={key === 'MouseRight' ? 'dodge' : 'assist'} /></span>
            </span>)}
          </span>}</i>
        </div>;
      })}
      <div className="playhead" aria-hidden="true" />
      {inputAge < .24 && <div className={`input-pulse ${inputSuccess ? 'input-pulse-success' : 'input-pulse-extra'}`} aria-hidden="true"
        style={{ opacity: (1 - inputAge / .24) * .75, scale: 1 + inputAge / .24 * .45 }} />}
    </div>
    {preparation && <p className="preparation-note">준비·이동은 영상 경로 안내, 결과는 진입부터</p>}
  </div>;
}
