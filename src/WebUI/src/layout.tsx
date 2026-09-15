import type { ReactNode } from 'react';
import type { Snapshot } from './types';

export function Layout({ children, dev = false }: { children: ReactNode; dev?: boolean }) {
  return <div className="shell">
    <header><div><span className="eyebrow">DRAMABOARD / FREE PLAY</span><h1>{dev ? '开发诊断' : '自由行动'}</h1></div>
      <nav aria-label="页面"><a href="/player" aria-current={!dev ? 'page' : undefined}>Player</a><a href="/dev" aria-current={dev ? 'page' : undefined}>开发诊断</a></nav>
    </header>
    <main>{children}</main>
    <footer>单角色 · 内存运行。刷新继续本次运行；重启服务回到起点。</footer>
  </div>;
}

export function Summary({ view, connectionError }: { view: Snapshot | null; connectionError: boolean }) {
  const status = connectionError ? '连接失败，正在重试；当前画面可能已过时' : !view ? '正在连接…' :
    view.status === 'waiting' ? '等待你的决策' : view.status === 'advancing' ? '正在处理行动…' : view.status === 'stopped' ? '运行已停止' : '会话故障，已停止接收动作';
  return <section className="panel summary" aria-label="时间与位置">
    <div><span className="caption">当前位置</span><strong data-testid="location">{view?.location ?? '—'}</strong></div>
    <div><span className="caption">模型时间</span><strong data-testid="model-time">{view ? `${view.modelTimeMs} ms` : '—'}</strong></div>
    <p role="status" className={connectionError || view?.status === 'faulted' ? 'warning' : 'status'}>{status}</p>
  </section>;
}
