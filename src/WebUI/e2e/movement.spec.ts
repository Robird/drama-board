import { expect, test } from '@playwright/test';
import { mkdir, writeFile } from 'node:fs/promises';
import { resolve } from 'node:path';
import { repoRoot, startPublishedServer } from './published-server';
import type { DevView, PlayerView } from '../src/types';

test('published apphost: waiting, A → B → D, refresh and independent diagnostics', async ({ context }, testInfo) => {
  const server = await startPublishedServer();
  const errors: string[] = [];
  const playerRequests: string[] = [];
  try {
    let page = await context.newPage();
    page.on('pageerror', error => errors.push(error.message));
    page.on('request', request => playerRequests.push(new URL(request.url()).pathname));
    const player = async (): Promise<PlayerView> => (await context.request.get(`${server.url}/api/player/view`)).json();
    const dev = async (): Promise<DevView> => (await context.request.get(`${server.url}/api/dev/view`)).json();
    await page.goto(`${server.url}/`);
    await expect(page).toHaveURL(`${server.url}/player`);
    await expect(page.getByRole('button', { name: '前往 B 1000 ms', exact: true })).toBeEnabled();
    const initial = await player();
    expect(initial.status).toBe('waiting');
    expect(initial.modelTimeMs).toBe(0);
    expect(initial.trajectory.map(point => point.placeId)).toEqual(['A']);
    const initialDev = await dev();
    expect(initialDev.transitionCount).toBe(0);
    for (let i = 0; i < 3; i++) {
      const waiting = await player();
      expect(waiting.decision?.decisionId).toBe(initial.decision?.decisionId);
      expect(waiting.modelTimeMs).toBe(0);
    }
    await page.reload();
    await expect(page.getByRole('button', { name: '前往 B 1000 ms', exact: true })).toBeEnabled();
    expect((await player()).decision?.decisionId).toBe(initial.decision?.decisionId);
    expect((await dev()).transitionCount).toBe(0);

    const invalid = await context.request.post(`${server.url}/api/player/decisions`, {
      data: { decisionId: initial.decision!.decisionId, actionKind: 'action.travel', exitId: 'not-an-exit' },
    });
    expect(invalid.status()).toBe(400);
    expect((await player()).decision?.decisionId).toBe(initial.decision?.decisionId);

    await page.getByRole('button', { name: '前往 B 1000 ms', exact: true }).click();
    await expect(page.getByTestId('model-time')).toHaveText('1000 ms');
    await expect(page.getByRole('button', { name: '前往 D 3000 ms', exact: true })).toBeEnabled();
    expect((await player()).trajectory.map(point => point.placeId)).toEqual(['A', 'B']);
    expect((await dev()).transitionCount).toBe(2);
    const old = await context.request.post(`${server.url}/api/player/decisions`, {
      data: { decisionId: initial.decision!.decisionId, actionKind: 'action.travel', exitId: initial.decision!.exits[0].exitId },
    });
    expect(old.status()).toBe(409);
    await page.getByRole('button', { name: '前往 D 3000 ms', exact: true }).click();
    await expect(page.getByTestId('model-time')).toHaveText('4000 ms');
    await expect(page.getByRole('button', { name: '前往 C 1000 ms', exact: true })).toBeEnabled();
    const arrived = await player();
    expect(arrived.runId).toBe(initial.runId);
    expect(arrived.trajectory.map(point => [point.placeId, point.modelTimeMs, point.passageId])).toEqual([
      ['A', 0, null], ['B', 1000, 'ab'], ['D', 4000, 'bd'],
    ]);
    for (const field of ['records', 'lastRejection', 'lastCommittedInstant', 'causeKey', 'fault', 'transitionCount']) {
      expect(arrived).not.toHaveProperty(field);
    }
    await page.reload();
    await expect(page.getByTestId('model-time')).toHaveText('4000 ms');
    await expect(page.getByTestId('trajectory').locator('li')).toHaveCount(3);
    expect((await player()).decision?.decisionId).toBe(arrived.decision?.decisionId);
    await expect(page.locator('svg line.traveled')).toHaveCount(2);
    await page.close();
    page = await context.newPage();
    page.on('pageerror', error => errors.push(error.message));
    page.on('request', request => playerRequests.push(new URL(request.url()).pathname));
    await page.goto(`${server.url}/player`);
    await expect(page.getByTestId('model-time')).toHaveText('4000 ms');
    await expect(page.getByTestId('trajectory').locator('li')).toHaveCount(3);
    const reopened = await player();
    expect(reopened.runId).toBe(arrived.runId);
    expect(reopened.decision?.decisionId).toBe(arrived.decision?.decisionId);
    expect(playerRequests.filter(path => path.startsWith('/api/dev'))).toEqual([]);

    const diagnostic = await context.newPage();
    diagnostic.on('pageerror', error => errors.push(error.message));
    await diagnostic.goto(`${server.url}/dev`);
    await expect(diagnostic.getByTestId('run-id')).toHaveText(initial.runId);
    await expect(diagnostic.getByTestId('transition-count')).toHaveText('4');
    await expect(diagnostic.getByTestId('records').locator('tr')).toHaveCount(4);
    await diagnostic.reload();
    await expect(diagnostic.getByTestId('model-time')).toHaveText('4000 ms');
    const result = await dev();
    expect(result.records.map(record => record.modelTimeMs)).toEqual([0, 1000, 1000, 4000]);
    expect(result.records.every(record => record.causeKey.length > 0 && record.factKinds.length > 0)).toBe(true);
    const missing = await context.request.get(`${server.url}/api/not-a-route`);
    expect(missing.status()).toBe(404);
    expect(missing.headers()['content-type'] ?? '').not.toContain('text/html');
    expect(errors).toEqual([]);
    const output = resolve(repoRoot, 'artifacts/0025');
    await mkdir(output, { recursive: true });
    await page.screenshot({ path: resolve(output, 'player.png'), fullPage: true });
    await diagnostic.screenshot({ path: resolve(output, 'dev.png'), fullPage: true });
    const evidence = JSON.stringify({ cwd: server.cwd, initial, arrived, diagnostic: result }, null, 2);
    await writeFile(resolve(output, 'movement-evidence.json'), evidence);
    await testInfo.attach('movement-evidence', { body: evidence, contentType: 'application/json' });
  } finally {
    await context.close();
    await server.stop();
    await mkdir(resolve(repoRoot, 'artifacts/0025'), { recursive: true });
    await writeFile(resolve(repoRoot, 'artifacts/0025/server.log'), server.getLog());
    await testInfo.attach('server-log', { body: server.getLog(), contentType: 'text/plain' });
  }
});
