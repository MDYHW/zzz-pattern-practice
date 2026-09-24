import { expect, test } from '@playwright/test';
import type { Page } from '@playwright/test';
import { phaethonExecute01, phaethonIntegratedExecute02 } from '../../src/content/skills';

const surface = (page: Page) => page.getByRole('region', { name: '연습 입력 영역' });
async function at(page: Page, time: number) {
  await page.waitForFunction(target => {
    const video = document.querySelector('video')!;
    const phase = document.querySelector<HTMLElement>('[data-phase]')?.dataset.phase;
    if (video.currentTime >= target) return true;
    if (['paused', 'interrupted', 'error', 'finished'].includes(phase ?? ''))
      throw new Error(`Playback stopped: ${phase} at ${video.currentTime}, target ${target}`);
    return false;
  }, time);
}

for (const content of [phaethonExecute01, phaethonIntegratedExecute02]) {
  test(`${content.bossLabel} keeps its own route and completes ${content.cues.length} scored cues`, async ({ page }, info) => {
    test.setTimeout(45_000);
    const errors: string[] = [];
    page.on('pageerror', error => errors.push(error.message));
    page.on('response', response => { if (response.status() >= 400) errors.push(`${response.status()} ${response.url()}`); });
    await page.setViewportSize({ width: 1280, height: 1080 });
    await page.goto('./');
    const boss = page.getByRole('combobox', { name: '보스', exact: true });
    await expect(boss.locator('option')).toHaveCount(8);
    await boss.selectOption(content.bossId!);
    await expect(surface(page)).toHaveAttribute('data-phase', 'ready');
    await expect(page.getByRole('combobox', { name: '제어스킬' })).toHaveValue(content.id);
    await expect(page.getByRole('combobox', { name: '진입 입력' })).toHaveCount(0);
    await expect(page.locator('.cue')).toHaveCount(content.cues.length);
    await expect(page.locator('[data-preparation-id]')).toHaveCount(content.preparation?.dodges.length ?? 0);
    await expect(page.locator('[data-movement-key]')).toHaveCount(0);
    await expect.poll(() => page.locator('video').evaluate((video: HTMLVideoElement) => video.videoWidth)).toBe(1280);
    expect(await page.locator('video').evaluate((video: HTMLVideoElement) => video.duration)).toBeCloseTo(content.duration, 2);
    if (content.preparation) {
      await expect(page.locator('.entry-selection')).toContainText('준비 회피 1회는');
      await expect(page.locator('.entry-selection')).not.toContainText('이동');
      await expect(page.locator('.preparation-note')).toContainText('준비 회피는');
    } else {
      await expect(page.locator('.preparation-note')).toHaveCount(0);
    }
    await page.screenshot({ path: info.outputPath(`${content.id}-ready.png`), fullPage: true });
    await page.getByRole('button', { name: '연습 시작' }).click();
    await expect(surface(page)).toHaveAttribute('data-phase', 'running');
    const box = (await page.locator('.video-stage').boundingBox())!;
    const rmb = () => page.mouse.click(box.x + box.width / 2, box.y + box.height / 2, { button: 'right' });
    if (content.preparation) {
      await at(page, Math.max(0, content.preparation.dodges[0].time - .15));
      await rmb();
      await expect(page.locator('.cue-success')).toHaveCount(0);
      await expect(surface(page)).not.toHaveAttribute('data-last-input-time');
    }
    for (const cue of content.cues) {
      await at(page, cue.reference);
      if (cue.key === 'MouseRight') await rmb();
      else await page.keyboard.press('Space');
    }
    await expect(surface(page)).toHaveAttribute('data-phase', 'finished', { timeout: 8_000 });
    await expect(page.getByRole('heading', { name: '전체 대응 성공' })).toBeVisible();
    await expect(page.locator('.result-hit')).toHaveCount(content.cues.length);
    await expect(page.getByTestId('extras')).toHaveText('0');
    await page.screenshot({ path: info.outputPath(`${content.id}-complete.png`), fullPage: true });
    expect(errors).toEqual([]);
  });
}

test.describe('touch route', () => {
test.use({ viewport: { width: 844, height: 390 }, hasTouch: true });
test('Phaethon route stays usable with touch controls in compact landscape', async ({ page }, info) => {
  test.setTimeout(30_000);
  await page.goto('./?mobile=1');
  await page.getByRole('button', { name: '제어 메뉴 열기' }).tap();
  await page.getByRole('combobox', { name: '보스', exact: true }).selectOption(phaethonExecute01.bossId!);
  await page.getByRole('button', { name: '제어 메뉴 닫기' }).tap();
  await expect(surface(page)).toHaveAttribute('data-phase', 'ready');
  await expect(page.locator('.cue')).toHaveCount(4);
  await expect(page.locator('[data-preparation-id]')).toHaveCount(1);
  await expect(page.getByRole('button', { name: '회피', exact: true })).toBeDisabled();
  await page.screenshot({ path: info.outputPath('phaethon-mobile-ready.png') });
  await page.getByRole('button', { name: '연습 시작', exact: true }).tap();
  await at(page, Math.max(0, phaethonExecute01.preparation!.dodges[0].time - .15));
  await page.getByRole('button', { name: '회피', exact: true }).tap();
  await expect(page.locator('.cue-success')).toHaveCount(0);
  await expect(surface(page)).not.toHaveAttribute('data-last-input-time');
  await at(page, phaethonExecute01.cues[0].reference);
  await page.getByRole('button', { name: '회피', exact: true }).tap();
  await expect(page.locator('.cue-success')).toHaveCount(1);
  await page.screenshot({ path: info.outputPath('phaethon-mobile-entry.png') });
});
});
