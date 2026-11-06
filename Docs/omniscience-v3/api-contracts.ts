// Generated from the C# contract types by TypeScriptContracts. Do not edit by hand.
// Instants are UTC epoch milliseconds; durations are milliseconds.

export interface ApiError {
  code: string;
  message: string;
}

export interface DatasetDescriptor {
  id: string;
  title: string;
  dimensions: DimensionBinding[];
  measures: MeasureDescriptor[];
  recordKind: string;
  requiredPermission: string;
  minorFiltered: boolean;
}

export interface DimensionBinding {
  dimensionId: string;
  column: string;
}

export interface JobActionRequest {
  jobId: number;
}

export interface JobActionResultDto {
  jobId: number;
  changed: boolean;
  state: string;
}

export interface JobPage {
  items: JobRecord[];
  nextCursor?: number | null;
}

export interface JobRecord {
  jobId: number;
  moduleId: string;
  kind: string;
  subjectKind?: string | null;
  subjectId?: string | null;
  dedupeKey?: string | null;
  priority: number;
  lane: Lane;
  state: string;
  attempts: number;
  maxAttempts: number;
  notBeforeMs: number;
  leaseOwner?: string | null;
  leaseExpiresMs?: number | null;
  payloadJson?: string | null;
  progressJson?: string | null;
  resultJson?: string | null;
  errorClass?: string | null;
  errorMessage?: string | null;
  createdAtMs: number;
  startedAtMs?: number | null;
  finishedAtMs?: number | null;
  parentJobId?: number | null;
  runId?: string | null;
}

export type Lane = "Cpu" | "Io" | "LlmBackground" | "LlmInteractive";

export interface LaneJobsDto {
  lane: Lane;
  queued: number;
  leased: number;
  succeeded: number;
  dead: number;
  cancelled: number;
}

export interface LlmCallDto {
  callId: number;
  atMs: number;
  moduleId: string;
  purpose: string;
  jobId?: number | null;
  model?: string | null;
  provider?: string | null;
  workClass?: string | null;
  promptTokens?: number | null;
  cachedTokens?: number | null;
  completionTokens?: number | null;
  requestBytes?: number | null;
  latencyMs?: number | null;
  outcome: string;
  finishReason?: string | null;
  promptVersion?: string | null;
  error?: string | null;
}

export interface LlmCallPage {
  items: LlmCallDto[];
  nextCursor?: number | null;
}

export type LlmGroupBy = "WorkClass" | "Module" | "Purpose" | "Model" | "Outcome";

export interface LlmStatusDto {
  circuitOpen: boolean;
  backgroundPaused: boolean;
  lastHourByWorkClass: LlmSummaryRowDto[];
}

export interface LlmSummaryDto {
  fromMs?: number | null;
  toMs?: number | null;
  groupBy: LlmGroupBy;
  rows: LlmSummaryRowDto[];
}

export interface LlmSummaryRowDto {
  key: string;
  calls: number;
  ok: number;
  failed: number;
  promptTokens: number;
  cachedTokens: number;
  completionTokens: number;
  cacheHitRate: number;
  p50LatencyMs?: number | null;
  p95LatencyMs?: number | null;
  firstAtMs?: number | null;
  lastAtMs?: number | null;
}

export interface MeasureDescriptor {
  id: string;
  title: string;
  aggregate: string;
  column?: string | null;
  unit: string;
}

export interface ModuleDto {
  id: string;
  version: number;
  title: string;
  category: string;
  description: string;
  dependsOn: string[];
  subscribedChangeKinds: string[];
  schedules: ScheduleDto[];
  defaultLane: Lane;
  minorRestricted: boolean;
  datasets: DatasetDescriptor[];
  settings: SettingDescriptor[];
  health?: ModuleHealth | null;
}

export interface ModuleEnabledRequest {
  moduleId: string;
  enabled: boolean;
}

export interface ModuleHealth {
  moduleId: string;
  title: string;
  version: number;
  enabled: boolean;
  watermark: number;
  backlog: number;
  dirtySubjects: number;
  queued: number;
  leased: number;
  dead: number;
  lastSucceededMs?: number | null;
  lastFailedMs?: number | null;
  lastError?: string | null;
}

export interface ScheduleDto {
  name: string;
  dailyAtLocal?: string | null;
  everyMs?: number | null;
}

export interface SetSettingRequest {
  key: string;
  value: unknown;
}

export interface SettingDescriptor {
  key: string;
  type: string;
  defaultJson: string;
  description: string;
  min?: number | null;
  max?: number | null;
}

export interface SettingDto {
  key: string;
  scope: string;
  type: string;
  defaultJson: string;
  valueJson?: string | null;
  description: string;
  min?: number | null;
  max?: number | null;
  updatedAtMs?: number | null;
}

export interface StoreHealthDto {
  name: string;
  fileBytes: number;
  walBytes: number;
  pendingWrites: number;
  committedBatches: number;
  lastCommitMs: number;
}

export interface StreamEventDto {
  id: number;
  at: number;
  topic: string;
  payload: unknown;
}

export interface StreamEventPage {
  items: StreamEventDto[];
  cursor: number;
  reset?: string | null;
  more: boolean;
}

export interface StreamStatusDto {
  connections: number;
  oldestEventId: number;
  newestEventId: number;
}

export type V3Phase = "Disabled" | "Starting" | "Ready" | "Failed" | "Stopping";

export interface V3StatusDto {
  phase: V3Phase;
  stage: string;
  failure?: string | null;
  nowMs: number;
  startedAtMs?: number | null;
  readyAtMs?: number | null;
  klivesZone?: string | null;
  stores: StoreHealthDto[];
  lanes: LaneJobsDto[];
  modules: ModuleHealth[];
  llm?: LlmStatusDto | null;
  stream?: StreamStatusDto | null;
  routes: string[];
}
