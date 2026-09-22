import { expect, test } from '@playwright/test';
import type { Page } from '@playwright/test';
import { kusarikkuExecute01 as content } from '../../src/content/skills';

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

test('Kusarikku preparation route plays without adding scored attacks or extra RMB inputs', async ({ page }, info) => {
  test.setTimeout(45_000);
  const errors: string[] = [];
  page.on('pageerror', error => errors.push(error.message));
  await page.setViewportSize({ width: 1280, height: 1080 });
  await page.goto('./');
  await page.getByRole('combobox', { name: '보스' }).selectOption(content.bossId!);
  await expect(surface(page)).toHaveAttribute('data-phase', 'ready');
  await expect(page.locator('.cue')).toHaveCount(5);
  await expect(page.locator('[data-preparation-id]')).toHaveCount(2);
  expect(await page.locator('[data-movement-key]').count()).toBeGreaterThan(0);
  await expect.poll(() => page.locator('video').evaluate((video: HTMLVideoElement) => video.videoWidth)).toBe(1280);
  expect(await page.locator('video').evaluate((video: HTMLVideoElement) => video.duration)).toBeCloseTo(content.duration, 2);
  await page.screenshot({ path: info.outputPath('kusarikku-ready.png'), fullPage: true });
  await page.getByRole('button', { name: '리듬 안내 표시' }).click();
  await expect(page.locator('[data-preparation-id], [data-movement-key]')).toHaveCount(0);
  await page.getByRole('button', { name: '리듬 안내 표시' }).click();
  await page.getByRole('button', { name: '연습 시작' }).click();
  await expect(surface(page)).toHaveAttribute('data-phase', 'running');
  const video = (await page.locator('.video-stage').boundingBox())!;
  const rmb = () => page.mouse.click(video.x + video.width / 2, video.y + video.height / 2, { button: 'right' });
  for (const [index, dodge] of content.preparation!.dodges.entries()) {
    await at(page, dodge.time - .4);
    if (index === 0) await page.screenshot({ path: info.outputPath('kusarikku-preparation.png'), fullPage: true });
    await page.keyboard.press(index === 0 ? 'd' : 'w');
    await at(page, dodge.time);
    await rmb();
  }
  for (const cue of content.cues) {
    await at(page, cue.reference);
    if (cue.key === 'MouseRight') await rmb();
    else await page.keyboard.press('Space');
  }
  await expect(surface(page)).toHaveAttribute('data-phase', 'finished', { timeout: 8_000 });
  await expect(page.getByRole('heading', { name: '전체 대응 성공' })).toBeVisible();
  await expect(page.getByTestId('extras')).toHaveText('0');
  await expect(page.locator('.result-score')).toContainText('/ 5');
  await page.screenshot({ path: info.outputPath('kusarikku-complete.png'), fullPage: true });
  await page.getByRole('button', { name: '다시 연습' }).click();
  await expect(surface(page)).toHaveAttribute('data-phase', 'running');
  expect(await page.locator('video').evaluate((video: HTMLVideoElement) => video.currentTime)).toBeLessThan(1);
  await at(page, content.preparation!.dodges[0].time - .5);
  await page.keyboard.press('Escape');
  await page.setViewportSize({ width: 390, height: 844 });
  await page.screenshot({ path: info.outputPath('kusarikku-narrow.png'), fullPage: true });
  expect(errors).toEqual([]);
});
