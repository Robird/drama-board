import { getJson } from './poll';
import type { PlayerView } from './types';

export const readPlayer = (signal: AbortSignal) => getJson<PlayerView>('/api/player/view', signal);

// Discriminated union: each kind serializes only the fields it owns, never nulls.
export type SubmittedInstruction =
  | { kind: 'move'; passage: string }
  | { kind: 'think'; note: string }
  | { kind: 'stay' };

export async function submitProgram(activationId: string, program: SubmittedInstruction[], workspace: string) {
  const response = await fetch('/api/player/programs', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ activationId, program, workspace }),
  });
  if (response.status === 202) return;
  if (response.status === 409) throw new Error('这次提交已过期或已被处理，请等待最新画面。');
  if (response.status === 400) throw new Error('程序无法接受，请检查指令与工作区后重试。');
  if (response.status === 503) throw new Error('运行已停止接收动作，请查看开发诊断。');
  throw new Error(`提交失败（HTTP ${response.status}），请等待同步后再试。`);
}
