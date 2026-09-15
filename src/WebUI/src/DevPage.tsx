import { Layout, Summary } from './layout';
import { useSnapshot } from './poll';
import { readDev } from './dev-api';

export default function DevPage() {
  const { view, connectionError } = useSnapshot(readDev);
  return <Layout dev>
    <Summary view={view} connectionError={connectionError} />
    {view && <>
      <section className="panel"><h2>运行与 Kernel 游标</h2><p className="muted">只读诊断 · 与 Player 共用同一次运行</p>
        <dl><dt>运行身份</dt><dd data-testid="run-id">{view.runId}</dd><dt>快照修订</dt><dd>{view.viewRevision}</dd>
          <dt>已提交转换</dt><dd data-testid="transition-count">{view.transitionCount}</dd>
          <dt>最后提交时刻</dt><dd>{view.lastCommittedInstant ? `${view.lastCommittedInstant.modelTimeMs} ms / causal ${view.lastCommittedInstant.causalOrdinal}` : '尚无提交'}</dd>
          <dt>待决请求</dt><dd>{view.pendingDecisionId ?? '无'}</dd><dt>最近拒绝</dt><dd>{view.lastRejection ?? '无'}</dd>
          <dt>故障</dt><dd className={view.fault ? 'warning' : ''}>{view.fault ?? '无'}</dd></dl>
      </section>
      <section className="panel"><h2>最近完成记录</h2><div className="table-wrap"><table><thead><tr><th>模型时间</th><th>Cause key</th><th>Facts</th></tr></thead>
        <tbody data-testid="records">{view.records.map((record, index) => <tr key={index}><td>{record.modelTimeMs} ms</td><td><code>{record.causeKey}</code></td><td>{record.factKinds.join(', ')}</td></tr>)}</tbody>
      </table></div>{view.records.length === 0 && <p className="muted">尚无已提交记录，等待 Player 行动。</p>}</section>
    </>}
  </Layout>;
}
