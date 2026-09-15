import { useState } from 'react';
import { Layout, Summary } from './layout';
import { useSnapshot } from './poll';
import { readPlayer, submitDecision } from './player-api';
import type { PlayerView } from './types';

function KnownMap({ view }: { view: PlayerView }) {
  const { places, passages } = view.knownMap;
  const traveled = new Set(view.trajectory.map(point => point.passageId));
  const current = view.location;
  const xs = places.map(place => place.x), ys = places.map(place => place.y);
  const minX = Math.min(...xs) - 65, minY = Math.min(...ys) - 60;
  const width = Math.max(...xs) - minX + 65, height = Math.max(...ys) - minY + 60;
  return <svg role="img" aria-label="已知地图，绿色线路为已走过的通道" viewBox={`${minX} ${minY} ${width} ${height}`}>
    {passages.map(passage => {
      const from = places.find(place => place.id === passage.from)!;
      const to = places.find(place => place.id === passage.to)!;
      return <g key={passage.id}><line x1={from.x} y1={from.y} x2={to.x} y2={to.y} className={traveled.has(passage.id) ? 'traveled' : ''} />
        <text className="passage-label" x={(from.x + to.x) / 2 + 12} y={(from.y + to.y) / 2 - 9}>{passage.id}</text></g>;
    })}
    {places.map(place => <g key={place.id} className={place.id === current && view.status !== 'advancing' ? 'place current' : 'place'}>
      <circle cx={place.x} cy={place.y} r="19" /><text x={place.x} y={place.y + 5}>{place.id}</text>
      <text className="place-label" x={place.x} y={place.y + 43}>{place.label}</text>
    </g>)}
  </svg>;
}

export default function PlayerPage() {
  const { view, connectionError } = useSnapshot(readPlayer);
  const [submitting, setSubmitting] = useState(false);
  const [acceptedId, setAcceptedId] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  async function travel(exitId: string) {
    const decision = view?.decision;
    if (!decision || submitting) return;
    setSubmitting(true); setError(null);
    try {
      await submitDecision(decision.decisionId, decision.actionKind, exitId);
      setAcceptedId(decision.decisionId);
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : '连接中断，请等待状态同步。');
    } finally { setSubmitting(false); }
  }
  return <Layout>
    <Summary view={view} connectionError={connectionError} />
    {view && <div className="grid">
      <section className="panel map-panel"><h2>已知地图</h2><p className="muted">本场景全图已知 · 绿色标记自身走过的通道</p><KnownMap view={view} /></section>
      <section className="panel"><h2>可行动作</h2><p className="muted">选择一个相邻出口。等待输入不会推进模型时间。</p>
        <div className="actions">{view.decision?.exits.map(exit => <button key={exit.exitId} onClick={() => void travel(exit.exitId)}
          disabled={connectionError || submitting || acceptedId === view.decision?.decisionId || view.status !== 'waiting'}>
          <span>前往 {exit.destinationId}</span><span>{exit.expectedDurationMs} ms</span>
        </button>)}</div>
        {(submitting || (view.decision && acceptedId === view.decision.decisionId)) && <p role="status">动作已发送，等待服务端提交结果…</p>}
        {!view.decision && <p>{view.status === 'faulted' || view.status === 'stopped' ? '本次运行已停止。' : '正在等待下一次决策。'}</p>}
        {error && <p role="alert" className="warning">{error}</p>}
      </section>
      <section className="panel trajectory"><h2>自身轨迹</h2><ol data-testid="trajectory">{view.trajectory.map((point, index) =>
        <li key={index}><strong>{point.placeId}</strong><span>{point.modelTimeMs} ms</span><span className="muted">{point.passageId ? `经 ${point.passageId} 抵达` : '起点'}</span></li>)}</ol></section>
    </div>}
  </Layout>;
}
