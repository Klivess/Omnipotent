// Generated from the C# contract types by TypeScriptContracts. Do not edit by hand.
// Instants are UTC epoch milliseconds; durations are milliseconds.

export interface ApiError {
  code: string;
  message: string;
}

export interface ClassifyCorrectRequest {
  messageId: string;
  task: string;
  label: string;
  note?: string | null;
}

export interface ClassifyCorrectResultDto {
  messageId: string;
  task: string;
  label: string;
  queued: boolean;
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

export interface ConceptDto {
  conceptId: string;
  name: string;
  description?: string | null;
  state: string;
  headVersion?: number | null;
  threshold?: number | null;
  hits: number;
  updatedAtMs: number;
}

export interface ConceptListDto {
  concepts: ConceptDto[];
}

export interface CorpusTotalsDto {
  messages: number;
  people: number;
  identities: number;
  conversations: number;
  guilds: number;
  attachments: number;
  sources: number;
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

export interface DayCountDto {
  day: string;
  msgs: number;
  authored: number;
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

export interface DirectoryPageDto {
  items: PersonCardDto[];
  nextCursor?: string | null;
  asOfMs?: number | null;
}

export type DirectorySort = "LastSeen" | "Messages" | "Recent" | "Name";

export interface DossierCoverageDto {
  platforms: string[];
  conversations: number;
  guilds: number;
  firstMs?: number | null;
  lastMs?: number | null;
}

export interface DossierDto {
  personId: string;
  displayName: string;
  isSelf: boolean;
  tier: string;
  pinned: boolean;
  ageBand: string;
  restricted: boolean;
  names: NameSpanDto[];
  identities: IdentitySummaryDto[];
  activity: PersonActivityDto;
  series: DayCountDto[];
  hours?: number[] | null;
  weekdays?: number[] | null;
  zone?: ZoneReadingDto | null;
  zoneHistory?: ZoneReadingDto[] | null;
  labels: LabelMixDto;
  languages: RankedCountDto[];
  conversations: RankedCountDto[];
  guilds: RankedCountDto[];
  partners: PartnerSummaryDto[];
  facts: DossierFactDto[];
  coverage: DossierCoverageDto;
  today: string;
  newestEvidenceMs: number;
  staleEvidence: boolean;
}

export interface DossierFactDto {
  assertionId: string;
  attribute: string;
  value?: string | null;
  status: string;
  probability?: number | null;
  validFromEarliestMs?: number | null;
  validFromLatestMs?: number | null;
  validToEarliestMs?: number | null;
  validToLatestMs?: number | null;
  assertedAtMs?: number | null;
  recordedAtMs: number;
  modality?: string | null;
  sourceKind?: string | null;
}

export interface EngineHealthDto {
  modules: ModuleBacklogDto[];
  stores: StoreSizeDto[];
  jobsByState: Record<string, number>;
  llmCallsHour: number;
  llmFailuresHour: number;
  llmCacheEfficiencyHour: number;
  encoder?: unknown | null;
  messageVectors: number;
  rollupDirty: number;
  corpusSeq: number;
  problems: string[];
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

export interface ExploreExportRequest {
  format: ExportFormat;
  drillToken?: string | null;
  query?: ExploreQuery | null;
}

export interface ExploreFacetDto {
  dimension: string;
  values: ExploreFacetValueDto[];
}

export interface ExploreFacetValueDto {
  value: unknown;
  count: number;
}

export interface ExploreFacetsDto {
  dataset: string;
  facets: ExploreFacetDto[];
  asOfMs: number;
}

export interface ExploreFacetsRequest {
  dataset: string;
  filters: ExploreFilter[];
  dimensions: string[];
  limit: number;
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

export type ExportFormat = "Csv" | "Json" | "Parquet";

export type FilterOp = "Eq" | "NotEq" | "In" | "NotIn" | "Between" | "Gte" | "Lte" | "Contains" | "IsNull" | "IsNotNull";

export interface IdentitySummaryDto {
  identityId: string;
  platform: string;
  kind: string;
  firstSeenMs: number;
  lastSeenMs: number;
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

export interface LabelMixDto {
  recent: Record<string, Record<string, number>>;
  baseline: Record<string, Record<string, number>>;
  recentAnswered: Record<string, number>;
  labelset?: number | null;
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

export interface MessageLabelsDto {
  messageId: string;
  labelset: number;
  itemVersion: number;
  gate?: string | null;
  labels: PackedLabelDto[];
  asOfMs: number;
}

export interface MessageLabelsPageDto {
  items: MessageLabelsDto[];
  activeLabelset?: number | null;
}

export interface ModuleBacklogDto {
  moduleId: string;
  enabled: boolean;
  watermark: number;
  backlog: number;
  dirty: number;
  queued: number;
  leased: number;
  dead: number;
  lastSucceededMs?: number | null;
  lastError?: string | null;
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

export interface NameSpanDto {
  kind: string;
  value: string;
  platform: string;
  fromMs: number;
  toMs?: number | null;
}

export interface OverviewDayDto {
  day: string;
  msgs: number;
  people: number;
}

export interface OverviewDto {
  totals: CorpusTotalsDto;
  series: OverviewDayDto[];
  labels30: Record<string, Record<string, number>>;
  topPeople30: RankedCountDto[];
  topGuilds30: RankedCountDto[];
  topConversations30: RankedCountDto[];
  sources: SourceStatusDto[];
  platforms: Record<string, number>;
  firstMs?: number | null;
  lastMs?: number | null;
  labelset?: number | null;
  today: string;
  corpusSeq: number;
}

export interface PackedLabelDto {
  task: string;
  label: string;
  probability: number;
  abstained: boolean;
}

export interface PartnerSummaryDto {
  personId: string;
  name: string;
  repliesOut: number;
  repliesIn: number;
  mentionsOut: number;
  mentionsIn: number;
  dmOut: number;
  dmIn: number;
  reactionsOut: number;
  reactionsIn: number;
  activeDays: number;
  reciprocity: number;
  medianReplyBucketOut?: number | null;
  medianReplyBucketIn?: number | null;
}

export interface PersonActivityDto {
  firstMs?: number | null;
  lastMs?: number | null;
  msgs: number;
  msgs30: number;
  msgs90: number;
  msgs365: number;
  activeDays: number;
  activeDays30: number;
  perActiveDay: number;
  deleted: number;
  edited: number;
  replies: number;
  attachments: number;
  links: number;
  dmShare: number;
  reactionsGiven: number;
  mentionsMade: number;
}

export interface PersonCardDto {
  personId: string;
  displayName: string;
  isSelf: boolean;
  tier: string;
  pinned: boolean;
  ageBand: string;
  platforms: string[];
  firstSeenMs?: number | null;
  lastSeenMs?: number | null;
  msgs: number;
  msgs30: number;
  msgs90: number;
  topGuild?: string | null;
  sentiment90?: Record<string, number> | null;
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

export interface RankedCountDto {
  id: string;
  name: string;
  count: number;
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

export interface ScopeCardDto {
  id: string;
  kind: string;
  title: string;
  platform?: string | null;
  guildId?: string | null;
  guildName?: string | null;
  nameHistory: NameSpanDto[];
  msgs: number;
  msgs30: number;
  msgs90: number;
  firstMs?: number | null;
  lastMs?: number | null;
  series: DayCountDto[];
  topPeople90: RankedCountDto[];
  people90: number;
  labels90: Record<string, Record<string, number>>;
  channels?: RankedCountDto[] | null;
  labelset?: number | null;
  today: string;
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

export interface SourceStatusDto {
  sourceId: string;
  platform: string;
  kind: string;
  label?: string | null;
  status: string;
  detail?: string | null;
  updatedAtMs: number;
}

export interface StoreHealthDto {
  name: string;
  fileBytes: number;
  walBytes: number;
  pendingWrites: number;
  committedBatches: number;
  lastCommitMs: number;
}

export interface StoreSizeDto {
  name: string;
  fileBytes: number;
  walBytes: number;
  pendingWrites: number;
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

export interface TaskCatalogDto {
  tasks: TaskCatalogEntryDto[];
}

export interface TaskCatalogEntryDto {
  id: string;
  title: string;
  question: string;
  labels: string[];
  defaultLabel?: string | null;
  dimensionId?: string | null;
  contextMatters: boolean;
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

export interface ZoneReadingDto {
  zone?: string | null;
  offsetMinutes?: number | null;
  confidence: number;
  method: string;
  month: string;
  alternatives: string[];
}
