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

export interface AlertDto {
  alertId: string;
  kind: string;
  ruleId?: string | null;
  ruleName?: string | null;
  reason: string;
  messageId: string;
  conversationId: string;
  conversationTitle?: string | null;
  guildId?: string | null;
  platform: string;
  personId?: string | null;
  personName?: string | null;
  occurredAtMs: number;
  lastHitMs: number;
  hitCount: number;
  hits: AlertHitDto[];
  probability?: number | null;
  state: string;
  delivery: string;
  sourceDeletedAtMs?: number | null;
  createdAtMs: number;
  verdict?: string | null;
}

export interface AlertFeedbackDto {
  alertId: string;
  kind: string;
  reason: string;
  messageId: string;
  verdict: AlertVerdict;
}

export interface AlertFeedbackRequest {
  alertId: string;
  verdict: AlertVerdict;
  messageId?: string | null;
  note?: string | null;
}

export interface AlertFeedbackResultDto {
  feedback: AlertFeedbackDto;
  classifierCorrected: boolean;
}

export interface AlertHitDto {
  messageId: string;
  conversationId: string;
  atMs: number;
  reason: string;
  probability?: number | null;
  personId?: string | null;
  excerpt?: string | null;
  deleted: boolean;
}

export interface AlertPageDto {
  items: AlertDto[];
  nextCursor?: string | null;
  unseen: number;
}

export type AlertState = "New" | "Seen" | "Dismissed";

export interface AlertStateRequest {
  state: AlertState;
  alertIds?: string[] | null;
  all: boolean;
  kind?: string | null;
}

export interface AlertStateResultDto {
  changed: number;
  unseen: number;
}

export type AlertVerdict = "Correct" | "Wrong";

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

export interface AnalysisOffListDto {
  people: AnalysisOffPersonDto[];
}

export interface AnalysisOffPersonDto {
  personId: string;
  name?: string | null;
  sinceMs: number;
}

export interface AnalysisOnDto {
  personId: string;
  changed: boolean;
}

export interface AnalysisOnRequest {
  personId: string;
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

export interface AssistantVoiceMetrics {
  realSamples: number;
  assistantSamples: number;
  testSamples: number;
  accuracy: number;
  precision: number;
  recall: number;
  auc: number;
}

export interface BackupDto {
  backupId: string;
  createdAtMs: number;
  trigger: string;
  keyId: string;
  files: BackupFileDto[];
}

export interface BackupFileDto {
  store: string;
  file: string;
  plainBytes: number;
  encryptedBytes: number;
  sha256: string;
}

export interface BackupKeyDto {
  keyId: string;
  exported?: string | null;
}

export interface BackupKeyExportRequest {
  passphrase: string;
}

export interface BackupKeyImportRequest {
  exported: string;
  passphrase: string;
}

export interface BackupListDto {
  backups: BackupDto[];
  keyId?: string | null;
  keep: number;
  enabled: boolean;
}

export interface BackupRequestedDto {
  jobId: number;
}

export interface BackupRestoreRequest {
  backupId: string;
}

export interface BridgeDto {
  personId: string;
  name?: string | null;
  communityId: string;
  bridgeTo: string;
  outsideShare: number;
}

export interface BriefFactDto {
  assertionId: string;
  attribute: string;
  value?: string | null;
  status: string;
  recordedAtMs: number;
}

export interface BriefMoodDto {
  recent: Record<string, number>;
  baseline: Record<string, number>;
  recentAnswered: number;
  baselineAnswered: number;
}

export interface BriefOpenThreadDto {
  itemId: string;
  kind: string;
  messageId: string;
  conversationId: string;
  sentAtMs: number;
}

export interface BriefSectionDto {
  key: string;
  title: string;
  points: CitedPoint[];
}

export interface BriefStyleDto {
  messages: number;
  averageWords: number;
  averageCharacters: number;
  replyShare: number;
  directShare: number;
  languages: string[];
}

export interface BundleDeleteRequest {
  bundleId: string;
}

export interface BundleDeletedDto {
  bundleId: string;
  deleted: boolean;
}

export interface BundleDto {
  bundleId: string;
  title: string;
  note?: string | null;
  items: BundleItemDto[];
  createdBy: string;
  createdAtMs: number;
  updatedAtMs: number;
}

export interface BundleExportDto {
  bundleId: string;
  format: string;
  fileName: string;
  content: string;
}

export interface BundleItemDto {
  kind: string;
  id: string;
  label?: string | null;
}

export interface BundleListDto {
  bundles: BundleDto[];
}

export interface ChoiceDto {
  option: string;
  probability: number;
}

export interface CitedPoint {
  text: string;
  refs: string[];
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

export interface CommunitiesDto {
  communities: CommunityDto[];
  bridges: BridgeDto[];
  people: number;
  modularity: number;
  asOfMs: number;
  note: string;
}

export interface CommunityDto {
  communityId: string;
  size: number;
  weight: number;
  top: CommunityMemberDto[];
}

export interface CommunityMemberDto {
  personId: string;
  name?: string | null;
  strength: number;
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

export interface DayCountDto {
  day: string;
  msgs: number;
  authored: number;
}

export type DayOfWeek = "Sunday" | "Monday" | "Tuesday" | "Wednesday" | "Thursday" | "Friday" | "Saturday";

export interface DigestDto {
  digestId: string;
  scopeKind: string;
  scopeId?: string | null;
  scopeTitle?: string | null;
  fromMs: number;
  toMs: number;
  state: string;
  trigger: string;
  messages: number;
  messagesRead: number;
  conversations: number;
  people: number;
  nothingNew: boolean;
  overall: CitedPoint[];
  sections: DigestSectionDto[];
  notable: string[];
  newFacts: DigestFactDto[];
  error?: string | null;
  llmCalls: number;
  createdAtMs: number;
  finishedAtMs?: number | null;
}

export interface DigestFactDto {
  assertionId: string;
  personId: string;
  personName?: string | null;
  attribute: string;
  value?: string | null;
  recordedAtMs: number;
}

export interface DigestPageDto {
  items: DigestDto[];
  nextCursor?: string | null;
}

export interface DigestRunRequest {
  scopeKind: string;
  scopeId?: string | null;
  fromMs?: number | null;
  toMs?: number | null;
}

export interface DigestSectionDto {
  kind: string;
  id: string;
  title: string;
  messages: number;
  people: number;
  lastMessageMs: number;
  points: CitedPoint[];
  children: DigestSectionDto[];
}

export interface DigestSeenDto {
  scopeKind: string;
  scopeId?: string | null;
  seen: boolean;
}

export interface DigestSeenRequest {
  scopeKind: string;
  scopeId?: string | null;
  atMs?: number | null;
}

export interface DigestSubscriptionDeleteRequest {
  subscriptionId: string;
}

export interface DigestSubscriptionDeletedDto {
  subscriptionId: string;
  deleted: boolean;
}

export interface DigestSubscriptionDto {
  subscriptionId: string;
  name: string;
  scopeKind: string;
  scopeId?: string | null;
  cadence: string;
  at: string;
  weekday?: number | null;
  enabled: boolean;
  lastRunMs?: number | null;
  createdAtMs: number;
}

export interface DigestSubscriptionListDto {
  subscriptions: DigestSubscriptionDto[];
}

export interface DigestSubscriptionSaveRequest {
  name: string;
  scopeKind: string;
  cadence: string;
  at: string;
  subscriptionId?: string | null;
  scopeId?: string | null;
  weekday?: number | null;
  enabled: boolean;
}

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

export interface DisambiguationDto {
  mention: string;
  candidates: PersonCandidateDto[];
}

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
  analysisOff: boolean;
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

export interface ForgetConfirmRequest {
  personId: string;
  confirmToken: string;
}

export interface ForgetPreviewDto {
  personId: string;
  name?: string | null;
  persons: number;
  identities: number;
  messages: number;
  directChatMessagesOfKlives: number;
  directChats: number;
  attachments: number;
  mediaFiles: number;
  mediaFilesLeft: number;
  reactions: number;
  events: number;
  facts: number;
  includeDirectChats: boolean;
  level: string;
  confirmToken: string;
  expiresAtMs: number;
  notes: string[];
}

export interface ForgetPreviewRequest {
  personId: string;
  includeDirectChats: boolean;
  level: string;
}

export interface ForgetRunDto {
  runId: string;
  state: string;
  counts: Record<string, number>;
  error?: string | null;
  startedAtMs: number;
  finishedAtMs?: number | null;
}

export interface GateDecision {
  subject: string;
  candidate: string;
  baseline?: string | null;
  decision: string;
  reasons: string[];
  decidedAtMs: number;
}

export interface GenerationConfig {
  temperature: number;
  topP: number;
  candidates: number;
  retrieved: number;
  burstSampling: boolean;
  llmJudge: boolean;
  key: string;
}

export interface HealthIssueDto {
  issueId: string;
  kind: string;
  subject?: string | null;
  severity: string;
  detail: unknown;
  firstMs: number;
  lastMs: number;
  resolvedMs?: number | null;
}

export interface HealthListDto {
  open: HealthIssueDto[];
  resolved: HealthIssueDto[];
  enabled: boolean;
  checkedAtMs?: number | null;
}

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

export interface IdentitySummaryDto {
  identityId: string;
  platform: string;
  kind: string;
  firstSeenMs: number;
  lastSeenMs: number;
}

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

export type InboxAction = "Dismiss" | "Snooze" | "Reopen";

export interface InboxItemDto {
  itemId: string;
  kind: string;
  messageId: string;
  conversationId: string;
  conversationKind?: string | null;
  conversationTitle?: string | null;
  guildId?: string | null;
  platform: string;
  personId: string;
  personName?: string | null;
  sentAtMs: number;
  waitingMs: number;
  state: string;
  reason: string;
  probability?: number | null;
  snoozedUntilMs?: number | null;
  live: boolean;
  excerpt?: string | null;
  strength: number;
  rank: number;
}

export interface InboxPageDto {
  items: InboxItemDto[];
  open: number;
  asOfMs: number;
}

export interface InboxResolveRequest {
  itemIds: string[];
  action: InboxAction;
  untilMs?: number | null;
}

export interface InboxResolveResultDto {
  changed: number;
  open: number;
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

export interface InsightsRefreshDto {
  jobId: number;
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

export interface LinkCountDto {
  key: string;
  label?: string | null;
  shares: number;
}

export interface LinkDto {
  urlId: string;
  url: string;
  host: string;
  site: string;
  provider?: string | null;
  kind: string;
  sensitive: boolean;
  title?: string | null;
  author?: string | null;
  description?: string | null;
  metaSource?: string | null;
  metaAtMs?: number | null;
  topic?: string | null;
  summary?: string | null;
  shares: number;
  sharers: number;
  firstSharedMs?: number | null;
  lastSharedMs?: number | null;
}

export interface LinkSearchDto {
  items: LinkShareDto[];
  truncated: boolean;
}

export interface LinkShareDto {
  messageId: string;
  personId?: string | null;
  personName?: string | null;
  conversationId: string;
  conversationTitle?: string | null;
  guildId?: string | null;
  atMs: number;
  reactions: number;
  link: LinkDto;
}

export interface LinkSignalDto {
  kind: string;
  strength: number;
  detail: string;
}

export interface LinkTrackDto {
  target: string;
  targetKind: string;
  link?: LinkDto | null;
  shares: number;
  sharers: number;
  reactions: number;
  firstAtMs?: number | null;
  lastAtMs?: number | null;
  first?: LinkShareDto | null;
  people: LinkCountDto[];
  places: LinkCountDto[];
  months: LinkCountDto[];
  topLinks: LinkDto[];
  items: LinkShareDto[];
  truncated: boolean;
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

export interface MapPointDto {
  personId: string;
  name?: string | null;
  x: number;
  y: number;
  communityId?: string | null;
  messages: number;
}

export interface MeasureDescriptor {
  id: string;
  title: string;
  aggregate: string;
  column?: string | null;
  unit: string;
}

export interface MediaSearchDto {
  query: string;
  hits: MediaSearchHitDto[];
  visualAvailable: boolean;
  imagesEmbedded: number;
  note?: string | null;
}

export interface MediaSearchHitDto {
  attachmentId: string;
  messageId: string;
  conversationId: string;
  authorPersonId?: string | null;
  authorName?: string | null;
  sentAtMs: number;
  deleted: boolean;
  caption?: string | null;
  tags: string[];
  imageText?: string | null;
  score: number;
  why: string[];
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

export interface MonitorEventDto {
  eventId: string;
  kind: string;
  ruleId?: string | null;
  personId?: string | null;
  personName?: string | null;
  guildId?: string | null;
  conversationId?: string | null;
  atMs: number;
  data: unknown;
  notified: boolean;
}

export interface MonitorEventPageDto {
  items: MonitorEventDto[];
}

export interface MonitorRuleChangedDto {
  watchId: string;
  changed: boolean;
}

export interface MonitorRuleDto {
  watchId: string;
  kind: string;
  name: string;
  enabled: boolean;
  definition: unknown;
  createdAtMs: number;
  updatedAtMs: number;
  fired30d: number;
  lastFiredMs?: number | null;
}

export interface MonitorRuleEnabledRequest {
  watchId: string;
  enabled: boolean;
}

export interface MonitorRuleListDto {
  rules: MonitorRuleDto[];
}

export interface MonitorRuleRequest {
  watchId: string;
}

export interface NameSpanDto {
  kind: string;
  value: string;
  platform: string;
  fromMs: number;
  toMs?: number | null;
}

export interface NoticeDto {
  noticeId: string;
  kind: string;
  moduleId: string;
  severity: string;
  personId?: string | null;
  personName?: string | null;
  otherPersonId?: string | null;
  otherPersonName?: string | null;
  guildId?: string | null;
  conversationId?: string | null;
  occurredAtMs: number;
  data: unknown;
  state: string;
  delivery: string;
  createdAtMs: number;
}

export interface NoticePageDto {
  items: NoticeDto[];
  nextCursor?: string | null;
  unseen: number;
}

export interface NoticeStateRequest {
  state: AlertState;
  noticeIds?: string[] | null;
  all: boolean;
  kind?: string | null;
}

export interface NoticeStateResultDto {
  changed: number;
  unseen: number;
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

export interface PairDetailDto {
  summary: PairSummaryDto;
  weeks: PairWeekDto[];
  events: PairEventDto[];
  medianReplySecondsFromA?: number | null;
  medianReplySecondsFromB?: number | null;
  asOfMs: number;
}

export interface PairEventDto {
  eventId: string;
  a: string;
  b: string;
  kind: string;
  week: string;
  atMs: number;
  data: unknown;
}

export interface PairSummaryDto {
  a: string;
  aName?: string | null;
  b: string;
  bName?: string | null;
  interactions: number;
  recent: number;
  closeness: number;
  previousCloseness: number;
  reciprocity: number;
  tension: number;
  trend: string;
  firstWeek: string;
  lastWeek: string;
}

export interface PairWeekDto {
  week: string;
  fromA: number;
  fromB: number;
  conflict: number;
  negative: number;
  positive: number;
  answered: number;
  closeness: number;
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

export interface PeopleMapDto {
  points: MapPointDto[];
  asOfMs?: number | null;
  people: number;
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

export interface PersonBriefDto {
  personId: string;
  name?: string | null;
  state: string;
  asOfMs?: number | null;
  newestEvidenceMs?: number | null;
  stale: boolean;
  messages30d: number;
  messagesPrevious30d: number;
  activeDays30d: number;
  messagesRead: number;
  messagesInWindow: number;
  sections: BriefSectionDto[];
  mood?: BriefMoodDto | null;
  style?: BriefStyleDto | null;
  facts: BriefFactDto[];
  openThreads: BriefOpenThreadDto[];
  error?: string | null;
  llmCalls: number;
  requestedAtMs: number;
  finishedAtMs?: number | null;
}

export interface PersonBriefRequest {
  personId: string;
}

export interface PersonCandidateDto {
  personId: string;
  label: string;
  detail?: string | null;
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

export interface PersonExportRequest {
  personId: string;
  format: string;
}

export interface PersonLinksDto {
  personId: string;
  name?: string | null;
  days: number;
  shares: number;
  links: number;
  kinds: LinkCountDto[];
  providers: LinkCountDto[];
  sites: LinkCountDto[];
  topics: LinkCountDto[];
  recent: LinkShareDto[];
}

export interface PersonNamesDto {
  names: Record<string, string>;
}

export interface PersonRelationshipsDto {
  personId: string;
  name?: string | null;
  communityId?: string | null;
  bridgeTo?: string | null;
  outsideShare?: number | null;
  relationships: PairSummaryDto[];
  events: PairEventDto[];
  asOfMs: number;
}

export interface PersonRequest {
  personId: string;
}

export interface PersonTraitDto {
  trait: string;
  text: string;
  z: number;
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

export interface QualityLabDto {
  asOfMs: number;
  promptVersion: string;
  gold: QualityMetric[];
  planted: QualityMetric[];
  gate?: GateDecision | null;
}

export interface QualityMetric {
  scope: string;
  metric: string;
  value?: number | null;
  ciLow?: number | null;
  ciHigh?: number | null;
  n: number;
  promptVersion?: string | null;
  model?: string | null;
}

export interface QualityRunRequest {
  kind: string;
}

export interface RankedCountDto {
  id: string;
  name: string;
  count: number;
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

export interface RestoreDto {
  backupId: string;
  folder: string;
  files: string[];
  note: string;
}

export type RestrictedCapability = "FaceLabelling" | "Replica" | "PersonTargetedAlert" | "RoutineInference" | "IdentityFacts" | "TrackingSuggestion" | "ReplicaBasedTactic";

export interface RetentionDto {
  stream: string;
  keptDays: number;
  cutoffMs?: number | null;
  deletedTotal: number;
  lastDeleted: number;
  lastRunMs?: number | null;
}

export interface RetentionListDto {
  streams: RetentionDto[];
}

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

export interface SharedTraitDto {
  trait: string;
  text: string;
  mine: number;
  theirs: number;
}

export interface SimilarPeopleDto {
  personId: string;
  name?: string | null;
  messages: number;
  asOfMs?: number | null;
  traits: PersonTraitDto[];
  people: SimilarPersonDto[];
  note?: string | null;
}

export interface SimilarPersonDto {
  personId: string;
  name?: string | null;
  similarity: number;
  content: number;
  tone: number;
  style: number;
  rhythm: number;
  shared: SharedTraitDto[];
  sharedPlaces: string[];
  messages: number;
  communityId?: string | null;
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

export interface SpaceAboutKlivesDto {
  messages: number;
  previousMessages: number;
  kinds: Record<string, number>;
  sentiment: Record<string, number>;
  sentimentAnswered: number;
  people: SpaceTalkerDto[];
}

export interface SpaceCardDto {
  scopeKind: string;
  scopeId: string;
  title?: string | null;
  platform?: string | null;
  messages: number;
  previousMessages: number;
  people: number;
  aboutKlives: number;
  lastMessageMs?: number | null;
  computedAtMs: number;
}

export interface SpaceChannelDto {
  conversationId: string;
  title?: string | null;
  messages: number;
  people: number;
}

export interface SpaceCoverageDto {
  channelsKnown: number;
  channelsWithMessages: number;
  channelsActive: number;
  channelsFullyBackfilled: number;
  firstMessageMs?: number | null;
  lastMessageMs?: number | null;
  note: string;
}

export interface SpaceDayDto {
  day: string;
  messages: number;
  people: number;
}

export interface SpaceEventDto {
  day: string;
  kind: string;
  value: number;
  baseline: number;
  text: string;
}

export interface SpaceFactionDto {
  factionId: string;
  size: number;
  weight: number;
  members: SpaceFactionMemberDto[];
}

export interface SpaceFactionMemberDto {
  personId: string;
  name?: string | null;
  strength: number;
}

export interface SpaceListDto {
  spaces: SpaceCardDto[];
  windowDays: number;
}

export interface SpaceMemberDto {
  personId: string;
  name?: string | null;
  messages: number;
  share: number;
  activeDays: number;
  devotion?: number | null;
}

export interface SpacePageDto {
  scopeKind: string;
  scopeId: string;
  title?: string | null;
  platform?: string | null;
  windowDays: number;
  fromDay: string;
  toDay: string;
  asOfMs: number;
  messages: number;
  previousMessages: number;
  people: number;
  previousPeople: number;
  days: SpaceDayDto[];
  core: SpaceMemberDto[];
  rising: SpaceRisingDto[];
  tone: SpaceToneDto;
  aboutKlives: SpaceAboutKlivesDto;
  events: SpaceEventDto[];
  factions: SpaceFactionDto[];
  factionModularity?: number | null;
  channels: SpaceChannelDto[];
  coverage: SpaceCoverageDto;
  peopleLeftOut: number;
  latestDigestId?: string | null;
}

export interface SpaceRefreshRequest {
  scopeKind: string;
  scopeId: string;
}

export interface SpaceRisingDto {
  personId: string;
  name?: string | null;
  recent: number;
  earlier: number;
  isNew: boolean;
  everywhere: boolean;
  firstDay?: string | null;
}

export interface SpaceTalkerDto {
  personId: string;
  name?: string | null;
  messages: number;
}

export interface SpaceToneDto {
  recent: Record<string, number>;
  baseline: Record<string, number>;
  recentAnswered: number;
  baselineAnswered: number;
  conflictRecent: number;
  conflictBaseline: number;
}

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

export interface TopicStatusDto {
  watchId: string;
  name: string;
  last24h: number;
  usualDay: number;
  robustZ: number;
  surging: boolean;
  threshold: number;
  days: number[];
  recentMessageIds: string[];
}

export interface TranslateRequest {
  messageIds: string[];
}

export interface TranslateStatusDto {
  target: string;
  auto: boolean;
  queued: number;
  stored: number;
  usedThisHour: number;
  perHour: number;
}

export interface TranslationDto {
  messageId: string;
  version: number;
  original?: string | null;
  translation?: string | null;
  sourceLanguage?: string | null;
  target: string;
  state: string;
  lossy: boolean;
  note?: string | null;
}

export interface TranslationHitDto {
  messageId: string;
  sourceLanguage?: string | null;
  snippet: string;
}

export interface TranslationSearchDto {
  items: TranslationHitDto[];
  target: string;
}

export interface TranslationsDto {
  items: TranslationDto[];
  target: string;
  fromCache: number;
  translated: number;
}

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

export interface WatchDefinition {
  text: string;
  persons?: string[] | null;
  conversations?: string[] | null;
  guilds?: string[] | null;
}

export interface WatchDeleteRequest {
  watchId: string;
}

export interface WatchDeletedDto {
  watchId: string;
  deleted: boolean;
}

export interface WatchEnabledRequest {
  watchId: string;
  enabled: boolean;
}

export interface WatchListDto {
  rules: WatchRuleDto[];
}

export interface WatchRuleDto {
  watchId: string;
  name: string;
  definition: WatchDefinition;
  enabled: boolean;
  state: string;
  reason?: string | null;
  threshold?: number | null;
  checked: number;
  matched: number;
  lastMatchMs?: number | null;
  createdAtMs: number;
  updatedAtMs: number;
}

export interface WatchSaveRequest {
  name: string;
  text: string;
  watchId?: string | null;
  persons?: string[] | null;
  conversations?: string[] | null;
  guilds?: string[] | null;
  enabled: boolean;
}

export interface WellbeingListDto {
  suggestions: WellbeingSuggestionDto[];
  enabled: boolean;
  asOfMs: number;
}

export interface WellbeingStateDto {
  suggestionId: string;
  state: string;
}

export interface WellbeingStateRequest {
  suggestionId: string;
  state: string;
}

export interface WellbeingSuggestionDto {
  suggestionId: string;
  personId: string;
  personName?: string | null;
  kind: string;
  week: string;
  state: string;
  data: unknown;
  resourceText?: string | null;
  createdAtMs: number;
}

export interface ZoneReadingDto {
  zone?: string | null;
  offsetMinutes?: number | null;
  confidence: number;
  method: string;
  month: string;
  alternatives: string[];
}
