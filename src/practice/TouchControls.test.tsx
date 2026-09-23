import { createRef } from 'react';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { afterEach, beforeEach, expect, test, vi } from 'vitest';
import { TouchControls } from './TouchControls';

beforeEach(() => {
  class TestPointerEvent extends MouseEvent {
    pointerId: number;
    constructor(type: string, init: PointerEventInit) { super(type, init); this.pointerId = init.pointerId ?? 0; }
  }
  vi.stubGlobal('PointerEvent', TestPointerEvent);
  HTMLElement.prototype.setPointerCapture = vi.fn();
});
afterEach(() => { cleanup(); vi.unstubAllGlobals(); delete (HTMLElement.prototype as Partial<HTMLElement>).setPointerCapture; });

test('two independent touches submit once per held action and cancellation permits another press', () => {
  const input = vi.fn();
  render(<TouchControls videoRef={createRef()} fixed enabled input={input} />);
  const dodge = screen.getByRole('button', { name: '회피' });
  const assist = screen.getByRole('button', { name: '지원' });
  fireEvent.pointerDown(dodge, { pointerId: 1, button: 0 });
  fireEvent.pointerDown(dodge, { pointerId: 2, button: 0 });
  fireEvent.pointerDown(assist, { pointerId: 3, button: 0 });
  fireEvent.click(dodge, { detail: 1 });
  expect(input.mock.calls).toEqual([['MouseRight'], ['Space']]);
  fireEvent.pointerUp(dodge, { pointerId: 1 });
  expect(dodge).toHaveClass('is-pressed');
  fireEvent.pointerCancel(dodge, { pointerId: 2 });
  expect(dodge).not.toHaveClass('is-pressed');
  fireEvent.pointerDown(dodge, { pointerId: 4, button: 0 });
  expect(input.mock.calls).toEqual([['MouseRight'], ['Space'], ['MouseRight']]);
  fireEvent.blur(window);
  expect(dodge).not.toHaveClass('is-pressed');
  expect(assist).not.toHaveClass('is-pressed');
});

test('pausing clears held visuals and disables pointer input', () => {
  const input = vi.fn();
  const videoRef = createRef<HTMLVideoElement>();
  const view = render(<TouchControls videoRef={videoRef} fixed enabled input={input} />);
  const dodge = screen.getByRole('button', { name: '회피' });
  fireEvent.pointerDown(dodge, { pointerId: 1, button: 0 });
  view.rerender(<TouchControls videoRef={videoRef} fixed enabled={false} input={input} />);
  expect(dodge).toBeDisabled();
  expect(dodge).not.toHaveClass('is-pressed');
  fireEvent.pointerDown(dodge, { pointerId: 2, button: 0 });
  expect(input).toHaveBeenCalledTimes(1);
});
