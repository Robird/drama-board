import { expect, test } from '@playwright/test';
import { mkdir, writeFile } from 'node:fs/promises';
import { resolve } from 'node:path';
import { repoRoot, startPublishedServer } from './published-server';
import type { DevView, PlayerView } from '../src/types';

test('published apphost: programs, think, standby, refresh continuity and independent diagnostics', async ({ context }, testInfo) => {
  const server = await startPublishedServer();
  const errors: string[] = [];
  const playerRequests: string[] = [];
  try {
    const player = async (): Promise<PlayerView> => (await context.request.get(`${server.url}/api/player/view`)).json();
    const dev = async (): Promise<DevView> => (await context.request.get(`${server.url}/api/dev/view`)).json();
    const postProgram = (activationId: string, program: unknown[], workspace: string) =>
      context.request.post(`${server.url}/api/player/programs`, { data: { activationId, program, workspace } });

    let page = await context.newPage();
    page.on('pageerror', error => errors.push(error.message));
    page.on('request', request => playerRequests.push(new URL(request.url()).pathname));
    const programAccepted = () => page.waitForResponse(response =>
      new URL(response.url()).pathname === '/api/player/programs' && response.request().method() === 'POST');

    // 1. Cold start: / redirects to /player, initial waiting request, frozen time.
    await page.goto(`${server.url}/`);
    await expect(page).toHaveURL(`${server.url}/player`);
    await expect(page.getByRole('button', { name: '前往 B 1000 ms', exact: true })).toBeEnabled();
    const initial = await player();
    expect(initial.status).toBe('waiting');
    expect(initial.modelTimeMs).toBe(0);
    expect(initial.location).toBe('A');
    expect(initial.trajectory.map(point => point.placeId)).toEqual(['A']);
    expect(initial.request?.reason).toBe('MissingInstruction');
    expect(initial.request?.locationText).toBe('A');
    const initialDev = await dev();
    expect(initialDev.transitionCount).toBe(0);
    for (let i = 0; i < 3; i++) {
      const waiting = await player();
      expect(waiting.request?.activationId).toBe(initial.request?.activationId);
      expect(waiting.modelTimeMs).toBe(0);
    }

    // 2. Reload keeps the same pending activation; GET never advances the world.
    await page.reload();
    await expect(page.getByRole('button', { name: '前往 B 1000 ms', exact: true })).toBeEnabled();
    expect((await player()).request?.activationId).toBe(initial.request?.activationId);
    expect((await dev()).transitionCount).toBe(0);

    // 3. Strict program contract: unknown passage is rejected without consuming the activation.
    const invalid = await postProgram(initial.request!.activationId, [{ kind: 'move', passage: 'not-a-passage' }], '');
    expect(invalid.status()).toBe(400);
    expect((await player()).request?.activationId).toBe(initial.request?.activationId);

    // 4. Quick one-step move A → B: accept, start, arrival = 3 transitions.
    const quickAccepted = programAccepted();
    await page.getByRole('button', { name: '前往 B 1000 ms', exact: true }).click();
    expect((await quickAccepted).status()).toBe(202);
    await expect(page.getByTestId('model-time')).toHaveText('1000 ms');
    expect((await player()).trajectory.map(point => point.placeId)).toEqual(['A', 'B']);
    expect((await dev()).transitionCount).toBe(3);
    await expect(page.getByRole('button', { name: '前往 D 3000 ms', exact: true })).toBeEnabled();

    // 5. The consumed activation is stale now.
    const stale = await postProgram(initial.request!.activationId, [{ kind: 'stay' }], '');
    expect(stale.status()).toBe(409);

    // 6. Program editor at B: Think with a non-empty workspace.
    await page.getByTestId('add-instruction').click();
    await page.getByLabel('指令 1 种类').selectOption('think');
    await page.getByLabel('指令 1 笔记').fill('先想清楚再出发');
    await page.getByTestId('workspace-input').fill('目标：D');
    const thinkAccepted = programAccepted();
    await page.getByTestId('submit-program').click();
    expect((await thinkAccepted).status()).toBe(202);
    await expect(page.getByTestId('request-reason')).toContainText('思考完成');
    await expect(page.getByTestId('model-time')).toHaveText('2000 ms');
    const afterThink = await player();
    expect(afterThink.status).toBe('waiting');
    expect(afterThink.request?.reason).toBe('ThinkCompleted');
    expect(afterThink.workspace).toContain('目标：D');
    expect(afterThink.workspace).toContain('[1000] 先想清楚再出发');
    expect((await dev()).transitionCount).toBe(6);

    // 7. Refresh at the post-think waiting point: activation, workspace and remaining program survive.
    await page.reload();
    await expect(page.getByTestId('request-reason')).toContainText('思考完成');
    await expect(page.getByTestId('workspace-input')).toHaveValue(afterThink.workspace);
    await expect(page.getByTestId('program-rows').getByTestId('program-row')).toHaveCount(0);
    const refreshed = await player();
    expect(refreshed.runId).toBe(initial.runId);
    expect(refreshed.request?.activationId).toBe(afterThink.request?.activationId);
    expect(refreshed.workspace).toBe(afterThink.workspace);
    expect(refreshed.request?.remainingProgram).toEqual(afterThink.request?.remainingProgram);

    // 8. Editor move B → D carrying the workspace.
    await page.getByTestId('add-instruction').click();
    await page.getByLabel('指令 1 通路').selectOption('bd');
    const moveAccepted = programAccepted();
    await page.getByTestId('submit-program').click();
    expect((await moveAccepted).status()).toBe(202);
    await expect(page.getByTestId('model-time')).toHaveText('5000 ms');
    const arrived = await player();
    expect(arrived.status).toBe('waiting');
    expect(arrived.location).toBe('D');
    expect(arrived.trajectory.map(point => [point.placeId, point.modelTimeMs, point.passageId])).toEqual([
      ['A', 0, null], ['B', 1000, 'ab'], ['D', 5000, 'bd'],
    ]);
    expect(arrived.workspace).toContain('目标：D');
    expect((await dev()).transitionCount).toBe(9);
    await expect(page.locator('svg line.traveled')).toHaveCount(2);

    // 9. Empty program: normalized to [Stay], started, then staying with frozen time.
    const standbyAccepted = programAccepted();
    await page.getByTestId('standby').click();
    expect((await standbyAccepted).status()).toBe(202);
    await expect(page.getByTestId('status')).toHaveText('待机中：世界继续，等待没有安排');
    await expect(page.getByTestId('model-time')).toHaveText('5000 ms');
    await expect(page.getByTestId('trajectory').locator('li')).toHaveCount(3);
    await expect(page.getByTestId('pending-program')).toBeVisible();
    await expect(page.getByTestId('workspace-display')).toContainText('目标：D');
    const standby = await player();
    expect(standby.status).toBe('staying');
    expect(standby.request).toBeNull();
    expect(standby.modelTimeMs).toBe(5000);
    expect(standby.trajectory.map(point => point.placeId)).toEqual(['A', 'B', 'D']);
    const rejected = await postProgram(arrived.request!.activationId, [{ kind: 'stay' }], standby.workspace);
    expect(rejected.status()).toBe(409);
    expect((await player()).status).toBe('staying');

    // 10. Diagnostics stay independent from the player surface.
    await page.close();
    page = await context.newPage();
    page.on('pageerror', error => errors.push(error.message));
    page.on('request', request => playerRequests.push(new URL(request.url()).pathname));
    await page.goto(`${server.url}/player`);
    await expect(page.getByTestId('status')).toHaveText('待机中：世界继续，等待没有安排');
    await expect(page.getByTestId('model-time')).toHaveText('5000 ms');
    await expect(page.getByTestId('trajectory').locator('li')).toHaveCount(3);
    const staying = await player();
    expect(staying.runId).toBe(initial.runId);
    for (const field of ['records', 'lastRejection', 'lastCommittedInstant', 'causeKey', 'fault', 'transitionCount', 'processMode', 'controlRevision', 'pendingActivationId']) {
      expect(staying).not.toHaveProperty(field);
    }
    expect(playerRequests.filter(path => path.startsWith('/api/dev'))).toEqual([]);

    const diagnostic = await context.newPage();
    diagnostic.on('pageerror', error => errors.push(error.message));
    await diagnostic.goto(`${server.url}/dev`);
    await expect(diagnostic.getByTestId('run-id')).toHaveText(initial.runId);
    const result = await dev();
    expect(result.transitionCount).toBe(11); // 3 (move) + 3 (think) + 3 (move) + 2 (stay)
    expect(result.records.length).toBe(result.transitionCount);
    await expect(diagnostic.getByTestId('transition-count')).toHaveText('11');
    await expect(diagnostic.getByTestId('records').locator('tr')).toHaveCount(result.records.length);
    expect(result.records.every(record => record.causeKey.length > 0 && record.factKinds.length > 0)).toBe(true);
    const missing = await context.request.get(`${server.url}/api/not-a-route`);
    expect(missing.status()).toBe(404);
    expect(missing.headers()['content-type'] ?? '').not.toContain('text/html');
    expect(errors).toEqual([]);

    // 11. Evidence.
    const output = resolve(repoRoot, 'artifacts/0026');
    await mkdir(output, { recursive: true });
    await page.screenshot({ path: resolve(output, 'player.png'), fullPage: true });
    await diagnostic.screenshot({ path: resolve(output, 'dev.png'), fullPage: true });
    const evidence = JSON.stringify({ cwd: server.cwd, initial, afterThink, arrived, standby, diagnostic: result }, null, 2);
    await writeFile(resolve(output, 'movement-evidence.json'), evidence);
    await testInfo.attach('movement-evidence', { body: evidence, contentType: 'application/json' });
  } finally {
    await context.close();
    await server.stop();
    await mkdir(resolve(repoRoot, 'artifacts/0026'), { recursive: true });
    await writeFile(resolve(repoRoot, 'artifacts/0026/server.log'), server.getLog());
    await testInfo.attach('server-log', { body: server.getLog(), contentType: 'text/plain' });
  }
});
