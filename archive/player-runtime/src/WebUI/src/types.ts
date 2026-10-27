export interface InstructionDto {
  kind: 'move' | 'think' | 'stay';
  passage: string | null;
  note: string | null;
}

export interface KnownPassageDto {
  handle: string;
  endpointA: string;
  endpointB: string;
  expectedDurationMs: number;
  enterableFromA: boolean;
  enterableFromB: boolean;
}

export interface ProgrammingRequestDto {
  activationId: string;
  reason: 'MissingInstruction' | 'MoveBlocked' | 'ThinkCompleted';
  reasonDetail: string | null;
  locationText: string;
  knownPassages: KnownPassageDto[];
  workspace: string;
  remainingProgram: InstructionDto[];
}

export interface Snapshot {
  runId: string;
  viewRevision: number;
  status: 'waiting' | 'advancing' | 'staying' | 'faulted' | 'stopped';
  modelTimeMs: number;
  location: string;
}

export interface PlayerView extends Snapshot {
  knownMap: {
    places: { id: string; label: string; x: number; y: number }[];
    passages: { id: string; from: string; to: string }[];
  };
  trajectory: { placeId: string; modelTimeMs: number; passageId: string | null }[];
  workspace: string;
  pendingProgram: InstructionDto[];
  // Non-null only while status === 'waiting'; the single read source for the waking UI.
  request: ProgrammingRequestDto | null;
}

export interface DevView extends Snapshot {
  transitionCount: number;
  lastCommittedInstant: { modelTimeMs: number; causalOrdinal: number } | null;
  records: { modelTimeMs: number; causeKey: string; factKinds: string[] }[];
  processMode: string;
  controlRevision: number;
  pendingActivationId: string | null;
  lastRejection: string | null;
  fault: string | null;
}
