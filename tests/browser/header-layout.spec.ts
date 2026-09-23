import { expect, test } from '@playwright/test';
test('compare headers', async ({page},info)=>{
 await page.setViewportSize({width:1280,height:720}); await page.goto('./');
 await page.getByRole('button',{name:'제어 메뉴 열기'}).focus();
 for(const boss of ['vesper','gir-reborn','larval','mirage-archer-unit','larval']){
 await page.getByRole('combobox',{name:'보스',exact:true}).selectOption(boss);
 await page.mouse.move(640,20); await page.waitForTimeout(200);
 await expect(page.locator('[data-phase]')).toHaveAttribute('data-phase','ready');
 const header=await page.locator('.header-shell').boundingBox();
 expect(header!.x).toBe(0); expect(header!.y).toBe(0); expect(header!.height).toBe(49);
 const video=await page.locator('.video-stage').boundingBox();
 expect(video!.y).toBeCloseTo(0,0);
 await page.mouse.move(640,400);
 await expect(page.locator('.header-shell')).not.toHaveClass(/is-floating/);
 await page.mouse.move(8,40);
 await expect(page.locator('.header-shell')).toHaveClass(/is-floating/);
 await page.screenshot({path:info.outputPath(boss+'.png')});
 }
});
