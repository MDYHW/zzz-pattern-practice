import { expect, test } from '@playwright/test';
import type { Page } from '@playwright/test';
import { mirageArcherUnitAttack09 as content, withMirageLastResponse } from '../../src/content/skills';

const surface = (page: Page) => page.getByRole('region', { name: '연습 입력 영역' });
async function at(page: Page, time: number) {
  await page.waitForFunction(time => {
    const video = document.querySelector('video')!;
    const phase = document.querySelector<HTMLElement>('[data-phase]')?.dataset.phase;
    if (video.currentTime >= time) return true;
    if (['paused', 'interrupted', 'error', 'finished'].includes(phase ?? ''))
      throw new Error(`Playback stopped: ${phase} at ${video.currentTime}, target ${time}`);
    return false;
  }, time);
}

test('Mirage Archer Unit plays five adopted cues without preparation and keeps display controls responsive', async ({ page }, info) => {
  test.setTimeout(45_000);
  const errors: string[] = [];
  page.on('pageerror', error => errors.push(error.message));
  page.on('response', response => { if (response.status() >= 400) errors.push(`${response.status()} ${response.url()}`); });
  await page.setViewportSize({ width: 1280, height: 1080 });
  await page.goto('./');
  await page.getByRole('combobox', { name: '보스' }).selectOption(content.bossId!);
  await expect(surface(page)).toHaveAttribute('data-phase', 'ready');
  await expect(page.getByRole('combobox', { name: '제어스킬' })).toHaveValue(content.id);
  await expect(page.getByRole('combobox', { name: '진입 입력' })).toHaveCount(0);
  await expect(page.getByRole('combobox', { name: '마지막 대응' })).toHaveValue('Space');
  await expect(page.locator('.entry-selection')).toContainText('진입은 우클릭, 1~3타는 Space, 마지막은 Space입니다.');
  await expect(page.locator('.cue')).toHaveCount(5);
  await expect(page.locator('[data-preparation-id], [data-movement-key]')).toHaveCount(0);
  await expect(page.locator('.recorded-input-mask')).toHaveCount(1);
  await expect.poll(() => page.locator('video').evaluate((video: HTMLVideoElement) => video.videoWidth)).toBe(1280);
  expect(await page.locator('video').evaluate((video: HTMLVideoElement) => video.duration)).toBeCloseTo(content.duration, 2);
  await page.screenshot({ path: info.outputPath('mirage-ready.png'), fullPage: true });

  await page.getByRole('button', { name: '리듬 안내 표시' }).click();
  await expect(page.locator('.cue')).toHaveCount(0);
  await page.getByRole('button', { name: '리듬 안내 표시' }).click();
  await expect(page.locator('.cue')).toHaveCount(5);
  await page.getByRole('button', { name: '녹화 입력 가리기' }).click();
  await expect(page.locator('.recorded-input-mask')).toHaveCount(0);
  await page.getByRole('button', { name: '녹화 입력 가리기' }).click();
  await expect(page.locator('.recorded-input-mask')).toHaveCount(1);

  await page.setViewportSize({ width: 390, height: 844 });
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
  expect(await page.locator('.practice-scroll').evaluate(element => element.scrollWidth <= element.clientWidth)).toBe(true);
  await page.screenshot({ path: info.outputPath('mirage-ready-narrow.png'), fullPage: true });
  await page.setViewportSize({ width: 1280, height: 1080 });

  await page.getByRole('button', { name: '연습 시작' }).click();
  await expect(surface(page)).toHaveAttribute('data-phase', 'running');
  const video = (await page.locator('.video-stage').boundingBox())!;
  for (const cue of content.cues) {
    await at(page, cue.reference);
    if (cue.key === 'MouseRight')
      await page.mouse.click(video.x + video.width / 2, video.y + video.height / 2, { button: 'right' });
    else await page.keyboard.press('Space');
  }
  await expect(surface(page)).toHaveAttribute('data-phase', 'finished', { timeout: 8_000 });
  await expect(page.getByRole('heading', { name: '전체 대응 성공' })).toBeVisible();
  await expect(page.locator('.result-hit')).toHaveCount(5);
  await expect(page.getByTestId('extras')).toHaveText('0');
  await page.screenshot({ path: info.outputPath('mirage-complete.png'), fullPage: true });
  expect(errors).toEqual([]);
});

test('Mirage B shows key-specific windows and recovers an early Space with final RMB', async ({ page }, info) => {
  test.setTimeout(50_000);
  await page.goto('./');
  await page.getByRole('combobox', { name: '보스' }).selectOption(content.bossId!);
  const layout = page.getByRole('combobox', { name: 'tile type', exact: true });
  await layout.selectOption('overlap');
  await expect(surface(page)).toHaveAttribute('data-phase', 'ready');
  await expect(page.locator('.cue')).toHaveCount(5);
  await expect(page.locator('.cue-window-base')).toHaveCount(4);
  await expect(page.locator('[data-window-key="MouseRight"]')).toHaveCount(1);
  const lastTile = page.locator('.cue').last();
  await expect(lastTile.locator('.cue-symbol')).toHaveCount(1);
  const shape = await lastTile.locator('.cue-window-hatched').evaluate(element => ({ left: (element as HTMLElement).style.left, width: (element as HTMLElement).style.width }));
  expect(parseFloat(shape.left)).toBeCloseTo(50);
  expect(parseFloat(shape.width)).toBeCloseTo(50);
  await expect(page.getByRole('combobox', { name: '진입 입력' })).toHaveCount(0);
  await expect(page.locator('.lane-preview-note')).toContainText('각 키의 구간에서 하나만');
  await expect(page.getByRole('combobox', { name: '마지막 대응' })).toHaveCount(0);
  await page.getByRole('button', { name: '레인 아이콘 표시' }).click();
  await expect(page.locator('.cue-symbol')).toHaveCount(0);
  await expect(page.locator('.cue > span')).toHaveCount(0);
  await expect(page.locator('.cue-window-base')).toHaveCount(4);
  await page.getByRole('button', { name: '레인 아이콘 표시' }).click();
  await expect(page.locator('.cue-symbol')).toHaveCount(5);
  await expect(page.locator('.cue > span')).toHaveCount(5);
  await page.setViewportSize({ width: 390, height: 844 });
  expect(await page.locator('.practice-scroll').evaluate(element => element.scrollWidth <= element.clientWidth)).toBe(true);
  await page.screenshot({ path: info.outputPath('mirage-b-narrow.png'), fullPage: true });
  await page.setViewportSize({ width: 1280, height: 1080 });
  await page.getByRole('button', { name: '연습 시작' }).click();
  await expect(surface(page)).toHaveAttribute('data-phase', 'running');
  await expect(layout).toBeDisabled();
  await page.keyboard.press('Escape');
  await expect(surface(page)).toHaveAttribute('data-phase', 'paused');
  await expect(layout).toBeEnabled();
  await layout.selectOption('selected');
  await expect(surface(page)).toHaveAttribute('data-phase', 'ready');
  expect(await page.locator('video').evaluate((video: HTMLVideoElement) => video.duration)).toBeCloseTo(content.duration, 2);
  await expect(page.locator('.video-label')).toContainText('마지막 Space');
  expect(await page.locator('video').evaluate((video: HTMLVideoElement) => video.currentTime)).toBe(0);
  await layout.selectOption('overlap');
  await expect(surface(page)).toHaveAttribute('data-phase', 'ready');
  await page.getByRole('button', { name: '연습 시작' }).click();
  await expect(surface(page)).toHaveAttribute('data-phase', 'running');
  const box = (await page.locator('.video-stage').boundingBox())!;
  for (const cue of content.cues.slice(0, 4)) {
    await at(page, cue.reference);
    if (cue.key === 'MouseRight') await page.mouse.click(box.x + box.width / 2, box.y + box.height / 2, { button: 'right' });
    else await page.keyboard.press('Space');
    if (cue === content.cues[1]) await page.screenshot({ path: info.outputPath('mirage-b-running.png'), fullPage: true });
  }
  await at(page, 13.25);
  await page.keyboard.press('Escape');
  await expect(surface(page)).toHaveAttribute('data-phase', 'paused');
  await page.screenshot({ path: info.outputPath('mirage-b-overlap.png'), fullPage: true });
  await page.getByRole('button', { name: '이어서 하기' }).click();
  await expect(surface(page)).toHaveAttribute('data-phase', 'running');
  await at(page, 13.4);
  await page.keyboard.press('Space');
  await at(page, 13.95);
  await page.mouse.click(box.x + box.width / 2, box.y + box.height / 2, { button: 'right' });
  await expect(surface(page)).toHaveAttribute('data-phase', 'finished', { timeout: 8_000 });
  await expect(page.getByRole('heading', { name: '전체 대응 성공' })).toBeVisible();
  await expect(page.getByTestId('extras')).toHaveText('1');
  await expect(page.locator('.result-hit')).toHaveCount(5);
  await page.getByRole('button', { name: '5. 4타 성공' }).click();
  await expect(page.locator('.result-detail')).toContainText('우클릭');
  await page.screenshot({ path: info.outputPath('mirage-b-result.png'), fullPage: true });
  await expect(layout).toBeEnabled();
  await layout.selectOption('selected');
  await expect(surface(page)).toHaveAttribute('data-phase', 'ready');
  await expect(page.locator('.result-hit')).toHaveCount(0);
  expect(await page.locator('video').evaluate((video: HTMLVideoElement) => video.currentTime)).toBe(0);
  await layout.selectOption('overlap');
  await expect(surface(page)).toHaveAttribute('data-phase', 'ready');
  await page.getByRole('combobox', { name: '보스' }).selectOption('vesper');
  await expect(layout).toHaveCount(0);
  await expect(page.locator('.cue-window-base')).toHaveCount(0);
  await page.getByRole('combobox', { name: '보스' }).selectOption(content.bossId!);
  await expect(layout).toHaveValue('overlap');
  await page.reload();
  await page.getByRole('combobox', { name: '보스' }).selectOption(content.bossId!);
  await expect(layout).toHaveValue('overlap');
});

test('Mirage A switches only the final response and its video, retaining the selection', async ({ page }) => {
  test.setTimeout(45_000);
  const rmb = withMirageLastResponse('selected', 'MouseRight');
  await page.goto('./');
  await page.getByRole('combobox', { name: '보스' }).selectOption(content.bossId!);
  const last = page.getByRole('combobox', { name: '마지막 대응' });
  await last.selectOption('MouseRight');
  await expect(surface(page)).toHaveAttribute('data-phase', 'ready');
  expect(await page.locator('video').evaluate((video: HTMLVideoElement) => video.duration)).toBeCloseTo(rmb.duration, 2);
  await expect(page.locator('.video-label')).toContainText('우클릭 → QTE');
  await expect(page.locator('.cue-window-hatched')).toHaveCount(0);
  await page.getByRole('button', { name: '리듬 안내 표시' }).click();
  await page.getByRole('button', { name: '연습 시작' }).click();
  await expect(surface(page)).toHaveAttribute('data-phase', 'running');
  const box = (await page.locator('.video-stage').boundingBox())!;
  for (const cue of rmb.cues) {
    await at(page, (cue.start + cue.end) / 2);
    if (cue.key === 'MouseRight') await page.mouse.click(box.x + box.width / 2, box.y + box.height / 2, { button: 'right' });
    else await page.keyboard.press('Space');
  }
  await expect(surface(page)).toHaveAttribute('data-phase', 'finished', { timeout: 8_000 });
  await expect(page.locator('.result-hit')).toHaveCount(5);
  await expect(page.getByTestId('extras')).toHaveText('0');
  await expect(last).toBeDisabled();
  await page.getByRole('button', { name: '입력 선택으로' }).click();
  await expect(last).toHaveValue('MouseRight');
  await page.getByRole('combobox', { name: '보스' }).selectOption('vesper');
  await expect(last).toHaveCount(0);
  await page.getByRole('combobox', { name: '보스' }).selectOption(content.bossId!);
  await expect(last).toHaveValue('MouseRight');
});
