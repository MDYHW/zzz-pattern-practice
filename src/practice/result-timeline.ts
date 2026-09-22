import { cueEnvelope, type Attempt, type Cue, type PracticeKey } from './judge';

interface Segment { start: number; end: number; left: number; right: number; compressed: boolean }

/** Keep windows and 300ms on either side at one scale; fold only idle gaps. */
export function resultTimeline(cues: readonly Cue[], duration: number) {
  const focus: { start: number; end: number }[] = [];
  for (const cue of cues) {
    const envelope = cueEnvelope(cue);
    const start = Math.max(0, envelope.start - .3), end = Math.min(duration, envelope.end + .3);
    const previous = focus.at(-1);
    if (previous && start <= previous.end) previous.end = Math.max(previous.end, end);
    else focus.push({ start, end });
  }
  const segments: Segment[] = [];
  let cursor = 0, size = 0;
  const append = (start: number, end: number, idle: boolean) => {
    if (end <= start) return;
    const span = idle ? Math.min(end - start, .18) : end - start;
    segments.push({ start, end, left: size, right: size + span, compressed: span < end - start });
    size += span;
  };
  for (const range of focus) {
    append(cursor, range.start, true); append(range.start, range.end, false); cursor = range.end;
  }
  append(cursor, duration, true);
  const segmentAt = (time: number) => segments.findIndex(segment => time < segment.end);
  const locate = (time: number) => {
    const index = segmentAt(Math.max(0, time));
    return index < 0 ? segments.length - 1 : index;
  };
  const position = (time: number) => {
    const segment = segments[locate(time)];
    if (!segment) return 0;
    const fraction = Math.min(1, Math.max(0, (time - segment.start) / (segment.end - segment.start)));
    return segment.left + fraction * (segment.right - segment.left);
  };
  return { segments, size: size || 1, locate, position };
}

export interface ExtraGroup { key: PracticeKey; indices: number[]; segment: number; position: number }

/** Group nearby marks within the same key row and segment, retaining every record. */
export function groupExtraInputs(inputs: Attempt['extraInputs'], timeline: ReturnType<typeof resultTimeline>, width: number) {
  const groups: ExtraGroup[] = [];
  const previousByKey = new Map<PracticeKey, ExtraGroup>();
  inputs.forEach((input, index) => {
    const segment = timeline.locate(input.time), position = timeline.position(input.time);
    const previous = previousByKey.get(input.key);
    if (previous && previous.segment === segment && (position - previous.position) / timeline.size * width < 32) {
      previous.indices.push(index);
    } else {
      const group = { key: input.key, indices: [index], segment, position };
      groups.push(group); previousByKey.set(input.key, group);
    }
  });
  return groups;
}
