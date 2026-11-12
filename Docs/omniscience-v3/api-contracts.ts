// Generated from the C# contract types by TypeScriptContracts. Do not edit by hand.
// Instants are UTC epoch milliseconds; durations are milliseconds.

export interface ApiError {
  code: string;
  message: string;
}

export interface CohortDto {
  cohortId: string;
  name: string;
  members: number;
  createdAtMs: number;
  updatedAtMs: number;
}

export interface CohortSelectionDto {
  cohort: CohortDto;
  selection: Selection;
}

export interface DatasetCatalogDto {
  datasets: DatasetDescriptor[];
  dimensions: DimensionDefinition[];
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

export type DayOfWeek = "Sunday" | "Monday" | "Tuesday" | "Wednesday" | "Thursday" | "Friday" | "Saturday";

export interface DimensionBinding {
  dimensionId: string;
  column: string;
}

export interface DimensionDefinition {
  id: string;
  title: string;
  kind: DimensionKind;
  group: string;
  entityType?: string | null;
}

export type DimensionKind = "Time" | "Entity" | "Category" | "Number" | "Flag";

export interface DiscordBackfillRequest {
  channelId: string;
}

export interface DiscordBotStatusDto {
  enabled: boolean;
  attached: boolean;
  botUserId?: string | null;
  contentIntent: boolean;
  sourceStatus: string;
  sourceStatusDetail?: string | null;
  eventsReceived: number;
  eventsWritten: number;
  eventsDropped: number;
  lastEventAtMs?: number | null;
  lastError?: string | null;
  channels: number;
  channelsComplete: number;
  channelsForbidden: number;
  channelsGone: number;
  apiRequests: number;
}

export interface ExploreColumn {
  id: string;
  title: string;
  kind: string;
}

export interface ExploreCoverage {
  sources: string[];
  fromMs?: number | null;
  toMs?: number | null;
  records: number;
  sampled: boolean;
}

export interface ExploreFilter {
  dimension: string;
  op: FilterOp;
  values: unknown[];
}

export interface ExploreQuery {
  dataset: string;
  filters: ExploreFilter[];
  groupBy: string[];
  measures: string[];
  bucket: TimeBucket;
  timeDimension?: string | null;
  lens: TimeLens;
  zone?: string | null;
  asOfMs?: number | null;
  limit: number;
  cursor?: string | null;
}

export interface ExploreRecordsPage {
  recordKind: string;
  records: unknown[];
  nextCursor?: string | null;
  total: number;
}

export interface ExploreRecordsRequest {
  drillToken: string;
  narrow?: ExploreFilter[] | null;
  limit: number;
  cursor?: string | null;
}

export interface ExploreResult {
  columns: ExploreColumn[];
  rows: unknown[][];
  asOfMs: number;
  coverage: ExploreCoverage;
  nextCursor?: string | null;
  drillToken?: string | null;
  explain: string;
}

export type FilterOp = "Eq" | "NotEq" | "In" | "NotIn" | "Between" | "Gte" | "Lte" | "Contains" | "IsNull" | "IsNotNull";

export interface ImportFormatDto {
  id: string;
  title: string;
  needsZone: boolean;
}

export interface ImportInboxEntryDto {
  name: string;
  isFolder: boolean;
  bytes: number;
  modifiedAtMs: number;
}

export interface ImportJobPayload {
  name: string;
  format?: string | null;
  options?: ImportOptions | null;
}

export interface ImportJobStartedDto {
  jobId: number;
}

export interface ImportLedgerEntryDto {
  importId: string;
  format: string;
  sha256: string;
  zone?: string | null;
  state: string;
  createdAtMs: number;
  committedAtMs?: number | null;
  error?: string | null;
  countsJson?: string | null;
}

export interface ImportListDto {
  inboxPath: string;
  inbox: ImportInboxEntryDto[];
  ledger: ImportLedgerEntryDto[];
  formats: ImportFormatDto[];
}

export interface ImportOptions {
  zone?: string | null;
  dayFirst?: boolean | null;
  ownerName?: string | null;
  conversationTitle?: string | null;
  conversationKind?: string | null;
  platform?: string | null;
  columns?: Record<string, string> | null;
}

export interface ImportPreview {
  name: string;
  format: string;
  formatTitle: string;
  sha256: string;
  messages: number;
  systemMessages: number;
  firstMs?: number | null;
  lastMs?: number | null;
  alreadyInCorpus: number;
  committedBeforeMs?: number | null;
  conversations: ImportPreviewConversation[];
  senders: ImportPreviewSender[];
  senderCount: number;
  zoneUsed?: string | null;
  dateOrder?: string | null;
  ambiguousTimes: number;
  invalidTimes: number;
  mediaFound: number;
  mediaMissing: number;
  repairedText: number;
  skippedRecords: number;
  ownerDetected?: string | null;
  unreadableFiles: ImportUnreadableFile[];
  warnings: string[];
  decisionNeeded?: string | null;
}

export interface ImportPreviewConversation {
  conversationId: string;
  title?: string | null;
  kind: string;
  messages: number;
}

export interface ImportPreviewSender {
  name: string;
  identityId: string;
  messages: number;
  isOwner: boolean;
}

export interface ImportResult {
  importId: string;
  format: string;
  sha256: string;
  read: number;
  inserted: number;
  updated: number;
  unchanged: number;
  rejected: number;
  newIdentities: number;
  newConversations: number;
  attachmentsAdded: number;
  attachmentsStored: number;
  mediaTooLarge: number;
  mediaFailed: number;
  skippedRecords: number;
  unreadableFiles: ImportUnreadableFile[];
  warnings: string[];
}

export interface ImportUnreadableFile {
  file: string;
  reason: string;
}

export interface ImportUploadedDto {
  name: string;
  bytes: number;
  previewJobId: number;
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

export type Lane = "Cpu" | "Io" | "LlmBackground" | "LlmInteractive" | "Coordinator";

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

export interface Playbook {
  id: string;
  name: string;
  definition: PlaybookDefinition;
  trigger: PlaybookTrigger;
  enabled: boolean;
  lastRunMs?: number | null;
  createdAtMs: number;
  updatedAtMs: number;
}

export interface PlaybookDefinition {
  steps: PlaybookStep[];
}

export interface PlaybookListDto {
  playbooks: Playbook[];
}

export interface PlaybookRunRecord {
  playbookRunId: string;
  playbookId: string;
  triggerJson?: string | null;
  state: string;
  stepsJson?: string | null;
  error?: string | null;
  createdAtMs: number;
  startedAtMs?: number | null;
  finishedAtMs?: number | null;
}

export interface PlaybookRunRequest {
  playbookId: string;
}

export interface PlaybookSaveRequest {
  id?: string | null;
  name: string;
  definition: PlaybookDefinition;
  trigger: PlaybookTrigger;
  enabled: boolean;
}

export interface PlaybookStep {
  tactic: string;
  parameters?: unknown | null;
  input?: PlaybookStepInput | null;
}

export interface PlaybookStepInput {
  from: string;
  step?: number | null;
  output?: number | null;
  selections?: Selection[] | null;
}

export interface PlaybookTrigger {
  kind: PlaybookTriggerKind;
  at?: string | null;
  days?: DayOfWeek[] | null;
  everyMs?: number | null;
  topic?: string | null;
  match?: Record<string, string> | null;
  minIntervalMs?: number | null;
}

export type PlaybookTriggerKind = "Manual" | "Daily" | "Weekly" | "Every" | "Event";

export interface PlaybookValidationDto {
  problems: string[];
  nextDueMs?: number | null;
}

export interface ReadModelCatalogDto {
  models: ReadModelDescriptor[];
}

export interface ReadModelDescriptor {
  id: string;
  title: string;
  requiredPermission: string;
  schemaVersion: number;
  stalenessTargetMs: number;
  description: string;
}

export interface ReadModelDto {
  model: string;
  key: string;
  version: number;
  schemaVersion: number;
  asOfMs: number;
  sourceSeq: number;
  computedAtMs: number;
  body: unknown;
}

export interface RerunRequest {
  runId: string;
  parameters?: unknown | null;
  asOfMs?: number | null;
  clearAsOf: boolean;
}

export type RestrictedCapability = "FaceLabelling" | "Replica" | "PersonTargetedAlert" | "RoutineInference" | "IdentityFacts" | "TrackingSuggestion" | "ReplicaBasedTactic";

export interface RunActionRequest {
  runId: string;
}

export interface RunActionResultDto {
  runId: string;
  changed: boolean;
  state: string;
}

export interface ScheduleDto {
  name: string;
  dailyAtLocal?: string | null;
  everyMs?: number | null;
}

export interface Selection {
  kind: SelectionKind;
  items: SelectionItem[];
  origin?: string | null;
  sampled: boolean;
  totalBeforeSampling?: number | null;
}

export interface SelectionCombineRequest {
  op: string;
  a: Selection;
  b?: Selection | null;
  k?: number | null;
  order?: SortOrder | null;
}

export interface SelectionItem {
  id: string;
  score?: number | null;
  label?: string | null;
}

export type SelectionKind = "Person" | "Message" | "Conversation" | "Guild" | "Attachment" | "Face" | "FaceCluster" | "Assertion" | "TimeRange" | "ExploreDrill" | "Dataset" | "Question";

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

export type SortOrder = "ScoreDescending" | "ScoreAscending" | "Label" | "Id";

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

export interface TacticCatalogDto {
  tactics: TacticDescriptor[];
}

export type TacticCategory = "Inquiry" | "Transform" | "Monitor" | "Social" | "Investigation" | "Outward";

export interface TacticDescriptor {
  key: string;
  code: string;
  title: string;
  category: TacticCategory;
  description: string;
  accepts: SelectionKind[];
  minSelections: number;
  maxSelections: number;
  parameterSchemaJson: string;
  outputs: TacticOutputKind[];
  lane: Lane;
  requiredPermission: string;
  outbound: boolean;
  minorRestriction?: RestrictedCapability | null;
  version: number;
  typicalDurationMs: number;
  typicalLlmCalls: number;
}

export interface TacticEstimate {
  durationMs: number;
  llmCalls: number;
  items: number;
  willSample: boolean;
  note?: string | null;
}

export interface TacticOutput {
  kind: TacticOutputKind;
  selection?: Selection | null;
  data?: unknown | null;
  summary?: string | null;
}

export type TacticOutputKind = "Answer" | "Cohort" | "MessageSet" | "ImageSet" | "FactSet" | "Dataset" | "Timeline" | "Report" | "AlertRule" | "Reminder" | "ActionProposal";

export interface TacticRunDto {
  runId: string;
  tactic: string;
  tacticVersion: number;
  actor: string;
  state: string;
  input: unknown;
  asOfMs?: number | null;
  currentStage?: string | null;
  progress?: unknown | null;
  outputs?: unknown | null;
  error?: string | null;
  parentRunId?: string | null;
  playbookRunId?: string | null;
  createdAtMs: number;
  startedAtMs?: number | null;
  finishedAtMs?: number | null;
  stale: boolean;
}

export interface TacticRunPageDto {
  items: TacticRunDto[];
  nextCursor?: string | null;
}

export interface TacticRunRequest {
  tactic: string;
  selections: Selection[];
  parameters?: unknown | null;
  asOfMs?: number | null;
  confirmationToken?: string | null;
}

export interface TacticRunStartedDto {
  runId: string;
}

export type TimeBucket = "None" | "Hour" | "Day" | "Week" | "Month" | "Quarter" | "Year" | "HourOfDay" | "DayOfWeek";

export type TimeLens = "Klives" | "Subject" | "Utc";

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
