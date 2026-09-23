import { useEffect, useRef, useState } from 'react';
import { cueWindows, type Attempt, type PracticeKey } from './judge';
import type { PracticeContent } from './session';
import { ActionIcon } from './RhythmGuide';
import { groupExtraInputs, resultTimeline } from './result-timeline';
import './results.css';

const keyLabel = { MouseRight: '우클릭', Space: 'Space' };
const reasonLabel = { wrong: '선택과 다른 입력', outside: '추가 입력', 'after-success': '성공 후 입력' };
const stateLabel = { success: '성공', miss: '놓침', pending: '대기' };
const seconds = (time: number) => `${time.toFixed(2)}초`;

export function Results({ content, attempt, compact = false }: { content: PracticeContent; attempt: Attempt; compact?: boolean }) {
  const rowY = compact ? { MouseRight: 64, Space: 110 } : { MouseRight: 88, Space: 152 };
  const [selection, setSelection] = useState<{ kind: 'cue'; index: number } | { kind: 'extra'; first: number } | null>(null);
  const [width, setWidth] = useState(600);
  const trackRef = useRef<HTMLDivElement>(null);
  useEffect(() => {
    const track = trackRef.current!;
    const observer = new ResizeObserver(() => setWidth(track.clientWidth));
    observer.observe(track); setWidth(track.clientWidth);
    return () => observer.disconnect();
  }, []);
  const successes = attempt.results.filter(result => result.status === 'success').length;
  const timeline = resultTimeline(content.cues, content.duration);
  const groups = groupExtraInputs(attempt.extraInputs, timeline, width);
  const selectedGroup = selection?.kind === 'extra' ? groups.find(group => group.indices[0] === selection.first) : undefined;
  const rows: PracticeKey[] = ['MouseRight', 'Space'];
  const selectedCue = selection?.kind === 'cue' ? content.cues[selection.index] : undefined;
  const selectedResult = selection?.kind === 'cue' ? attempt.results[selection.index] : undefined;
  const percent = (position: number) => `${position / timeline.size * 100}%`;
  const at = (time: number) => percent(timeline.position(time));

  return <section className="results" aria-labelledby="results-title">
    <div className="result-header">
      <div className="result-summary">
        <h2 id="results-title" className={successes === content.cues.length ? 'result-complete' : undefined}
          aria-label={successes === content.cues.length ? '전체 대응 성공' : `${successes} / ${content.cues.length} 대응 성공`}>
          <span className="metric-label">대응 성공</span>
          <span className="result-score"><strong>{successes}</strong><span className="score-total">/ {content.cues.length}</span></span>
        </h2>
        <div className={`extra-total${attempt.extras ? ' has-extras' : ''}`}>
          <span className="metric-label">추가 입력</span>
          <span className="extra-value"><strong data-testid="extras">{attempt.extras}</strong><span>회</span></span>
        </div>
      </div>
      <div className="result-legend" aria-hidden="true"><span><i className="legend-hit" />성공 입력</span><span><i className="legend-extra" />추가 입력</span><span>〃 대기 축약</span></div>
    </div>
    <div className="result-scroll" tabIndex={0} aria-label="동작 주변 입력 비교 · 대기 간격 축약">
      <div className="result-chart">
        <div className="result-timeline" ref={trackRef} style={{ height: compact ? 150 : 206 }} aria-label="키별 동작과 입력 시점">
          {timeline.segments.map((segment, index) => segment.compressed
            ? <span key={index} className="result-gap" role="img" aria-label={`대기 ${seconds(segment.end - segment.start)} 축약`}
              style={{ left: percent((segment.left + segment.right) / 2) }}>〃</span>
            : <span key={index} className="result-focus" aria-hidden="true" style={{ left: percent(segment.left), width: percent(segment.right - segment.left) }} />)}
          {rows.map(key => {
            const count = attempt.extraInputs.filter(input => input.key === key).length;
            return <div className="result-key-row" key={key} data-key={key} style={{ top: rowY[key] }}>
              <span className="result-key-label">{keyLabel[key]}<small>추가 {count}</small></span>
            </div>;
          })}
          {content.cues.map((cue, index) => {
            const result = attempt.results[index];
            const resultKey = result.inputKey ?? cue.key;
            return <div key={cue.id} className={`result-event result-${result.status}`}>
              {cueWindows(cue).map(window => <span key={window.key} className="result-window" data-key={window.key}
                style={{ left: at(window.start), width: percent(timeline.position(window.end) - timeline.position(window.start)), top: rowY[window.key] - 10 }} />)}
              {result.inputTime !== undefined && <span className="result-hit" data-key={resultKey} style={{ left: at(result.inputTime), top: rowY[resultKey] }} aria-hidden="true" />}
              <button className="result-cue" style={{ left: at((cue.start + cue.end) / 2) }}
                aria-label={`${index + 1}. ${cue.label} ${stateLabel[result.status]}`} aria-pressed={selection?.kind === 'cue' && selection.index === index}
                aria-expanded={selection?.kind === 'cue' && selection.index === index}
                onClick={() => setSelection(current => current?.kind === 'cue' && current.index === index ? null : { kind: 'cue', index })}>
                <span className="step-number">{cue.label}</span>
                <span className="result-icon-frame">
                  <ActionIcon action={resultKey === 'MouseRight' ? 'dodge' : 'assist'} />
                </span>
              </button>
            </div>;
          })}
          {attempt.extraInputs.map((input, index) => <span key={index} className="result-extra-hit" data-key={input.key}
            style={{ left: at(input.time), top: rowY[input.key] }} aria-hidden="true" />)}
          {groups.map(group => {
            const first = group.indices[0], last = group.indices.at(-1)!;
            const count = group.indices.length;
            const selected = selection?.kind === 'extra' && selection.first === first;
            return <button key={first} className="extra-marker" data-key={group.key}
              style={{ left: percent(group.position), top: rowY[group.key] + (compact ? 12 : 18) }}
              aria-label={`추가 입력 ${keyLabel[group.key]} ${count}회, ${seconds(attempt.extraInputs[first].time)}${count > 1 ? `부터 ${seconds(attempt.extraInputs[last].time)}` : ''}`}
              aria-pressed={selected} aria-expanded={selected}
              onClick={() => setSelection(current => current?.kind === 'extra' && current.first === first ? null : { kind: 'extra', first })}>
              {count === 1 ? '+' : `+${count}`}
            </button>;
          })}
        </div>
      </div>
    </div>
    {selectedCue && selectedResult && <div className="result-detail" aria-live="polite">
      <strong>{selectedCue.label} · {stateLabel[selectedResult.status]}</strong>
      <span>{cueWindows(selectedCue).map(window =>
        `${keyLabel[window.key]} 구간 ${seconds(window.start)}–${seconds(window.end)}`).join(' / ')}</span>
      <span>{selectedResult.inputTime !== undefined ? `실제 입력 ${keyLabel[selectedResult.inputKey ?? selectedCue.key]} ${seconds(selectedResult.inputTime)}` : '성공 입력 없음'}</span>
    </div>}
    {selectedGroup && <div className="result-detail" aria-live="polite">
      <strong>추가 입력 {keyLabel[selectedGroup.key]} {selectedGroup.indices.length}회</strong>
      <ol className="extra-records">{selectedGroup.indices.map(index => {
        const input = attempt.extraInputs[index];
        return <li key={index}><time>{seconds(input.time)}</time><span>{keyLabel[input.key]}</span><span>{reasonLabel[input.reason]}</span></li>;
      })}</ol>
    </div>}
  </section>;
}
