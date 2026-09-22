import { expect, test } from '@playwright/test';
import { skills } from '../../src/content/skills';

test('in-game input masks stay independent, cover all recordings, and preserve playback', async ({ page }, info) => {
  test.setTimeout(60_000);
  await page.setViewportSize({ width: 1280, height: 1080 });
  await page.goto('./');
  const surface = page.getByRole('region', { name: '연습 입력 영역' });
  const gameToggle = page.getByRole('button', { name: '인게임 입력 가리기' });
  const recordedToggle = page.getByRole('button', { name: '녹화 입력 가리기' });
  await expect(surface).toHaveAttribute('data-phase', 'ready');
  await expect(gameToggle).toHaveAttribute('aria-pressed', 'false');
  await expect(page.locator('.game-input-mask')).toHaveCount(0);
  await gameToggle.click();
  await expect(page.locator('.game-input-mask')).toHaveCount(2);
  await recordedToggle.click();
  await expect(page.locator('.recorded-input-mask')).toHaveCount(0);
  await expect(page.locator('.game-input-mask')).toHaveCount(2);
  await recordedToggle.click();

  for (const content of skills) {
    await page.getByRole('combobox', { name: '보스', exact: true }).selectOption(content.bossId!);
    await page.getByRole('combobox', { name: '제어스킬', exact: true }).selectOption(content.id);
    await expect(surface).toHaveAttribute('data-phase', 'ready');
    await expect(gameToggle).toHaveAttribute('aria-pressed', 'true');
    await expect(page.locator('.game-input-mask')).toHaveCount(2);
    await page.screenshot({ path: info.outputPath(`masked-${content.id}.png`), fullPage: true });
  }
  await page.getByRole('combobox', { name: '마지막 대응', exact: true }).selectOption('MouseRight');
  await expect(surface).toHaveAttribute('data-phase', 'ready');
  await expect(page.locator('.game-input-mask')).toHaveCount(2);
  await page.getByRole('combobox', { name: 'tile type', exact: true }).selectOption('overlap');
  await expect(surface).toHaveAttribute('data-phase', 'ready');
  await page.screenshot({ path: info.outputPath('masked-mirage-rmb.png'), fullPage: true });
  await page.setViewportSize({ width: 390, height: 844 });
  expect(await page.locator('.practice-scroll').evaluate(e => e.scrollWidth <= e.clientWidth)).toBe(true);
  const video = (await page.locator('video').boundingBox())!;
  for (const mask of await page.locator('.game-input-mask').all()) {
    const box = (await mask.boundingBox())!;
    expect(box.x).toBeGreaterThanOrEqual(video.x);
    expect(box.y).toBeGreaterThanOrEqual(video.y);
    expect(box.x + box.width).toBeLessThanOrEqual(video.x + video.width + 1);
    expect(box.y + box.height).toBeLessThanOrEqual(video.y + video.height + 1);
    await expect(mask).toHaveCSS('pointer-events', 'none');
  }
  await page.screenshot({ path: info.outputPath('masked-narrow.png'), fullPage: true });
  await page.setViewportSize({ width: 1280, height: 1080 });
  await page.getByRole('button', { name: '연습 시작', exact: true }).click();
  await expect(surface).toHaveAttribute('data-phase', 'running');
  const before = await page.locator('video').evaluate((v: HTMLVideoElement) => v.currentTime);
  await gameToggle.click();
  await expect(surface).toHaveAttribute('data-phase', 'running');
  await expect(surface).toBeFocused();
  await expect(page.locator('.game-input-mask')).toHaveCount(0);
  expect(await page.locator('video').evaluate((v: HTMLVideoElement) => v.currentTime)).toBeGreaterThanOrEqual(before);
  await gameToggle.click();
  await expect(page.locator('.game-input-mask')).toHaveCount(2);
  await expect(surface).not.toHaveAttribute('data-last-input-time');
  await page.keyboard.press('Escape');
  await expect(surface).toHaveAttribute('data-phase', 'paused');
  await page.getByRole('button', { name: '처음부터', exact: true }).click();
  await expect(surface).toHaveAttribute('data-phase', 'running');
  await expect(gameToggle).toHaveAttribute('aria-pressed', 'true');
  await expect(page.locator('.game-input-mask')).toHaveCount(2);
});
