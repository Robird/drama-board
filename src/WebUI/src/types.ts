export interface Snapshot {
  runId: string;
  viewRevision: number;
  status: 'waiting' | 'advancing' | 'faulted' | 'stopped';
  modelTimeMs: number;
  location: string;
}

export interface PlayerView extends Snapshot {
  knownMap: {
    places: { id: string; label: string; x: number; y: number }[];
    passages: { id: string; from: string; to: string }[];
  };
  trajectory: { placeId: string; modelTimeMs: number; passageId: string | null }[];
  decision: {
    decisionId: string;
    actionKind: string;
    exits: { exitId: string; destinationId: string; expectedDurationMs: number }[];
  } | null;
}

export interface DevView extends Snapshot {
  transitionCount: number;
  lastCommittedInstant: { modelTimeMs: number; causalOrdinal: number } | null;
  records: { modelTimeMs: number; causeKey: string; factKinds: string[] }[];
  pendingDecisionId: string | null;
  lastRejection: string | null;
  fault: string | null;
}
