import { expect, test, type Page } from '@playwright/test';
import { vesselExecute01 as content } from '../../src/content/skills';

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

test('Vessel preparation is unscored and five desktop responses complete successfully', async ({ page }, info) => {
  test.setTimeout(45_000);
  const errors: string[] = [];
  page.on('pageerror', error => errors.push(error.message));
  page.on('response', response => { if (response.status() >= 400) errors.push(`${response.status()} ${response.url()}`); });
  await page.setViewportSize({ width: 1280, height: 1080 });
  await page.goto('./');
  const boss = page.getByRole('combobox', { name: '보스', exact: true });
  await expect(boss.locator('option')).toHaveCount(7);
  await boss.selectOption('vessel');
  await expect(page.getByRole('combobox', { name: '제어스킬' })).toHaveValue(content.id);
  await expect(page.getByRole('combobox', { name: '진입 입력' })).toHaveCount(0);
  await expect(page.locator('.cue')).toHaveCount(5);
  await expect(page.locator('[data-preparation-id]')).toHaveCount(2);
  await expect(page.locator('[data-movement-key="W"]')).toHaveCount(1);
  await expect.poll(() => page.locator('video').evaluate((v: HTMLVideoElement) => v.videoWidth)).toBe(1280);
  expect(await page.locator('video').evaluate((v: HTMLVideoElement) => v.duration)).toBeCloseTo(1175 / 60, 2);
  await page.screenshot({ path: info.outputPath('vessel-ready.png'), fullPage: true });
  await page.getByRole('button', { name: '연습 시작', exact: true }).click();
  const box = (await page.locator('.video-stage').boundingBox())!;
  const rmb = () => page.mouse.click(box.x + box.width / 2, box.y + box.height / 2, { button: 'right' });
  for (const dodge of content.preparation!.dodges) {
    await at(page, dodge.time - .15);
    await rmb();
    await expect(surface(page)).not.toHaveAttribute('data-last-input-time');
  }
  for (const cue of content.cues) {
    await at(page, cue.reference);
    if (cue.key === 'MouseRight') await rmb();
    else await page.keyboard.press('Space');
  }
  await expect(surface(page)).toHaveAttribute('data-phase', 'finished', { timeout: 6000 });
  await expect(page.getByRole('heading', { name: '전체 대응 성공' })).toBeVisible();
  await expect(page.locator('.result-hit')).toHaveCount(5);
  await expect(page.getByTestId('extras')).toHaveText('0');
  await page.screenshot({ path: info.outputPath('vessel-complete.png'), fullPage: true });
  await boss.selectOption('larval');
  await expect(surface(page)).toHaveAttribute('data-phase', 'ready');
  await expect(page.locator('.results')).toHaveCount(0);
  await expect(page.locator('[data-preparation-id]')).toHaveCount(2);
  expect(errors).toEqual([]);
});

for (const viewport of [{ width: 844, height: 390 }, { width: 390, height: 844 }]) {
  test.describe(`Vessel touch ${viewport.width}x${viewport.height}`, () => {
    test.use({ viewport, hasTouch: true });
    test('preparation, entry and result use the existing touch controls', async ({ page }, info) => {
      test.setTimeout(45_000);
      await page.goto('./?mobile=1');
      await page.getByRole('button', { name: '제어 메뉴 열기' }).tap();
      await page.getByRole('combobox', { name: '보스', exact: true }).selectOption('vessel');
      await page.screenshot({ path: info.outputPath('vessel-menu.png') });
      await page.getByRole('button', { name: '제어 메뉴 닫기' }).tap();
      // Set the viewport to the existing header stop without a wheel gesture overshooting it.
      await page.locator('.practice-scroll').evaluate(scroller => {
        scroller.scrollTop = scroller.querySelector('.masthead')!.getBoundingClientRect().height;
      });
      await expect(page.locator('.cue')).toHaveCount(5);
      await expect(page.locator('.game-input-mask')).toHaveCount(1);
      await expect(page.locator('.guide')).toBeInViewport();
      await expect(page.getByRole('button', { name: '지원', exact: true })).toBeInViewport();
      expect((await page.locator('.video-stage').boundingBox())!.y).toBeGreaterThanOrEqual(0);
      await page.getByRole('button', { name: '연습 시작', exact: true }).tap();
      const dodgeBox = (await page.getByRole('button', { name: '회피', exact: true }).boundingBox())!;
      const assistBox = (await page.getByRole('button', { name: '지원', exact: true }).boundingBox())!;
      const tap = (key: string) => {
        const box = key === 'MouseRight' ? dodgeBox : assistBox;
        // Avoid locator actionability polling adding >150ms to a real-time input.
        return page.touchscreen.tap(box.x + box.width / 2, box.y + box.height / 2);
      };
      for (const dodge of content.preparation!.dodges) {
        await at(page, dodge.time - .15);
        await tap('MouseRight');
        await expect(surface(page)).not.toHaveAttribute('data-last-input-time');
      }
      for (const cue of content.cues) {
        await at(page, cue.reference);
        await tap(cue.key);
        if (cue === content.cues[0]) await page.screenshot({ path: info.outputPath('vessel-touch-entry.png') });
      }
      await expect(surface(page)).toHaveAttribute('data-phase', 'finished', { timeout: 6000 });
      await expect(page.locator('.result-hit')).toHaveCount(5);
      await expect(page.getByTestId('extras')).toHaveText('0');
      await page.screenshot({ path: info.outputPath('vessel-touch-complete.png') });
    });
  });
}
