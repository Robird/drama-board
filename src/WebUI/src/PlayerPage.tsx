import { useRef, useState } from 'react';
import { Layout, Summary } from './layout';
import { useSnapshot } from './poll';
import { readPlayer, submitProgram, type SubmittedInstruction } from './player-api';
import type { InstructionDto, PlayerView, ProgrammingRequestDto } from './types';

const reasonLabels: Record<ProgrammingRequestDto['reason'], string> = {
  MissingInstruction: '缺少指令',
  MoveBlocked: '行动受阻',
  ThinkCompleted: '思考完成',
};

function statusNote(view: PlayerView): string {
  if (view.status === 'advancing') return '正在执行已接受的程序；到达指令边界后会再次请求安排。';
  if (view.status === 'staying') return '当前指令为待机（Stay）：世界继续，等待没有安排；保留的后续安排可见但不会自动执行。';
  if (view.status === 'faulted') return '会话故障，已停止接收动作；请查看开发诊断。';
  if (view.status === 'stopped') return '本次运行已停止。';
  return '正在准备唤醒材料…';
}

function instructionParts(instruction: InstructionDto): { label: string; detail: string } {
  if (instruction.kind === 'move') return { label: `移动 ${instruction.passage ?? ''}`, detail: '沿已知通路前往另一端' };
  if (instruction.kind === 'think') return { label: '思考', detail: instruction.note ?? '' };
  return { label: '待机', detail: '世界继续，等待没有安排' };
}

// Known enterable directions from the frozen request material. Dynamic closure is
// not filtered here; it surfaces later as the formal MoveBlocked reason.
function quickTargets(request: ProgrammingRequestDto): { handle: string; place: string; durationMs: number }[] {
  return request.knownPassages.flatMap(passage => {
    if (passage.endpointA === request.locationText && passage.enterableFromA)
      return [{ handle: passage.handle, place: passage.endpointB, durationMs: passage.expectedDurationMs }];
    if (passage.endpointB === request.locationText && passage.enterableFromB)
      return [{ handle: passage.handle, place: passage.endpointA, durationMs: passage.expectedDurationMs }];
    return [];
  });
}

interface EditorRow { id: number; kind: 'move' | 'think' | 'stay'; passage: string; note: string }

function toSubmitted(row: EditorRow): SubmittedInstruction | null {
  if (row.kind === 'move') return row.passage ? { kind: 'move', passage: row.passage } : null;
  if (row.kind === 'think') { const note = row.note.trim(); return note ? { kind: 'think', note } : null; }
  return { kind: 'stay' };
}

// Remounts per activationId, so rows and workspace always prefill from the frozen request.
function ProgramEditor({ request, disabled, onSubmit }: {
  request: ProgrammingRequestDto;
  disabled: boolean;
  onSubmit: (program: SubmittedInstruction[] | null, workspace: string) => void;
}) {
  const [rows, setRows] = useState<EditorRow[]>(() => request.remainingProgram.map((instruction, index) => ({
    id: index, kind: instruction.kind, passage: instruction.passage ?? '', note: instruction.note ?? '',
  })));
  const [workspace, setWorkspace] = useState(request.workspace);
  const nextId = useRef(request.remainingProgram.length);
  const addRow = () => setRows(current =>
    [...current, { id: nextId.current++, kind: 'move', passage: request.knownPassages[0]?.handle ?? '', note: '' }]);
  const updateRow = (id: number, patch: Partial<EditorRow>) =>
    setRows(current => current.map(row => row.id === id ? { ...row, ...patch } : row));
  const removeRow = (id: number) => setRows(current => current.filter(row => row.id !== id));
  const submitRows = () => {
    const program: SubmittedInstruction[] = [];
    for (const row of rows) {
      const instruction = toSubmitted(row);
      if (!instruction) { onSubmit(null, workspace); return; }
      program.push(instruction);
    }
    onSubmit(program, workspace);
  };
  return <div className="program-editor" data-testid="program-editor">
    <div className="program-rows" data-testid="program-rows">
      {rows.map((row, index) => <div className="program-row" data-testid="program-row" key={row.id}>
        <select aria-label={`指令 ${index + 1} 种类`} value={row.kind} disabled={disabled}
          onChange={event => updateRow(row.id, { kind: event.target.value as EditorRow['kind'] })}>
          <option value="move">移动</option><option value="think">思考</option><option value="stay">待机</option>
        </select>
        {row.kind === 'move' && <select aria-label={`指令 ${index + 1} 通路`} value={row.passage} disabled={disabled}
          onChange={event => updateRow(row.id, { passage: event.target.value })}>
          {request.knownPassages.map(passage => <option key={passage.handle} value={passage.handle}>
            {passage.handle}（{passage.endpointA}–{passage.endpointB}，{passage.expectedDurationMs} ms）</option>)}
        </select>}
        {row.kind === 'think' && <input aria-label={`指令 ${index + 1} 笔记`} value={row.note} disabled={disabled}
          placeholder="思考笔记（必填）" onChange={event => updateRow(row.id, { note: event.target.value })} />}
        {row.kind === 'stay' && <span className="muted">待机指令没有参数。</span>}
        <button type="button" className="secondary compact" aria-label={`删除指令 ${index + 1}`} disabled={disabled}
          onClick={() => removeRow(row.id)}>删除</button>
      </div>)}
    </div>
    <button type="button" className="secondary compact" data-testid="add-instruction" disabled={disabled} onClick={addRow}>添加指令</button>
    <div className="workspace-box">
      <label htmlFor="workspace-input">工作区（目标与判断，随程序整体提交）</label>
      <textarea id="workspace-input" data-testid="workspace-input" value={workspace} disabled={disabled}
        onChange={event => setWorkspace(event.target.value)} />
    </div>
    <div className="editor-actions">
      <button type="button" data-testid="submit-program" disabled={disabled} onClick={submitRows}>提交程序</button>
      <button type="button" className="secondary" data-testid="standby" disabled={disabled}
        onClick={() => onSubmit([], workspace)}>清空并待机</button>
    </div>
    <p className="muted">「清空并待机」提交空程序：清空后续安排，服务端会规范为一条待机（Stay）指令并结束本次自动推进。</p>
  </div>;
}

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
  const request = view?.status === 'waiting' ? view.request : null;

  async function submit(target: ProgrammingRequestDto, program: SubmittedInstruction[] | null, workspace: string) {
    if (submitting) return;
    if (program === null) { setError('程序无法接受，请检查指令与工作区后重试。'); return; }
    setSubmitting(true); setError(null);
    try {
      await submitProgram(target.activationId, program, workspace);
      setAcceptedId(target.activationId);
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : '连接中断，请等待状态同步。');
    } finally { setSubmitting(false); }
  }

  const controlsDisabled = !request || connectionError || submitting || acceptedId === request.activationId;

  return <Layout>
    <Summary view={view} connectionError={connectionError} />
    {view && <div className="grid">
      <section className="panel map-panel"><h2>已知地图</h2><p className="muted">本场景全图已知 · 绿色标记自身走过的通道</p><KnownMap view={view} /></section>
      {request ? <section className="panel">
        <h2>唤醒材料</h2><p className="muted">本次唤醒的冻结快照；快捷一步只列出当前地点已知可通行的方向。</p>
        <p data-testid="request-reason">唤醒原因：<strong>{reasonLabels[request.reason]}</strong></p>
        {request.reasonDetail && <p className="muted" data-testid="request-reason-detail">{request.reasonDetail}</p>}
        <p>当前位置 <strong data-testid="request-location">{request.locationText}</strong></p>
        <div className="table-wrap"><table data-testid="known-passages">
          <thead><tr><th>通路</th><th>两端</th><th>时长</th></tr></thead>
          <tbody>{request.knownPassages.map(passage => <tr key={passage.handle}>
            <td><code>{passage.handle}</code></td><td>{passage.endpointA} ↔ {passage.endpointB}</td><td>{passage.expectedDurationMs} ms</td>
          </tr>)}</tbody>
        </table></div>
        <h3>快捷一步</h3>
        <div className="actions">{quickTargets(request).map(target => <button key={target.handle} disabled={controlsDisabled}
          onClick={() => void submit(request, [{ kind: 'move', passage: target.handle }], request.workspace)}>
          <span>前往 {target.place}</span><span>{target.durationMs} ms</span>
        </button>)}</div>
        {quickTargets(request).length === 0 && <p className="muted">当前没有已知可通行的方向。</p>}
      </section> : <section className="panel"><h2>当前状态</h2><p data-testid="status-note">{statusNote(view)}</p></section>}
      {request ? <section className="panel program-panel">
        <h2>程序安排</h2><p className="muted">提交完整的后续程序与工作区；提交即授权按序执行。</p>
        <ProgramEditor key={request.activationId} request={request} disabled={controlsDisabled}
          onSubmit={(program, workspace) => void submit(request, program, workspace)} />
        {error && <p role="alert" className="warning">{error}</p>}
        {(submitting || acceptedId === request.activationId) && <p role="status">程序已发送，等待服务端提交结果…</p>}
      </section> : <section className="panel program-panel" data-testid="pending-program">
        <h2>待执行程序</h2>
        <ol>{view.pendingProgram.map((instruction, index) => {
          const parts = instructionParts(instruction);
          return <li key={index}><strong>{parts.label}</strong><span className="muted">{parts.detail}</span></li>;
        })}</ol>
        {view.pendingProgram.length === 0 && <p className="muted">没有已安排的指令。</p>}
        <h3>工作区（只读）</h3>
        <p data-testid="workspace-display" className="preformatted">{view.workspace || '（空）'}</p>
      </section>}
      <section className="panel trajectory"><h2>自身轨迹</h2><ol data-testid="trajectory">{view.trajectory.map((point, index) =>
        <li key={index}><strong>{point.placeId}</strong><span>{point.modelTimeMs} ms</span><span className="muted">{point.passageId ? `经 ${point.passageId} 抵达` : '起点'}</span></li>)}</ol></section>
    </div>}
  </Layout>;
}
