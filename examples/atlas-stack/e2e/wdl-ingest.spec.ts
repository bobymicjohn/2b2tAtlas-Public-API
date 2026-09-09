import { test, expect, Page } from '@playwright/test';
import { cases } from './wdl-matrix';

// Config from environment (credentials NEVER hard-coded).
const API = process.env.ATLAS_API_URL || 'http://127.0.0.1:5297';
const USER = process.env.ATLAS_USER || '';
const PASS = process.env.ATLAS_PASS || '';

async function login(page: Page): Promise<void> {
  await page.goto('/admin');
  if (page.url().includes('/login')) {
    await page.locator('input').first().fill(USER);           // username
    await page.locator('input[type=password]').fill(PASS);    // password
    await page.getByRole('button', { name: 'Login' }).click();
    await page.waitForURL('**/admin', { timeout: 30_000 });
  }
  // The Blazor app stores the JWT here after login.
  await expect
    .poll(async () => page.evaluate(() => localStorage.getItem('authToken')), { timeout: 20_000 })
    .not.toBeNull();
}

function token(page: Page): Promise<string> {
  return page.evaluate(() => (localStorage.getItem('authToken') || '').replace(/^"|"$/g, ''));
}

async function apiJobs(page: Page, t: string): Promise<any[]> {
  return page.evaluate(
    async ([api, tok]) => {
      const r = await fetch(api + '/api/ingestion-jobs', { headers: { Authorization: 'Bearer ' + tok } });
      return r.ok ? await r.json() : [];
    },
    [API, t] as const,
  );
}

/** Cancel any live (non-cancelled/failed) jobs for a slug so a re-run is idempotent. */
async function cancelSlug(page: Page, t: string, slug: string): Promise<void> {
  const jobs = await apiJobs(page, t);
  const active = jobs.filter((j) => j.slug === slug && j.status !== 'cancelled' && j.status !== 'failed');
  for (const j of active) {
    await page.evaluate(
      async ([api, tok, id]) => {
        await fetch(api + '/api/ingestion-jobs/' + id, { method: 'DELETE', headers: { Authorization: 'Bearer ' + tok } });
      },
      [API, t, j.publicId] as const,
    );
  }
}

test.describe.serial('WDL ingestion', () => {
  test.skip(!USER || !PASS, 'Set ATLAS_USER and ATLAS_PASS environment variables.');

  for (const c of cases) {
    test(c.name, async ({ page }) => {
      await login(page);
      const t = await token(page);
      await cancelSlug(page, t, c.slug);

      // Location Management tab -> "From World Download" opens the upload dialog.
      await page.locator('.rz-tabview-nav').getByText('Location Management', { exact: false }).first().click();
      await page.getByRole('button', { name: /From World Download/i }).click();

      // Drop the archive; wait for the client-side inspect + preview to auto-fill the slug.
      await page.locator('#atlas-wdl-archive').setInputFiles(c.file);
      const slugBox = page.locator('.rz-dialog').locator('input').nth(1);
      await expect(slugBox).not.toHaveValue('', { timeout: 45_000 });

      // Optionally override the auto-selected Attach location (re-derives name + slug).
      if (c.attachLocation) {
        const attach = page.locator('.rz-form-field', { hasText: 'Attach to location' }).locator('.rz-dropdown');
        await attach.click();
        const filter = page.locator('.rz-dropdown-panel:visible').locator('input').first();
        await filter.fill(c.attachLocation);
        await page
          .locator('.rz-dropdown-panel:visible .rz-dropdown-item', { hasText: c.attachLocation })
          .first()
          .click();
      }

      await page.getByRole('button', { name: /Upload and Queue/i }).click();
      // Dialog closes on success (upload + server-side inspection/match can take a while).
      await expect(page.locator('.rz-dialog')).toHaveCount(0, { timeout: 120_000 });

      // Verify the resulting jobs via the API.
      const t2 = await token(page);
      const jobs = (await apiJobs(page, t2)).filter((j) => j.slug === c.slug && j.status !== 'cancelled');
      expect(jobs.map((j) => j.dimension).sort()).toEqual([...c.expect.dimensions].sort());
      if (c.expect.attachedTo !== undefined) {
        for (const j of jobs) expect(j.existingLocationId).toBe(c.expect.attachedTo);
      }
      if (c.expect.allQueued) {
        for (const j of jobs) expect(j.status).toBe('queued');
      }
    });
  }
});
