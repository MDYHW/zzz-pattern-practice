import { expect, test } from '@playwright/test';
import type { Page } from '@playwright/test';
import { fileURLToPath } from 'node:url';
import { writeFileSync } from 'node:fs';
import { vesperExecute01, vesperExecute02 } from '../../src/content/skills';
import { GUIDE_SECONDS } from '../../src/practice/RhythmGuide';

const surface = (page: Page) => page.getByRole('region', { name: '연습 입력 영역' });
const at = async (page: Page, seconds: number) => {
  const outcome = await page.waitForFunction((time) => {
    const video = document.querySelector('video')!;
    const phase = document.querySelector<HTMLElement>('[data-phase]')?.dataset.phase;
    if (video.currentTime >= time) return { reached: true };
    if (!['interrupted', 'paused', 'error', 'finished'].includes(phase ?? '')) {
      return null;
    }
    return {
      reached: false,
      phase,
      currentTime: video.currentTime,
      paused: video.paused,
      readyState: video.readyState,
      seeking: video.seeking,
      buffered: Array.from({ length: video.buffered.length }, (_, index) => ({
        start: video.buffered.start(index), end: video.buffered.end(index),
      })),
    };
  }, seconds);
  const diagnostic = await outcome.jsonValue() as { reached: boolean };
  if (!diagnostic.reached) {
    throw new Error(`Playback stopped before ${seconds}s: ${JSON.stringify(diagnostic)}`);
  }
};
const refs = vesperExecute02.cues.map(cue => cue.reference);

test('ready screen, actual media decode, reload and narrow layout on the production subpath', async ({ page }, info) => {
  const errors: string[] = [];
  page.on('pageerror', error => errors.push(error.message));
  page.on('response', response => { if (response.status() >= 400) errors.push(`${response.status()} ${response.url()}`); });
  await page.setViewportSize({ width: 1280, height: 1080 });
  await page.goto('./');
  await expect(surface(page)).toHaveAttribute('data-phase', 'ready');
  expect(await page.locator('.action-icon').evaluateAll(icons => icons.every(icon => (icon as HTMLImageElement).naturalWidth === 66))).toBe(true);
  const media = await page.locator('video').evaluate((video: HTMLVideoElement) => ({ time: video.currentTime, paused: video.paused, width: video.videoWidth, duration: video.duration }));
  expect(media).toMatchObject({ time: 0, paused: true, width: 1280 });
  expect(media.duration).toBeCloseTo(vesperExecute02.duration, 2);
  const geometry = await page.locator('.timeline').evaluate(element => {
    const head = element.querySelector('.playhead')!;
    const upper = getComputedStyle(head, '::before'), lower = getComputedStyle(head, '::after');
    const cue = element.querySelector('.cue')!;
    return { arrowSizes: [upper.width, upper.height, lower.width, lower.height],
      thickness: getComputedStyle(cue.querySelector('i')!, '::before').height,
      tileFraction: cue.getBoundingClientRect().width / element.getBoundingClientRect().width,
      headTop: head.getBoundingClientRect().top - element.getBoundingClientRect().top,
      headBottom: element.getBoundingClientRect().bottom - head.getBoundingClientRect().bottom };
  });
  expect(geometry.arrowSizes).toEqual(['12px', '6px', '12px', '6px']);
  expect(geometry.headTop).toBeGreaterThanOrEqual(0);
  expect(geometry.headBottom).toBeGreaterThanOrEqual(0);
  expect(geometry.thickness).toBe('28px');
  expect(geometry.tileFraction).toBeCloseTo(.25 / 3, 3);
  await page.screenshot({ path: info.outputPath('practice-ready.png'), fullPage: true });
  expect((await page.locator('video').boundingBox())!.y).toBeLessThan(120);
  await expect(page.locator('.recorded-input-mask')).toHaveCount(1);
  await page.getByRole('button', { name: '녹화 입력 가리기' }).click();
  await expect(page.locator('.recorded-input-mask')).toHaveCount(0);
  await page.screenshot({ path: info.outputPath('practice-overlay-visible.png'), fullPage: true });
  await page.reload();
  await expect(surface(page)).toHaveAttribute('data-phase', 'ready');
  await page.setViewportSize({ width: 390, height: 844 });
  expect((await page.locator('video').boundingBox())!.y).toBeLessThan(160);
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
  await page.screenshot({ path: info.outputPath('practice-narrow.png'), fullPage: true });
  await page.getByRole('button', { name: '연습 시작' }).click();
  await at(page, 1.6);
  await page.screenshot({ path: info.outputPath('practice-narrow-running.png'), fullPage: true });
  const icon = (await page.locator('.cue .action-icon').first().boundingBox())!;
  expect(icon.width).toBe(28);
  await page.getByRole('button', { name: '일시정지' }).click();
  expect(errors).toEqual([]);
});

test('video and lane fit the first desktop viewport and optional help pauses without resuming', async ({ page }, info) => {
  for (const viewport of [{ width: 1280, height: 720 }, { width: 1366, height: 768 }, { width: 1440, height: 900 }, { width: 900, height: 650 }]) {
    await page.setViewportSize(viewport);
    await page.goto('./');
    await expect(surface(page)).toHaveAttribute('data-phase', 'ready');
    const video = (await page.locator('.video-stage').boundingBox())!;
    const guide = (await page.locator('.guide').boundingBox())!;
    const header = (await page.locator('.masthead').boundingBox())!;
    expect(Math.abs(video.y - header.y - header.height)).toBeLessThan(1);
    expect(Math.abs(header.y + header.height)).toBeLessThan(1);
    expect(Math.abs(video.y)).toBeLessThan(1);
    if (viewport.width === 1280) expect(video.height).toBeGreaterThan(595);
    expect(guide.y + guide.height).toBeLessThanOrEqual(viewport.height);
    expect(Math.abs(video.width - guide.width)).toBeLessThan(1);
    expect(Math.abs(video.width / video.height - 16 / 9)).toBeLessThan(.01);
    await expect(page.locator('.help')).not.toHaveAttribute('open');
    await expect(page.locator('.live-feedback, .guide-heading, .guide-caption')).toHaveCount(0);
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
    if (viewport.width === 1280 || viewport.width === 1440) {
      await page.screenshot({ path: info.outputPath(`desktop-${viewport.width}.png`), fullPage: true });
    }
  }
  await page.getByRole('button', { name: '연습 시작', exact: true }).click();
  await expect(surface(page)).toHaveAttribute('data-phase', 'running');
  await page.locator('.help summary').click();
  await expect(surface(page)).toHaveAttribute('data-phase', 'paused');
  await expect(page.locator('#input-help')).toBeVisible();
  await page.locator('.help summary').click();
  await expect(surface(page)).toHaveAttribute('data-phase', 'paused');
});

test('mistakes recover, missed cues keep playing, and hidden-guide retry can complete with extras', async ({ page }, info) => {
  test.setTimeout(45_000);
  await page.setViewportSize({ width: 1280, height: 1080 });
  await page.goto('./');
  const start = page.getByRole('button', { name: '연습 시작' });
  await expect(start).toBeEnabled();
  await start.focus();
  await page.keyboard.press('Space');
  await expect(surface(page)).toHaveAttribute('data-phase', 'running');
  await expect(surface(page)).toBeFocused();
  await page.keyboard.press('Space'); // Early, recoverable extra on the Space row.
  const videoElement = await page.locator('video').elementHandle();
  const beforeToggle = await page.locator('video').evaluate((video: HTMLVideoElement) => video.currentTime);
  await page.getByRole('button', { name: '녹화 입력 가리기' }).click();
  await expect(surface(page)).toBeFocused();
  await expect(surface(page)).toHaveAttribute('data-phase', 'running');
  expect(await videoElement!.evaluate(video => video === document.querySelector('video'))).toBe(true);
  expect(await page.locator('video').evaluate((video: HTMLVideoElement) => video.currentTime)).toBeGreaterThanOrEqual(beforeToggle);
  const area = await page.locator('.video-stage').boundingBox();
  await page.mouse.move(area!.x + area!.width / 2, area!.y + area!.height / 2);
  await at(page, refs[0]);
  await page.mouse.click(area!.x + area!.width / 2, area!.y + area!.height / 2, { button: 'right' });
  await expect(page.locator('[data-cue-id=entry]')).toHaveClass(/cue-success/);
  await expect(page.locator('[data-cue-id=entry]')).not.toHaveClass(/cue-current/);
  await expect(page.locator('[data-cue-id=entry]')).toHaveCSS('opacity', '1');
  await expect(page.locator('[data-cue-id=entry] > span')).toHaveCSS('opacity', '1');
  await expect(page.locator('[data-cue-id=entry] .action-icon')).toHaveCount(1);
  await expect(page.locator('[data-cue-id=entry] .cue-choice')).toHaveAttribute('data-key', 'MouseRight');
  await expect(page.locator('[data-cue-id=attack-1] .action-icon')).toHaveCount(1);
  await expect(page.locator('[data-cue-id=entry] .action-icon').first()).toHaveCSS('opacity', '1');
  await expect(page.locator('.video-progress')).toHaveCount(0);
  expect(await page.locator('[data-cue-id=entry] i').evaluate(element => getComputedStyle(element, '::after').content)).toBe('""');
  const entryColors = await page.locator('[data-cue-id=entry]').evaluate(element => ({
    border: getComputedStyle(element.querySelector('.cue-symbol')!).borderTopColor,
    fill: getComputedStyle(element.querySelector('i')!, '::before').backgroundColor,
  }));
  expect(entryColors.border).toBe(entryColors.fill);
  await expect(page.locator('.hit-feedback')).toHaveCount(0);
  await at(page, refs[1]);
  await page.mouse.click(area!.x + area!.width / 2, area!.y + area!.height / 2, { button: 'right' }); // Wrong key, then recover.
  await page.keyboard.press('Space');
  // Deliberately miss attack 2; video and later judgments must continue.
  await at(page, vesperExecute02.cues[2].end + .1);
  await expect(page.locator('[data-cue-id=attack-2]')).toHaveClass(/cue-miss/);
  const missedStyle = await page.locator('[data-cue-id=attack-2]').evaluate(element => ({
    border: getComputedStyle(element.querySelector('.cue-symbol')!).borderTopColor,
    tileBorder: getComputedStyle(element.querySelector('i')!, '::before').borderLeftColor,
    fill: getComputedStyle(element.querySelector('i')!, '::before').backgroundColor,
    symbol: getComputedStyle(element.querySelector('i')!, '::after').content,
  }));
  expect(missedStyle.border).toBe(missedStyle.fill);
  expect(missedStyle.tileBorder).toBe('rgb(240, 243, 233)');
  expect(missedStyle.symbol).toBe('""');
  await page.screenshot({ path: info.outputPath('practice-missed-lane.png'), fullPage: true });
  await at(page, refs[3]);
  await page.keyboard.press('Space');
  await at(page, refs[4]);
  await page.keyboard.press('Space');
  await expect(surface(page)).toHaveAttribute('data-phase', 'finished', { timeout: 14_000 });
  await expect(page.getByRole('heading', { name: '4 / 5 대응 성공' })).toBeVisible();
  await expect(page.locator('.result-event.result-miss')).toHaveCount(1);
  await expect(page.locator('.video-progress')).toHaveCount(0);
  const resultBox = (await page.locator('.results').boundingBox())!;
  const videoBox = (await page.locator('.video-stage').boundingBox())!;
  expect(Math.abs(resultBox.width - videoBox.width)).toBeLessThan(1);
  expect(Math.abs(resultBox.x - videoBox.x)).toBeLessThan(1);
  await page.locator('.result-cue').nth(2).click();
  const missedResult = page.locator('.result-event.result-miss');
  await expect(missedResult.locator('.result-window')).toHaveCSS('border-left-color', 'rgb(168, 58, 64)');
  await expect(missedResult.locator('.result-icon-frame')).toHaveCSS('outline-color', 'rgb(229, 143, 143)');
  await expect(page.locator('.result-detail')).toContainText('구간 내 성공 입력 없음');
  await page.locator('.result-cue').nth(2).click();
  await expect(page.locator('.result-detail')).toHaveCount(0);
  await expect(page.locator('.result-cue').nth(2)).toHaveAttribute('aria-expanded', 'false');
  await page.locator('.result-cue').first().focus();
  await page.keyboard.press('Enter');
  await expect(page.locator('.result-detail')).toContainText('실제 입력');
  await page.keyboard.press('Enter');
  await expect(page.locator('.result-detail')).toHaveCount(0);
  await page.keyboard.press('Space');
  await expect(page.locator('.result-cue').first()).toHaveAttribute('aria-expanded', 'true');
  await page.locator('.result-cue').nth(2).click();
  await expect(page.locator('.result-detail')).toContainText('구간 내 성공 입력 없음');
  await expect(page.locator('.extra-marker')).toHaveCount(2);
  await expect(page.locator('.result-extra-hit')).toHaveCount(2);
  await expect(page.locator('.result-hit')).toHaveCount(4);
  await expect(page.locator('.result-key-row')).toHaveCount(2);
  await expect(page.locator('.result-gap').first()).toBeVisible();
  for (const tick of await page.locator('.result-hit, .result-extra-hit').all()) {
    const key = await tick.getAttribute('data-key');
    const row = (await page.locator(`.result-key-row[data-key="${key}"]`).boundingBox())!;
    const box = (await tick.boundingBox())!;
    expect(box.y).toBeLessThan(row.y);
    expect(box.y + box.height).toBeGreaterThan(row.y);
  }
  await expect(page.locator('.extra-marker[data-key="Space"]')).toHaveCount(1);
  await expect(page.locator('.extra-marker[data-key="MouseRight"]')).toHaveCount(1);
  await page.locator('.extra-marker').last().focus();
  await page.keyboard.press('Enter');
  await expect(page.locator('.extra-records')).toContainText('우클릭');
  await expect(page.locator('.extra-records')).toContainText('선택과 다른 입력');
  const expandedHeight = (await page.locator('.results').boundingBox())!.height;
  await page.keyboard.press('Enter');
  await expect(page.locator('.extra-records')).toHaveCount(0);
  await expect(page.locator('.extra-marker').last()).toHaveAttribute('aria-expanded', 'false');
  expect((await page.locator('.results').boundingBox())!.height).toBeLessThan(expandedHeight);
  await page.locator('.extra-marker').first().click();
  await expect(page.locator('.extra-records')).toContainText('구간 밖');
  await page.locator('.extra-marker').last().click();
  await expect(page.locator('.extra-records')).toContainText('선택과 다른 입력');
  await page.locator('.extra-marker').last().click();
  await expect(page.locator('.extra-records')).toHaveCount(0);
  await page.locator('.extra-marker').last().click();
  await expect(page.getByTestId('extras')).toHaveText('2');
  await page.screenshot({ path: info.outputPath('practice-results.png'), fullPage: true });
  await page.keyboard.press('Space');
  await expect(page.getByTestId('extras')).toHaveText('2');
  await page.setViewportSize({ width: 390, height: 844 });
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
  expect(await page.locator('.result-scroll').evaluate(element => element.scrollWidth > element.clientWidth)).toBe(true);
  await page.screenshot({ path: info.outputPath('practice-results-narrow.png'), fullPage: true });
  await page.setViewportSize({ width: 1280, height: 1080 });

  const normalWidth = area!.width;
  await page.getByRole('button', { name: '리듬 안내 표시' }).click();
  await expect(page.getByLabel('리듬 안내', { exact: true })).toHaveCount(0);
  expect((await page.locator('.video-stage').boundingBox())!.width).toBeGreaterThanOrEqual(normalWidth);
  expect(Math.abs((await page.locator('.results').boundingBox())!.width - (await page.locator('.video-stage').boundingBox())!.width)).toBeLessThan(1);
  await expect(page.getByRole('combobox', { name: '진입 입력' })).toBeDisabled();
  await expect(page.getByRole('combobox', { name: '진입 입력' })).toHaveValue('MouseRight');
  await page.getByRole('button', { name: '입력 선택으로' }).click();
  await page.getByRole('combobox', { name: '진입 입력' }).selectOption('Space');
  await expect(surface(page)).toHaveAttribute('data-phase', 'ready');
  await page.getByRole('button', { name: '연습 시작' }).click();
  await expect(surface(page)).toHaveAttribute('data-phase', 'running');
  await expect(page.locator('.results')).toHaveCount(0);
  await expect(page.getByRole('button', { name: '녹화 입력 가리기' })).toHaveAttribute('aria-pressed', 'false');
  await page.keyboard.press('x');
  const bigger = await page.locator('.video-stage').boundingBox();
  await page.mouse.click(bigger!.x + bigger!.width / 2, bigger!.y + bigger!.height / 2); // Unrelated mouse input is ignored.
  await at(page, refs[0]);
  await page.keyboard.press('Space'); // Selected Space entry works with the guide hidden.
  for (const time of refs.slice(1)) {
    await at(page, time);
    await page.keyboard.down('Space');
    await page.keyboard.down('Space'); // Held/repeat is not an additional input.
    await page.keyboard.up('Space');
  }
  await expect(surface(page)).toHaveAttribute('data-phase', 'finished', { timeout: 14_000 });
  await expect(page.getByRole('heading', { name: '전체 대응 성공' })).toBeVisible();
  await expect(page.getByTestId('extras')).toHaveText('0');
  await expect(page.locator('.result-hit').first()).toHaveAttribute('data-key', 'Space');
  await page.screenshot({ path: info.outputPath('practice-results-complete.png'), fullPage: true });
});

test('media waiting interrupts, while window departure pauses without a final missed score', async ({ page }) => {
  await page.goto('./');
  await page.getByRole('button', { name: '연습 시작' }).click();
  await expect(surface(page)).toHaveAttribute('data-phase', 'running');
  await page.locator('video').dispatchEvent('waiting');
  await expect(surface(page)).toHaveAttribute('data-phase', 'interrupted');
  await expect(page.getByRole('heading', { name: '중단된 시도' })).toBeVisible();
  await expect(page.locator('.result-chart')).toHaveCount(0);
  expect(await page.locator('video').evaluate((video: HTMLVideoElement) => video.paused)).toBe(true);
  await page.getByRole('button', { name: '다시 연습' }).click();
  await expect(surface(page)).toHaveAttribute('data-phase', 'running');
  await page.evaluate(() => window.dispatchEvent(new Event('blur')));
  await expect(surface(page)).toHaveAttribute('data-phase', 'paused');
  await page.keyboard.press('Space');
  await expect(surface(page)).not.toHaveAttribute('data-last-input-time');
});

test('Escape and video controls pause, prepare resume, and restart without losing preferences', async ({ page }, info) => {
  await page.goto('./');
  await expect(surface(page)).toHaveAttribute('data-phase', 'ready');
  await page.keyboard.press('Escape');
  await expect(surface(page)).toHaveAttribute('data-phase', 'ready');
  await expect(page.locator('.video-cover').getByRole('button', { name: '연습 시작' })).toBeVisible();
  await page.getByRole('button', { name: '연습 시작' }).click();
  await at(page, refs[0]);
  const area = (await page.locator('.video-stage').boundingBox())!;
  await page.mouse.click(area.x + area.width / 2, area.y + area.height / 2, { button: 'right' });
  await page.keyboard.down('Escape');
  await expect(surface(page)).toHaveAttribute('data-phase', 'paused');
  await page.keyboard.down('Escape');
  await expect(surface(page)).toHaveAttribute('data-phase', 'paused');
  await page.keyboard.up('Escape');
  const stopped = await page.locator('video').evaluate((video: HTMLVideoElement) => video.currentTime);
  const inputTime = await surface(page).getAttribute('data-last-input-time');
  await expect(page.locator('.guide')).toHaveAttribute('data-time', stopped.toFixed(6));
  await page.keyboard.down('Space');
  await page.waitForTimeout(1100); // Real media must remain frozen beyond the resume delay.
  expect(await page.locator('video').evaluate((video: HTMLVideoElement) => video.currentTime)).toBe(stopped);
  await expect(surface(page)).toHaveAttribute('data-last-input-time', inputTime!);
  await page.screenshot({ path: info.outputPath('practice-paused.png'), fullPage: true });
  await page.keyboard.press('Escape');
  await expect(surface(page)).toHaveAttribute('data-phase', 'resuming');
  expect(await page.locator('video').evaluate((video: HTMLVideoElement) => video.currentTime)).toBe(stopped);
  await expect(surface(page)).toHaveAttribute('data-phase', 'running');
  await expect(page.locator('[data-cue-id=entry]')).toHaveClass(/cue-success/);
  await expect(surface(page)).toHaveAttribute('data-last-input-time', inputTime!);
  await page.keyboard.up('Space');
  await page.getByRole('button', { name: '일시정지', exact: true }).click();
  await expect(surface(page)).toHaveAttribute('data-phase', 'paused');
  await page.getByRole('button', { name: '이어서 하기', exact: true }).click();
  await expect(surface(page)).toHaveAttribute('data-phase', 'resuming');
  await page.keyboard.press('Escape');
  await expect(surface(page)).toHaveAttribute('data-phase', 'paused');
  await page.setViewportSize({ width: 390, height: 844 });
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
  await page.screenshot({ path: info.outputPath('practice-paused-narrow.png'), fullPage: true });
  await page.getByRole('button', { name: '녹화 입력 가리기' }).click();
  await page.getByRole('button', { name: '리듬 안내 표시' }).click();
  await page.getByRole('button', { name: '처음부터', exact: true }).click();
  await expect(surface(page)).toHaveAttribute('data-phase', 'running');
  expect(await page.locator('video').evaluate((video: HTMLVideoElement) => video.currentTime)).toBeLessThan(1);
  await expect(surface(page)).not.toHaveAttribute('data-last-input-time');
  await expect(page.locator('.progress-count, .video-progress')).toHaveCount(0);
  await expect(page.locator('.guide')).toHaveCount(0);
  await expect(page.getByRole('button', { name: '녹화 입력 가리기' })).toHaveAttribute('aria-pressed', 'false');
});

test('burned-in video pixels and moving tile edges remain within 50ms of the fixed line', async ({ page }, info) => {
  const mediaEvents: unknown[] = [];
  await page.exposeFunction('recordMediaEvent', (event: unknown) => mediaEvents.push(event));
  await page.route('**/media/vesper-execute-02.mp4', route => route.fulfill({
    path: fileURLToPath(new URL('../fixtures/video-clock.mp4', import.meta.url)), contentType: 'video/mp4',
  }));
  await page.setViewportSize({ width: 1280, height: 1080 });
  await page.goto('./');
  await page.locator('video').evaluate((video: HTMLVideoElement) => {
    for (const name of ['playing', 'waiting', 'seeking', 'seeked', 'pause', 'canplay']) video.addEventListener(name, () => {
      void (window as unknown as { recordMediaEvent(event: unknown): Promise<void> }).recordMediaEvent({
        name, time: video.currentTime, ready: video.readyState, seeking: video.seeking,
      });
    });
  });
  // Isolate display synchronization from the fixture route's startup download.
  await page.waitForFunction(() => {
    const video = document.querySelector('video')!;
    return video.readyState === 4 && video.buffered.length > 0 && video.buffered.end(video.buffered.length - 1) >= video.duration - 0.02;
  });
  const compositor = await page.context().newCDPSession(page);
  let latestFrame: { data: string; timestamp: number } | undefined;
  let stoppingScreencast = false;
  let ackFailure: unknown;
  const pendingAcks = new Set<Promise<void>>();
  const acknowledgeFrame = (sessionId: number) => {
    let acknowledgement: Promise<void>;
    acknowledgement = compositor.send('Page.screencastFrameAck', { sessionId }).then(() => undefined).catch(error => {
      if (!stoppingScreencast) ackFailure ??= error;
    }).finally(() => pendingAcks.delete(acknowledgement));
    pendingAcks.add(acknowledgement);
  };
  const captureFrame = (event: { data: string; sessionId: number; metadata: { timestamp?: number } }) => {
    latestFrame = { data: event.data, timestamp: event.metadata.timestamp ?? 0 };
    acknowledgeFrame(event.sessionId);
  };
  compositor.on('Page.screencastFrame', captureFrame);
  const observations = [];
  let bodyFailed = false;
  try {
    await compositor.send('Page.startScreencast', { format: 'jpeg', quality: 95, maxWidth: 1280, maxHeight: 1080, everyNthFrame: 1 });
    await page.getByRole('button', { name: '연습 시작' }).click();
    await expect(surface(page)).toHaveAttribute('data-phase', 'running');
    for (const sample of [{ time: 2.8, start: vesperExecute02.cues[0].start, min: .22, max: .43 }, { time: 5.5, start: vesperExecute02.cues[2].start, min: .22, max: .43 }, { time: 8.8, start: vesperExecute02.cues[4].start, min: .22, max: .43 }]) {
      const { time } = sample;
      await page.waitForFunction(time => document.querySelector('video')!.currentTime >= time, time, { timeout: 12_000 }).catch(error => {
        throw new Error(`${String(error)}; media events: ${JSON.stringify(mediaEvents)}`);
      });
      const videoBox = (await page.locator('video').boundingBox())!;
      const timeline = (await page.locator('.timeline').boundingBox())!;
      const captureBefore = await page.locator('video').evaluate((video: HTMLVideoElement) => ({
        videoTime: video.currentTime,
        guideTime: document.querySelector<HTMLElement>('.guide')?.dataset.time,
      }));
      const previous = latestFrame?.timestamp;
      await expect.poll(() => latestFrame?.timestamp).not.toBe(previous);
      const capturedFrame = latestFrame!;
      const captureAfter = await page.locator('video').evaluate((video: HTMLVideoElement) => ({
        videoTime: video.currentTime,
        guideTime: document.querySelector<HTMLElement>('.guide')?.dataset.time,
      }));
      const screenshot = Buffer.from(capturedFrame.data, 'base64');
      writeFileSync(info.outputPath(`clock-${time}.jpg`), screenshot);
      await info.attach(`clock-${time}`, { body: screenshot, contentType: 'image/jpeg' });
      const pixels = await page.evaluate(async ({ jpeg, videoBox, timeline, sample, guideSeconds }) => {
        const image = new Image();
        image.src = `data:image/jpeg;base64,${jpeg}`;
        await image.decode();
        const canvas = document.createElement('canvas');
        canvas.width = image.width; canvas.height = image.height;
        const ctx = canvas.getContext('2d')!;
        ctx.drawImage(image, 0, 0);
        const read = (x: number, y: number) => ctx.getImageData(Math.round(x), Math.round(y), 1, 1).data;
        let frame = 0;
        for (let bit = 0; bit < 10; bit++) {
          const pixel = read(videoBox.x + (130 + bit * 80) / 1280 * videoBox.width, videoBox.y + 330 / 720 * videoBox.height);
          if (pixel[0] > 180) frame += 2 ** bit;
        }
        // Decode the visible tile's leading edge, independent of React time/DOM positions.
        // The static search bands select an unoccluded tile at each sample.
        let edge: number | undefined;
        for (let x = Math.ceil(timeline.width * sample.min); x < timeline.width * sample.max; x++) {
          const pixel = read(timeline.x + x, timeline.y + 78);
          if (pixel[0] > 100 && pixel[1] > 110) { edge = Math.round(timeline.x + x) - timeline.x; break; }
        }
        if (edge === undefined) throw new Error('Rendered timing tile not found');
        let line: number | undefined;
        for (let x = Math.floor(timeline.width * .18); x < timeline.width * .22; x++) {
          const pixel = read(timeline.x + x, timeline.y + 92);
          if (pixel[0] > 180 && pixel[1] > 180) { line = Math.round(timeline.x + x) + 1 - timeline.x; break; }
        }
        if (line === undefined) throw new Error('Fixed judgment line not found');
        return { frame, videoTime: frame / 60, guideTime: sample.start + (line - edge) / timeline.width * guideSeconds };
      }, { jpeg: screenshot.toString('base64'), videoBox, timeline, sample, guideSeconds: GUIDE_SECONDS });
      const observation = {
        ...pixels,
        differenceMs: Math.abs(pixels.videoTime - pixels.guideTime) * 1000,
        screencastTimestamp: capturedFrame.timestamp,
        captureBefore,
        captureAfter,
        input: undefined as number | undefined,
        before: undefined as number | undefined,
        after: undefined as number | undefined,
      };
      observations.push(observation);
      expect(pixels.videoTime).toBeGreaterThanOrEqual(time - 1 / 60);
      expect(Math.abs(pixels.videoTime - pixels.guideTime), JSON.stringify(observation)).toBeLessThanOrEqual(0.05);
      // Native keyboard event: compare its media timestamp with the browser's before/after readings.
      observation.before = await page.locator('video').evaluate((video: HTMLVideoElement) => video.currentTime);
      await page.keyboard.press('Space');
      observation.input = Number(await surface(page).getAttribute('data-last-input-time'));
      observation.after = await page.locator('video').evaluate((video: HTMLVideoElement) => video.currentTime);
      expect(observation.input).toBeGreaterThanOrEqual(observation.before);
      expect(observation.input).toBeLessThanOrEqual(observation.after);
    }
    if (ackFailure) throw ackFailure;
  } catch (error) {
    bodyFailed = true;
    throw error;
  } finally {
    compositor.removeListener('Page.screencastFrame', captureFrame);
    await Promise.allSettled(pendingAcks);
    stoppingScreencast = true;
    let cleanupFailure: unknown;
    if (ackFailure) cleanupFailure = ackFailure;
    try {
      await compositor.send('Page.stopScreencast');
    } catch (error) {
      cleanupFailure ??= error;
    }
    try {
      await compositor.detach();
    } catch (error) {
      cleanupFailure ??= error;
    }
    const observationsJson = JSON.stringify(observations, null, 2);
    writeFileSync(info.outputPath('synchronization.json'), observationsJson);
    try {
      await info.attach('synchronization-observations', { body: observationsJson, contentType: 'application/json' });
    } catch (error) {
      if (!bodyFailed) throw error;
    }
    if (!bodyFailed && cleanupFailure) throw cleanupFailure;
  }
});

test('switching skills resets a paused attempt and Execute_01 completes and retries', async ({ page }, info) => {
  test.setTimeout(45_000);
  await page.goto('./');
  await page.getByRole('button', { name: '연습 시작' }).click();
  await expect(surface(page)).toHaveAttribute('data-phase', 'running');
  await expect(page.getByRole('combobox', { name: '제어스킬' })).toBeDisabled();
  await at(page, refs[0]);
  let area = (await page.locator('.video-stage').boundingBox())!;
  await page.mouse.click(area.x + area.width / 2, area.y + area.height / 2, { button: 'right' });
  await page.keyboard.press('Escape');
  const oldVideo = await page.locator('video').elementHandle();
  await page.getByRole('combobox', { name: '제어스킬' }).selectOption(vesperExecute01.id);
  await expect(surface(page)).toHaveAttribute('data-phase', 'ready');
  expect(await oldVideo!.evaluate(video => video.isConnected)).toBe(false);
  await expect(surface(page)).not.toHaveAttribute('data-last-input-time');
  await expect(page.locator('.cue-success, .results')).toHaveCount(0);
  await expect(page.locator('.cue')).toHaveCount(6);
  expect(await page.locator('video').evaluate((v: HTMLVideoElement) => ({ time: v.currentTime, duration: v.duration })))
    .toEqual({ time: 0, duration: vesperExecute01.duration });
  await page.screenshot({ path: info.outputPath('execute01-ready.png'), fullPage: true });
  await page.getByRole('button', { name: '리듬 안내 표시' }).click();
  await page.getByRole('button', { name: '연습 시작' }).click();
  area = (await page.locator('.video-stage').boundingBox())!;
  for (const cue of vesperExecute01.cues) {
    await at(page, cue.reference);
    if (cue.key === 'MouseRight') await page.mouse.click(area.x + area.width / 2, area.y + area.height / 2, { button: 'right' });
    else await page.keyboard.press('Space');
  }
  await expect(surface(page)).toHaveAttribute('data-phase', 'finished', { timeout: 6_000 });
  await expect(page.getByRole('heading', { name: '전체 대응 성공' })).toBeVisible();
  await expect(page.getByTestId('extras')).toHaveText('0');
  await page.screenshot({ path: info.outputPath('execute01-complete.png'), fullPage: true });
  await page.getByRole('button', { name: '다시 연습' }).click();
  await expect(surface(page)).toHaveAttribute('data-phase', 'running');
  expect(await page.locator('video').evaluate((v: HTMLVideoElement) => v.currentTime)).toBeLessThan(1);
  await expect(surface(page)).not.toHaveAttribute('data-last-input-time');
  await expect(page.locator('.guide, .results')).toHaveCount(0);
  await page.keyboard.press('Escape');
  await page.getByRole('combobox', { name: '제어스킬' }).selectOption(vesperExecute02.id);
  await expect(surface(page)).toHaveAttribute('data-phase', 'ready');
  await expect(page.locator('.guide')).toHaveCount(0);
  await page.getByRole('button', { name: '리듬 안내 표시' }).click();
  await expect(page.locator('.cue')).toHaveCount(5);
});

test('reload starts below the header, wheel stops there and settings remain reachable', async ({ page }, info) => {
  await page.setViewportSize({ width: 1280, height: 720 });
  await page.goto('./');
  await expect(surface(page)).toHaveAttribute('data-phase', 'ready');
  const scroll = page.locator('.practice-scroll');
  const alignment = () => scroll.evaluate(element => ({
    scroll: element.scrollTop,
    header: element.querySelector('.masthead')!.getBoundingClientRect().bottom,
    video: element.querySelector('.video-stage')!.getBoundingClientRect().top,
  }));
  await expect.poll(async () => Math.abs((await alignment()).video)).toBeLessThan(1);
  expect((await alignment()).scroll).toBeGreaterThan(0);
  const video = (await page.locator('.video-stage').boundingBox())!;
  const guide = (await page.locator('.guide').boundingBox())!;
  expect(video.height).toBeGreaterThan(595);
  expect(guide.y + guide.height).toBeLessThanOrEqual(720);
  await page.screenshot({ path: info.outputPath('header-hidden.png') });

  await page.mouse.move(640, 300);
  await page.mouse.wheel(0, -600);
  await expect.poll(async () => (await alignment()).scroll).toBe(0);
  await expect(page.getByRole('combobox', { name: '제어스킬' })).toBeInViewport();
  await page.screenshot({ path: info.outputPath('header-visible.png') });
  await page.getByRole('combobox', { name: '제어스킬' }).selectOption(vesperExecute01.id);
  await expect(surface(page)).toHaveAttribute('data-phase', 'ready');
  await expect(page.getByRole('combobox', { name: '제어스킬' })).toBeInViewport();
  await page.mouse.move(640, 300);
  await page.mouse.wheel(0, 600);
  await expect.poll(async () => Math.abs((await alignment()).video)).toBeLessThan(1);
  await expect.poll(async () => Math.abs((await alignment()).header)).toBeLessThan(1);

  // A continuous burst stays at the stop, then a new gesture can reach help.
  await page.mouse.wheel(0, 200);
  expect(Math.abs((await alignment()).video)).toBeLessThan(1);
  await page.waitForTimeout(180); // Separate wheel gestures, beyond the 140ms inertia guard.
  await page.mouse.wheel(0, 800);
  await expect(page.locator('.help summary')).toBeInViewport();
  await page.reload();
  await expect(surface(page)).toHaveAttribute('data-phase', 'ready');
  await expect.poll(async () => Math.abs((await alignment()).video)).toBeLessThan(1);
  await page.setViewportSize({ width: 390, height: 844 });
  await expect.poll(async () => Math.abs((await alignment()).video)).toBeLessThan(1);
  await page.screenshot({ path: info.outputPath('header-hidden-narrow.png') });
  // Native keyboard focus reveals controls in the off-screen header as well.
  await page.getByRole('combobox', { name: '제어스킬' }).focus();
  await expect(page.getByRole('combobox', { name: '제어스킬' })).toBeInViewport();
});

test('lane feedback follows the paused video clock and respects reduced motion', async ({ page }, info) => {
  await page.goto('./');
  await page.getByRole('button', { name: '연습 시작' }).click();
  await at(page, refs[0]);
  await surface(page).click({ button: 'right', position: { x: 10, y: 10 } });
  await expect(page.locator('.cue-success')).toHaveCount(1);
  await page.keyboard.press('Escape');
  await expect(surface(page)).toHaveAttribute('data-phase', 'paused');
  const read = () => page.locator('.timeline').evaluate(element => ({
    time: element.parentElement!.dataset.time,
    icon: element.querySelector<HTMLElement>('.cue-success .cue-symbol')!.style.translate,
    pulse: element.querySelector<HTMLElement>('.input-pulse')?.style.cssText,
    line: element.querySelector('.playhead')!.getBoundingClientRect().x,
  }));
  await expect(page.locator('.input-pulse-success')).toHaveCount(1);
  const before = await read();
  await page.waitForTimeout(300);
  expect(await read()).toEqual(before);
  await page.screenshot({ path: info.outputPath('lane-feedback.png') });
  await page.emulateMedia({ reducedMotion: 'reduce' });
  expect(await page.locator('.cue-success .cue-symbol').evaluate(element => getComputedStyle(element).translate)).toBe('none');
  expect((await read()).line).toBe(before.line);
});

test('entry choice persists per pattern, changes one tile and is locked during an attempt', async ({ page }, info) => {
  await page.goto('./');
  await expect(surface(page)).toHaveAttribute('data-phase', 'ready');
  const choice = page.getByRole('combobox', { name: '진입 입력' });
  await expect(choice).toHaveValue('MouseRight');
  await choice.selectOption('Space');
  await expect(surface(page)).toHaveAttribute('data-phase', 'ready');
  await expect(page.locator('.cue').first().locator('.action-icon')).toHaveCount(1);
  const fraction = () => page.locator('.timeline').evaluate(el =>
    el.querySelector('.cue')!.getBoundingClientRect().width / el.getBoundingClientRect().width);
  expect(await fraction()).toBeCloseTo(.4 / 3, 3);
  await page.reload();
  await expect(surface(page)).toHaveAttribute('data-phase', 'ready');
  await expect(choice).toHaveValue('Space');
  await page.getByRole('combobox', { name: '제어스킬' }).selectOption(vesperExecute01.id);
  await expect(surface(page)).toHaveAttribute('data-phase', 'ready');
  await expect(choice).toHaveValue('MouseRight');
  expect(await fraction()).toBeCloseTo(.2 / 3, 3);
  await page.getByRole('combobox', { name: '제어스킬' }).selectOption(vesperExecute02.id);
  await expect(surface(page)).toHaveAttribute('data-phase', 'ready');
  await expect(choice).toHaveValue('Space');
  await page.getByRole('button', { name: '연습 시작' }).click();
  await at(page, 1.5);
  await page.screenshot({ path: info.outputPath('entry-space-running.png') });
  await page.getByRole('button', { name: '일시정지' }).click();
  await expect(choice).toBeDisabled();
  await expect(choice).toHaveValue('Space');
});
