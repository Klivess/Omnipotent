// Generated from the C# contract types by TypeScriptContracts. Do not edit by hand.
// Instants are UTC epoch milliseconds; durations are milliseconds.

export interface AgeBandDto {
  personId: string;
  band: string;
  restricted: boolean;
  markedMinorByKlives: boolean;
  atMs: number;
  statements: AgeStatementDto[];
}

export interface AgeReviewRequest {
  evidenceId: string;
  verdict: string;
  note?: string | null;
}

export interface AgeStatementDto {
  evidenceId: string;
  kind: string;
  status: string;
  sourceKind: string;
  assertedAtMs: number;
  messageId: string;
  probability: number;
  quote?: string | null;
}

export interface AgendaDto {
  asOfMs: number;
  zone: string;
  upcoming: PlanItemDto[];
  overdue: PlanItemDto[];
  klivesPromises: PlanItemDto[];
}

export interface AliasDto {
  aliasId: string;
  personId: string;
  alias: string;
  nameKind: string;
  scopeKind: string;
  scopeId?: string | null;
  validFromMs: number;
  validToMs?: number | null;
  usages: number;
  speakers: number;
  selfClaims: number;
  probability: number;
  status: string;
  commonWord: boolean;
  alertEligible: boolean;
}

export interface AliasReviewRequest {
  aliasId: string;
  verdict: string;
  note?: string | null;
}

export interface ApiError {
  code: string;
  message: string;
}

export interface AskPlanDto {
  tactic: string;
  parameters?: unknown | null;
  restatement: string;
  people: PersonCandidateDto[];
  reason?: string | null;
}

export interface AskRequest {
  question: string;
  personIds?: string[] | null;
  tactic?: string | null;
  asOfMs?: number | null;
  standing: boolean;
}

export interface AskStartedDto {
  runId: string;
  tactic: string;
}

export interface AssertionDetailDto {
  fact: KnowledgeFactDto;
  naturalKey: string;
  modality?: string | null;
  derivationJson?: string | null;
  detailJson?: string | null;
  evidence: EvidenceContextDto[];
  history: AssertionVersionDto[];
  goldVerdict?: string | null;
  goldDecidedAtMs?: number | null;
}

export interface AssertionVersionDto {
  assertionId: string;
  status: string;
  probability?: number | null;
  recordedAtMs: number;
  supersededAtMs?: number | null;
  retractedAtMs?: number | null;
  value?: string | null;
  validFromLatestMs?: number | null;
  validToLatestMs?: number | null;
  promptVersion?: string | null;
  model?: string | null;
}

export interface ChoiceDto {
  option: string;
  probability: number;
}

export interface ClaimDto {
  claimId: string;
  runId: string;
  personId: string;
  attribute: string;
  category: string;
  value?: string | null;
  modality?: string | null;
  sourceKind?: string | null;
  probability?: number | null;
  agreement: string;
  verdict: string;
  reason?: string | null;
  critic?: string | null;
  assertedAtMs: number;
  recordedAtMs: number;
  promptVersion: string;
  model?: string | null;
  conversationId?: string | null;
}

export interface ClaimPageDto {
  items: ClaimDto[];
  nextCursor?: string | null;
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

export interface CoverageDto {
  subjects: string[];
  fromMs?: number | null;
  toMs: number;
  messagesConsidered: number;
  factsConsidered: number;
  itemsInDossier: number;
  excludedAsJokeOrFiction: number;
  excludedAfterAsOf: number;
  vectorSearch: boolean;
  platforms: string[];
  notes: string[];
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

export interface DisambiguationDto {
  mention: string;
  candidates: PersonCandidateDto[];
}

export interface EntityDto {
  entityId: string;
  ownerPersonId: string;
  kind: string;
  name: string;
  attributesJson: string;
  firstEvidenceMs: number;
  lastEvidenceMs: number;
  mentions: number;
  status: string;
}

export interface EvidenceContextDto {
  messageId: string;
  version: number;
  role: string;
  quote?: string | null;
  weight: number;
  sentAtMs?: number | null;
  authorPersonId?: string | null;
  authorName?: string | null;
  conversationId?: string | null;
  conversationKind?: string | null;
  conversationTitle?: string | null;
  guildId?: string | null;
  guildName?: string | null;
  platform?: string | null;
  deleted: boolean;
  edited: boolean;
  text?: string | null;
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

export interface ExtractionRunDto {
  runId: string;
  personId: string;
  jobId?: number | null;
  promptVersion: string;
  model?: string | null;
  slices: number;
  messages: number;
  claimsRaw: number;
  claimsAgreed: number;
  claimsVerified: number;
  claimsRejected: number;
  rejectionsJson?: string | null;
  llmCalls: number;
  outcome: string;
  startedAtMs: number;
  finishedAtMs?: number | null;
}

export interface ExtractionRunPageDto {
  items: ExtractionRunDto[];
  nextCursor?: string | null;
}

export interface FactReviewRequest {
  assertionId: string;
  verdict: string;
  value?: string | null;
  validFromMs?: number | null;
  validToMs?: number | null;
  note?: string | null;
}

export type FilterOp = "Eq" | "NotEq" | "In" | "NotIn" | "Between" | "Gte" | "Lte" | "Contains" | "IsNull" | "IsNotNull";

export interface HypothesisDto {
  hypothesisId: string;
  personId: string;
  statement: string;
  attribute?: string | null;
  probability: number;
  status: string;
  createdAtMs: number;
  expiresAtMs: number;
  resolvedAtMs?: number | null;
  resolutionNote?: string | null;
  derivedFromJson: string;
}

export interface IdentityLinkDecisionDto {
  link: IdentityLinkDto;
  merge?: MergeResultDto | null;
}

export interface IdentityLinkDecisionRequest {
  linkId: string;
  accept: boolean;
  confirm: boolean;
  note?: string | null;
}

export interface IdentityLinkDto {
  linkId: string;
  identityA: string;
  identityB: string;
  personA: string;
  personB: string;
  signals: LinkSignalDto[];
  independentSignals: number;
  probability: number;
  verdict: string;
  judgeJson?: string | null;
  createdAtMs: number;
  updatedAtMs: number;
  decidedAtMs?: number | null;
  mergeId?: number | null;
}

export interface IdentityLinkPageDto {
  items: IdentityLinkDto[];
  nextCursor?: string | null;
}

export type InquiryAnswerType = "Binary" | "Choice" | "Ranking" | "Numeric" | "DateInterval" | "Open" | "ChangePoint" | "Prediction";

export interface InquiryEvidenceDto {
  id: string;
  kind: string;
  messageId?: string | null;
  assertionId?: string | null;
  authorPersonId?: string | null;
  atMs: number;
  quote: string;
  conversationId?: string | null;
  guildId?: string | null;
  platform?: string | null;
  where?: string | null;
  reliability: string;
  facet: string;
}

export interface InquiryFeedbackRequest {
  inquiryId: string;
  feedback: string;
  note?: string | null;
}

export interface InquiryPageDto {
  items: InquiryRowDto[];
  nextCursor?: string | null;
}

export interface InquiryReportDto {
  inquiryId: string;
  runId: string;
  question: string;
  restatement: string;
  answerType: InquiryAnswerType;
  verdict: InquiryVerdict;
  probability?: number | null;
  probabilityRaw?: number | null;
  bandLow?: number | null;
  bandHigh?: number | null;
  calibrated: boolean;
  distribution: ChoiceDto[];
  ranking: RankedPersonDto[];
  estimate?: number | null;
  changeFromMs?: number | null;
  changeToMs?: number | null;
  evidenceFor: InquiryEvidenceDto[];
  evidenceAgainst: InquiryEvidenceDto[];
  reasoning?: string | null;
  temporalNote?: string | null;
  wouldChange: string[];
  questionsToAsk: string[];
  coverage: CoverageDto;
  evidenceStrength: number;
  passes: number;
  passesDropped: number;
  disagreement: number;
  devilsAdvocate?: string | null;
  priorProbability?: number | null;
  priorRationale?: string | null;
  worldKnowledgeDependent: boolean;
  isPrediction: boolean;
  policyNote?: string | null;
  disambiguation: DisambiguationDto[];
  asOfMs: number;
  asOfNote?: string | null;
  promptVersion: string;
  model?: string | null;
  assertionId?: string | null;
  standingId?: string | null;
}

export interface InquiryRowDto {
  inquiryId: string;
  runId: string;
  tacticKey: string;
  question: string;
  restatement?: string | null;
  answerType: string;
  primarySubjectId?: string | null;
  asOfMs: number;
  verdict?: string | null;
  probability?: number | null;
  state: string;
  feedback?: string | null;
  standingId?: string | null;
  createdAtMs: number;
}

export type InquiryVerdict = "Yes" | "LeansYes" | "Uncertain" | "LeansNo" | "No" | "InsufficientEvidence" | "Refused" | "NeedsDisambiguation" | "Answered";

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

export interface KnowledgeCategoryDto {
  category: string;
  facts: KnowledgeFactDto[];
}

export interface KnowledgeFactDto {
  assertionId: string;
  attribute: string;
  attributeTitle: string;
  category: string;
  value: string;
  polarity: string;
  objectPersonId?: string | null;
  status: string;
  probability: number;
  storedProbability: number;
  calibrated: boolean;
  validFromEarliestMs?: number | null;
  validFromLatestMs?: number | null;
  validToEarliestMs?: number | null;
  validToLatestMs?: number | null;
  lastEvidenceMs?: number | null;
  firstEvidenceMs?: number | null;
  recordedAtMs: number;
  sourceKind?: string | null;
  sensitivity: string;
  producer?: string | null;
}

export interface KnowledgeFactsPageDto {
  personId: string;
  mode: string;
  atMs: number;
  minorRestricted: boolean;
  items: KnowledgeFactDto[];
  nextCursor?: string | null;
}

export interface KnowledgeJobStartedDto {
  jobId: number;
  detail?: string | null;
}

export interface KnowledgeProfileDto {
  personId: string;
  asOfMs: number;
  minorRestricted: boolean;
  categories: KnowledgeCategoryDto[];
  inferences: KnowledgeFactDto[];
  coveredMessages: number;
  lastExtractMs?: number | null;
  backlogPending: boolean;
  promptVersion: string;
}

export interface KnowledgeReviewItemDto {
  itemId: string;
  kind: string;
  subjectId?: string | null;
  title: string;
  detailJson?: string | null;
  priority: number;
  state: string;
  createdAtMs: number;
  updatedAtMs: number;
  resolvedAtMs?: number | null;
  resolution?: string | null;
  actor?: string | null;
}

export interface KnowledgeReviewPageDto {
  asOfMs: number;
  items: KnowledgeReviewItemDto[];
  nextCursor?: string | null;
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

export interface LifeEventDto {
  eventId: string;
  category: string;
  title: string;
  attribute: string;
  assertionId: string;
  earliestMs?: number | null;
  latestMs?: number | null;
  midpointMs?: number | null;
  precision: string;
  probability: number;
  evidenceCount: number;
  evidenceMessageIds: string[];
}

export interface LifeTimelineDto {
  personId: string;
  asOfMs: number;
  events: LifeEventDto[];
  totalEvents: number;
}

export interface LinkActionRequest {
  linkId: string;
}

export interface LinkSignalDto {
  kind: string;
  strength: number;
  detail: string;
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

export interface MergeRequest {
  personA: string;
  personB: string;
  confirm: boolean;
  reason?: string | null;
}

export interface MergeResultDto {
  mergeId: number;
  survivor: string;
  absorbed: string;
  changed: boolean;
  rule: string;
}

export interface MergeRowDto {
  mergeId: number;
  fromPersonId: string;
  intoPersonId: string;
  mergedAtMs: number;
  actor: string;
  reason?: string | null;
  undoneAtMs?: number | null;
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

export interface PersonCandidateDto {
  personId: string;
  label: string;
  detail?: string | null;
}

export interface PersonRequest {
  personId: string;
}

export interface PlanItemDto {
  planId: string;
  kind: string;
  subtype?: string | null;
  ownerPersonId: string;
  counterpartyPersonId?: string | null;
  involvesKlives: boolean;
  klivesPromised: boolean;
  title: string;
  scheduledEarliestMs?: number | null;
  scheduledLatestMs?: number | null;
  nextOccurrenceMs?: number | null;
  localZone?: string | null;
  allDay: boolean;
  firmness: string;
  status: string;
  recurrence?: string | null;
  evidenceCount: number;
  evidenceMessageIds: string[];
  conversationId?: string | null;
}

export interface PlanPageDto {
  items: PlanItemDto[];
  nextCursor?: string | null;
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

export interface QualityRunRequest {
  kind: string;
}

export interface RankedPersonDto {
  personId: string;
  label: string;
  score: number;
  reason?: string | null;
  evidenceIds: string[];
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

export interface ReviewResolveRequest {
  itemId: string;
  state: string;
  resolution: string;
}

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

export interface StandingEnabledRequest {
  questionId: string;
  enabled: boolean;
}

export interface StandingQuestionDto {
  questionId: string;
  question: string;
  personIds: string[];
  enabled: boolean;
  minIntervalMs: number;
  lastEvaluatedMs?: number | null;
  lastVerdict?: string | null;
  lastProbability?: number | null;
  pending: boolean;
  createdAtMs: number;
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

export interface UnmergeRequest {
  mergeId: number;
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
