import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { expect, test, vi } from 'vitest';
import { App } from './App';

test('unsupported media environments show a recoverable message and never offer a misleading start', () => {
  vi.spyOn(HTMLMediaElement.prototype, 'pause').mockImplementation(() => {});
  vi.stubGlobal('ResizeObserver', class { observe() {} disconnect() {} });
  render(<App />);
  expect(screen.getByRole('status')).toHaveTextContent('영상 프레임 동기화를 지원하지 않습니다');
  expect(screen.queryByRole('button', { name: '연습 시작' })).not.toBeInTheDocument();
  expect(screen.getByRole('button', { name: '새로고침' })).toBeEnabled();
  expect(screen.getByLabelText('리듬 안내')).toBeInTheDocument();
  fireEvent.click(screen.getByRole('button', { name: '리듬 안내 표시' }));
  expect(screen.queryByLabelText('리듬 안내')).not.toBeInTheDocument();
  expect(screen.getByRole('status')).toHaveTextContent('영상 프레임 동기화를 지원하지 않습니다');
  cleanup();
  vi.restoreAllMocks();
  vi.unstubAllGlobals();
});
