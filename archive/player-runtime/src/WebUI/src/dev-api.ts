import { getJson } from './poll';
import type { DevView } from './types';

export const readDev = (signal: AbortSignal) => getJson<DevView>('/api/dev/view', signal);
