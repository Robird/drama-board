import { getJson } from './poll';
import type { PlayerView } from './types';

export const readPlayer = (signal: AbortSignal) => getJson<PlayerView>('/api/player/view', signal);

export async function submitDecision(decisionId: string, actionKind: string, exitId: string) {
  const response = await fetch('/api/player/decisions', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ decisionId, actionKind, exitId }),
  });
  if (response.status === 202) return;
  if (response.status === 409) throw new Error('这次决策已经过期或被回答，请等待最新画面。');
  if (response.status === 400) throw new Error('这个动作无法执行，请从当前出口重新选择。');
  if (response.status === 503) throw new Error('运行已停止接收动作，请查看开发诊断。');
  throw new Error(`提交失败（HTTP ${response.status}），请等待同步后再试。`);
}
