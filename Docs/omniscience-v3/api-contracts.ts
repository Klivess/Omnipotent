// Generated from the C# contract types by TypeScriptContracts. Do not edit by hand.
// Instants are UTC epoch milliseconds; durations are milliseconds.

export interface ApiError {
  code: string;
  message: string;
}

export interface AssistantVoiceMetrics {
  realSamples: number;
  assistantSamples: number;
  testSamples: number;
  accuracy: number;
  precision: number;
  recall: number;
  auc: number;
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

export interface GenerationConfig {
  temperature: number;
  topP: number;
  candidates: number;
  retrieved: number;
  burstSampling: boolean;
  llmJudge: boolean;
  key: string;
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

export interface ReplicaActionResultDto {
  changed: boolean;
  detail?: string | null;
}

export interface ReplicaAutoRetrainRequest {
  personId: string;
  enabled: boolean;
}

export interface ReplicaBlindAnswerDto {
  index: number;
  choice: string;
  real: string;
  correct: boolean;
  answered: number;
  correctTotal: number;
}

export interface ReplicaBlindAnswerRequest {
  sessionId: string;
  index: number;
  choice: string;
}

export interface ReplicaBlindItemDto {
  index: number;
  stimulus: string;
  optionA: string;
  optionB: string;
  answered?: string | null;
  correct?: boolean | null;
}

export interface ReplicaBlindResultsDto {
  personId: string;
  sessions: number;
  answered: number;
  correct: number;
  accuracy?: number | null;
  recent: ReplicaBlindSessionDto[];
}

export interface ReplicaBlindSessionDto {
  sessionId: string;
  personId: string;
  versionId: string;
  createdAtMs: number;
  items: ReplicaBlindItemDto[];
  answered: number;
  correct: number;
  accuracy?: number | null;
  finishedAtMs?: number | null;
}

export interface ReplicaBlindStartRequest {
  personId: string;
  count: number;
}

export interface ReplicaChatActionRequest {
  chatId: string;
}

export interface ReplicaChatCreateRequest {
  personId: string;
  title?: string | null;
  asOfMs?: number | null;
  contextKind?: string | null;
  addresseePersonId?: string | null;
}

export interface ReplicaChatDto {
  chatId: string;
  kind: string;
  personId: string;
  title?: string | null;
  asOfMs?: number | null;
  contextKind: string;
  addresseePersonId?: string | null;
  archived: boolean;
  createdAtMs: number;
  updatedAtMs: number;
  members: string[];
  topic?: string | null;
}

export interface ReplicaChatMessageDto {
  chatId: string;
  seq: number;
  role: string;
  content: string;
  atMs: number;
  personId?: string | null;
  speaker?: string | null;
  turnId?: string | null;
  status?: string | null;
  error?: string | null;
  simulated: boolean;
  versionId?: string | null;
  generationId?: string | null;
  burstIndex?: number | null;
  burstSize?: number | null;
}

export interface ReplicaChatMessagePageDto {
  items: ReplicaChatMessageDto[];
  nextCursor?: number | null;
}

export interface ReplicaChatPageDto {
  items: ReplicaChatDto[];
  nextCursor?: string | null;
}

export interface ReplicaChatRateRequest {
  chatId: string;
  seq: number;
  rating: number;
  note?: string | null;
}

export interface ReplicaChatSendRequest {
  chatId: string;
  content: string;
}

export interface ReplicaChatUpdateRequest {
  chatId: string;
  title?: string | null;
  archived?: boolean | null;
}

export interface ReplicaCountsDto {
  pairs: number;
  replies: number;
  initiations: number;
  train: number;
  holdoutTune: number;
  holdoutTest: number;
  shadow: number;
  eras: number;
  slices: number;
  personaItems: number;
  personaDropped: number;
  reflexLabelled: number;
  vectors: number;
}

export interface ReplicaEraDto {
  index: number;
  fromMs: number;
  toMs: number;
  pairs: number;
  means: Record<string, number>;
}

export interface ReplicaEvalItemDto {
  pairId: string;
  configKey: string;
  split: string;
  contextKind: string;
  occurredMs: number;
  asOfMs: number;
  stimulus?: string | null;
  realReply?: string | null;
  generatedReply?: string | null;
  outcome: string;
  judgeCorrect?: boolean | null;
  judgeConfidence?: number | null;
  embeddingSimilarity?: number | null;
  styleDistance?: number | null;
  lengthError?: number | null;
  model?: string | null;
}

export interface ReplicaEvalPageDto {
  items: ReplicaEvalItemDto[];
  nextCursor?: string | null;
  textsIncluded: boolean;
}

export interface ReplicaFidelityPointDto {
  versionId: string;
  number: number;
  finishedAtMs: number;
  discriminationAccuracy?: number | null;
  embeddingSimilarity?: number | null;
  styleDistance?: number | null;
  evaluated: number;
}

export interface ReplicaGroupAdvanceRequest {
  chatId: string;
  turns: number;
}

export interface ReplicaGroupCreateRequest {
  personIds: string[];
  topic: string;
  conversationId?: string | null;
  turns: number;
}

export interface ReplicaGroupSendRequest {
  chatId: string;
  content: string;
  turns: number;
}

export interface ReplicaHouseModelDto {
  modelId: string;
  version: number;
  state: string;
  trainedAtMs?: number | null;
  metrics?: AssistantVoiceMetrics | null;
  detail?: string | null;
}

export interface ReplicaJobStartedDto {
  personId: string;
  jobId: number;
}

export interface ReplicaMetricsDto {
  evaluated: number;
  failed: number;
  discriminationAccuracy?: number | null;
  discriminationCiLow?: number | null;
  discriminationCiHigh?: number | null;
  embeddingSimilarity?: number | null;
  styleDistance?: number | null;
  lengthError?: number | null;
  burstError?: number | null;
  configKey?: string | null;
  tuning: ReplicaTuneResultDto[];
  discriminationByContext: Record<string, number>;
  model?: string | null;
  evaluatedAtMs?: number | null;
  note: string;
}

export interface ReplicaModuleStatusDto {
  encoderModel?: string | null;
  encoderAvailable: boolean;
  assistantVoice?: ReplicaHouseModelDto | null;
  replicas: number;
  ready: number;
  building: number;
  failed: number;
  blocked: number;
  stale: number;
  nowMs: number;
}

export interface ReplicaPersonDto {
  personId: string;
  name: string;
  status: string;
  statusDetail?: string | null;
  stale: boolean;
  staleReason?: string | null;
  staleSinceMs?: number | null;
  newMessagesSinceServing: number;
  autoRetrain: boolean;
  readiness?: ReplicaReadinessDto | null;
  serving?: ReplicaVersionSummaryDto | null;
  building?: ReplicaVersionSummaryDto | null;
  fidelityTrend: ReplicaFidelityPointDto[];
  updatedAtMs: number;
}

export interface ReplicaPersonPageDto {
  items: ReplicaPersonDto[];
  nextCursor?: string | null;
}

export interface ReplicaPersonRequest {
  personId: string;
}

export interface ReplicaReadinessDto {
  personId: string;
  name: string;
  tier: ReplicaTier;
  mode: string;
  reason: string;
  replies: number;
  initiations: number;
  effective: number;
  explicitReplies: number;
  repliesToKlives: number;
  authoredMessages: number;
  nonAuthored: number;
  deleted: number;
  minorPeriodExcluded: number;
  byContext: Record<string, number>;
  firstMs?: number | null;
  lastMs?: number | null;
  conversations: number;
  ageBand: string;
  adultFromMs?: number | null;
  thresholdExperimental: number;
  thresholdReady: number;
  isSelf: boolean;
  isBot: boolean;
  truncated: boolean;
  computedAtMs: number;
  sourceSeq: number;
}

export interface ReplicaStageDto {
  stage: string;
  state: string;
  attempts: number;
  startedAtMs?: number | null;
  finishedAtMs?: number | null;
  detail?: string | null;
  error?: string | null;
  total: number;
  completed: number;
  failed: number;
}

export interface ReplicaStreamEventDto {
  type: string;
  chatId: string;
  turnId?: string | null;
  stage?: string | null;
  index?: number | null;
  of?: number | null;
  message?: ReplicaChatMessageDto | null;
  code?: string | null;
  text?: string | null;
}

export interface ReplicaStyleDto {
  bursts: number;
  fromMs: number;
  toMs: number;
  charsP50: number;
  charsP90: number;
  burstShares: number[];
  latencyP50Ms?: number | null;
  casing: Record<string, number>;
  endings: Record<string, number>;
  emojiLines: number;
  topEmoji: string[];
  laughter: string[];
  distinctive: string[];
  scripts: Record<string, number>;
  peakLocalHours: number[];
}

export type ReplicaTier = "Ready" | "Experimental" | "NotPossible" | "Blocked";

export interface ReplicaTrainRequest {
  personId: string;
  reason?: string | null;
}

export interface ReplicaTrainResultDto {
  personId: string;
  versionId: string;
  number: number;
  jobId: number;
  alreadyRunning: boolean;
  readiness?: ReplicaReadinessDto | null;
}

export interface ReplicaTuneResultDto {
  configKey: string;
  temperature: number;
  retrieved: number;
  burstSampling: boolean;
  pairs: number;
  discriminationAccuracy?: number | null;
  embeddingSimilarity?: number | null;
  styleDistance?: number | null;
  chosen: boolean;
}

export interface ReplicaVersionDto {
  summary: ReplicaVersionSummaryDto;
  personId: string;
  moduleVersion: number;
  requestedBy: string;
  eras: ReplicaEraDto[];
  style?: ReplicaStyleDto | null;
  config?: GenerationConfig | null;
}

export interface ReplicaVersionListDto {
  versions: ReplicaVersionSummaryDto[];
}

export interface ReplicaVersionSummaryDto {
  versionId: string;
  number: number;
  state: string;
  reason: string;
  createdAtMs: number;
  finishedAtMs?: number | null;
  dataFromMs?: number | null;
  dataToMs?: number | null;
  encoderModel?: string | null;
  stages: ReplicaStageDto[];
  metrics?: ReplicaMetricsDto | null;
  degraded: string[];
  counts?: ReplicaCountsDto | null;
  error?: string | null;
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
