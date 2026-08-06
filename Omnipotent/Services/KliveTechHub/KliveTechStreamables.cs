using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Omnipotent.Profiles;
using System.Collections.Concurrent;
using System.Collections.Specialized;
using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;

namespace Omnipotent.Services.KliveTechHub
{
    public sealed partial class KliveTechHub
    {
        private const int MaximumSamplesPerStream = 512;
        private const int MaximumSamplesAcrossService = 4 * 1024;
        private const int MaximumRetainedSampleBytes = 16 * 1024 * 1024;
        private const int MaximumStreamablesAcrossService = 2 * 1024;
        private const int MaximumGadgetsWithStreamables = 256;
        private const int MaximumRetainedFramesPerStream = 2;
        private const int MaximumRetainedFrameBytes = 16 * 1024 * 1024;
        private const int MaximumIncompleteFramesPerStream = 2;
        private const int MaximumIncompleteFramesAcrossService = 64;
        private const int MaximumIncompleteFrameBytes = 16 * 1024 * 1024;
        private const int StreamEventQueueCapacity = 1024;
        private const int LiveSubscriberQueueCapacity = 8;
        private const int MaximumLiveSubscribers = 32;
        private static readonly TimeSpan IncompleteFrameLifetime = TimeSpan.FromSeconds(15);

        private readonly object streamableStateLock = new();
        private readonly Dictionary<string, StreamableState> streamableStates =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, GadgetManifestState> streamManifests =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, FrameAssembly> incompleteStreamFrames =
            new(StringComparer.Ordinal);
        private readonly LinkedList<StoredSample> sampleRetentionOrder = new();
        private readonly LinkedList<StoredBinaryFrame> frameRetentionOrder = new();
        private readonly ConcurrentDictionary<Guid, StreamLiveSubscriber> streamLiveSubscribers = new();
        private readonly ConcurrentDictionary<string, long> invalidStreamLogTicks =
            new(StringComparer.OrdinalIgnoreCase);
        private Channel<QueuedStreamEvent>? streamEventQueue;
        private Task? streamEventLoopTask;
        private int retainedSampleCount;
        private int retainedSampleBytes;
        private int retainedFrameBytes;
        private int incompleteFrameBytes;
        private long droppedStreamEvents;

        public sealed class StreamableCatalogEntry
        {
            public string gadgetID { get; init; } = string.Empty;
            public string gadgetName { get; init; } = string.Empty;
            public bool gadgetOnline { get; init; }
            public string streamID { get; init; } = string.Empty;
            public string valueType { get; init; } = string.Empty;
            public string mimeType { get; init; } = string.Empty;
            public string mode { get; init; } = string.Empty;
            public uint intervalMs { get; init; }
            public bool enabled { get; init; }
            public string sessionID { get; init; } = string.Empty;
            public ulong manifestRevision { get; init; }
            public ulong? latestSequence { get; init; }
            public ulong? latestDeviceTimestampMs { get; init; }
            public DateTime? latestReceivedUtc { get; init; }
            public JToken? latestValue { get; init; }
            public int? latestBinaryBytes { get; init; }
            public string latestBinarySha256 { get; init; } = string.Empty;
            public long droppedEvents { get; init; }
        }

        public sealed class StreamableSample
        {
            public string gadgetID { get; init; } = string.Empty;
            public string streamID { get; init; } = string.Empty;
            public string sessionID { get; init; } = string.Empty;
            public ulong sequence { get; init; }
            public ulong deviceTimestampMs { get; init; }
            public DateTime receivedUtc { get; init; }
            public JToken value { get; init; } = JValue.CreateNull();
        }

        public sealed class StreamableBinaryData
        {
            public required byte[] Data { get; init; }
            public required string MimeType { get; init; }
            public required string SessionID { get; init; }
            public required string Sha256 { get; init; }
            public required ulong Sequence { get; init; }
            public required ulong DeviceTimestampMs { get; init; }
            public required DateTime ReceivedUtc { get; init; }
        }

        private sealed class GadgetManifestState
        {
            internal required string SessionID { get; set; }
            internal ulong Revision { get; set; }
        }

        private sealed class StreamableState
        {
            internal required string GadgetID { get; init; }
            internal required string StreamID { get; set; }
            internal required KliveTechParsedStreamDefinition Definition { get; set; }
            internal required string SessionID { get; set; }
            internal ulong ManifestRevision { get; set; }
            internal ulong? LatestSequence { get; set; }
            internal ulong? LatestDeviceTimestampMs { get; set; }
            internal DateTime? LatestReceivedUtc { get; set; }
            internal JToken? LatestValue { get; set; }
            internal long DroppedEvents { get; set; }
            internal LinkedList<StoredSample> Samples { get; } = new();
            internal LinkedList<StoredBinaryFrame> Frames { get; } = new();
        }

        private sealed class StoredSample
        {
            internal required StreamableState Owner { get; init; }
            internal required StreamableSample Value { get; init; }
            internal required int SizeBytes { get; init; }
            internal LinkedListNode<StoredSample>? PerStreamNode { get; set; }
            internal LinkedListNode<StoredSample>? GlobalNode { get; set; }
        }

        private sealed class StoredBinaryFrame
        {
            internal required StreamableState Owner { get; init; }
            internal required StreamableBinaryData Value { get; init; }
            internal LinkedListNode<StoredBinaryFrame>? PerStreamNode { get; set; }
            internal LinkedListNode<StoredBinaryFrame>? GlobalNode { get; set; }
        }

        private sealed class FrameAssembly
        {
            internal required string Key { get; init; }
            internal required StreamableState Owner { get; init; }
            internal required string SessionID { get; init; }
            internal required ulong Sequence { get; init; }
            internal required ulong TimestampMs { get; init; }
            internal required string MimeType { get; init; }
            internal required int FrameSize { get; init; }
            internal required string FrameSha256 { get; init; }
            internal required byte[]?[] Chunks { get; init; }
            internal required DateTime LastUpdatedUtc { get; set; }
            internal int ReceivedChunks { get; set; }
            internal int ReceivedBytes { get; set; }
        }

        private sealed class QueuedStreamEvent
        {
            internal required KliveTechGadget Gadget { get; init; }
            internal required KliveTechParsedStreamEvent Event { get; init; }
        }

        private sealed class StreamLiveSubscriber
        {
            internal required Guid ID { get; init; }
            internal required string GadgetID { get; init; }
            internal required string StreamID { get; init; }
            internal required WebSocket Socket { get; init; }
            internal required CancellationTokenSource Cancellation { get; init; }
            internal Channel<string> Messages { get; } = Channel.CreateBounded<string>(
                new BoundedChannelOptions(LiveSubscriberQueueCapacity)
                {
                    SingleReader = true,
                    SingleWriter = false,
                    FullMode = BoundedChannelFullMode.DropOldest,
                    AllowSynchronousContinuations = false
                });
        }

        private static string MakeStreamKey(string gadgetId, string streamId)
        {
            return gadgetId + "\u001f" + streamId;
        }

        private void ResetStreamableRuntime()
        {
            lock (streamableStateLock)
            {
                streamableStates.Clear();
                streamManifests.Clear();
                incompleteStreamFrames.Clear();
                sampleRetentionOrder.Clear();
                frameRetentionOrder.Clear();
                retainedSampleCount = 0;
                retainedSampleBytes = 0;
                retainedFrameBytes = 0;
                incompleteFrameBytes = 0;
                Interlocked.Exchange(ref droppedStreamEvents, 0);
            }
            invalidStreamLogTicks.Clear();
            streamEventQueue = Channel.CreateBounded<QueuedStreamEvent>(
                new BoundedChannelOptions(StreamEventQueueCapacity)
                {
                    SingleReader = true,
                    SingleWriter = false,
                    FullMode = BoundedChannelFullMode.Wait,
                    AllowSynchronousContinuations = false
                });
            streamEventLoopTask = null;
        }

        private void StartStreamableRuntime(CancellationToken serviceToken)
        {
            Channel<QueuedStreamEvent> queue = streamEventQueue
                ?? throw new InvalidOperationException("The Streamables queue has not been initialized.");
            streamEventLoopTask = Task.Run(
                () => ProcessStreamEventsAsync(queue, serviceToken),
                serviceToken);
        }

        private void StopStreamableRuntime()
        {
            streamEventQueue?.Writer.TryComplete();
            foreach (StreamLiveSubscriber subscriber in streamLiveSubscribers.Values)
            {
                subscriber.Messages.Writer.TryComplete();
                try { subscriber.Cancellation.Cancel(); } catch { }
            }
            streamLiveSubscribers.Clear();
            lock (streamableStateLock)
            {
                streamableStates.Clear();
                streamManifests.Clear();
                incompleteStreamFrames.Clear();
                sampleRetentionOrder.Clear();
                frameRetentionOrder.Clear();
                retainedSampleCount = 0;
                retainedSampleBytes = 0;
                retainedFrameBytes = 0;
                incompleteFrameBytes = 0;
            }
        }

        private async Task ProcessStreamEventsAsync(
            Channel<QueuedStreamEvent> queue,
            CancellationToken serviceToken)
        {
            try
            {
                await foreach (QueuedStreamEvent queued in queue.Reader.ReadAllAsync(serviceToken))
                {
                    string key = NormalizeAddress(queued.Gadget.IPAddress);
                    if (!gadgetsByAddress.TryGetValue(key, out KliveTechGadget? active) ||
                        !ReferenceEquals(active, queued.Gadget))
                    {
                        continue;
                    }
                    ApplyStreamEvent(queued.Gadget, queued.Event);
                }
            }
            catch (OperationCanceledException) when (serviceToken.IsCancellationRequested)
            {
                // Normal service shutdown.
            }
            catch (ChannelClosedException) when (serviceToken.IsCancellationRequested)
            {
                // Normal service shutdown.
            }
            catch (Exception ex)
            {
                await ServiceLogError(ex, "The KliveTech Streamables worker stopped unexpectedly.");
                _ = TerminateService();
            }
        }

        private bool TryAcceptStreamEvent(
            KliveTechGadget gadget,
            JObject value,
            out string error)
        {
            if (!KliveTechStreamProtocol.TryParseEvent(value, out KliveTechParsedStreamEvent? parsed, out error) ||
                parsed == null)
            {
                return false;
            }

            if (parsed.Kind == KliveTechStreamEventKind.Manifest)
            {
                ApplyStreamEvent(gadget, parsed);
                return true;
            }

            Channel<QueuedStreamEvent>? queue = streamEventQueue;
            if (queue == null || !queue.Writer.TryWrite(new QueuedStreamEvent
                {
                    Gadget = gadget,
                    Event = parsed
                }))
            {
                Interlocked.Increment(ref droppedStreamEvents);
                lock (streamableStateLock)
                {
                    if (streamableStates.TryGetValue(
                            MakeStreamKey(gadget.gadgetID, parsed.StreamID),
                            out StreamableState? state))
                    {
                        state.DroppedEvents++;
                    }
                }
            }
            return true;
        }

        private void ApplyStreamEvent(KliveTechGadget gadget, KliveTechParsedStreamEvent parsed)
        {
            switch (parsed.Kind)
            {
                case KliveTechStreamEventKind.Manifest:
                    ApplyStreamManifest(gadget, parsed);
                    break;
                case KliveTechStreamEventKind.Sample:
                    ApplyStreamSample(gadget, parsed);
                    break;
                case KliveTechStreamEventKind.FrameChunk:
                    ApplyStreamFrameChunk(gadget, parsed);
                    break;
            }
        }

        private void ApplyStreamManifest(KliveTechGadget gadget, KliveTechParsedStreamEvent manifest)
        {
            string liveMessage;
            lock (streamableStateLock)
            {
                bool newSession = !streamManifests.TryGetValue(
                    gadget.gadgetID,
                    out GadgetManifestState? previous) ||
                    !string.Equals(previous.SessionID, manifest.SessionID, StringComparison.Ordinal);
                if (!newSession && previous != null && manifest.Revision < previous.Revision)
                {
                    return;
                }

                if (previous == null && streamManifests.Count >= MaximumGadgetsWithStreamables)
                {
                    Interlocked.Increment(ref droppedStreamEvents);
                    return;
                }

                int existingCount = streamableStates.Values.Count(state =>
                    string.Equals(state.GadgetID, gadget.gadgetID, StringComparison.OrdinalIgnoreCase));
                if (streamableStates.Count - existingCount + manifest.Definitions.Count >
                    MaximumStreamablesAcrossService)
                {
                    Interlocked.Increment(ref droppedStreamEvents);
                    return;
                }
                streamManifests[gadget.gadgetID] = new GadgetManifestState
                {
                    SessionID = manifest.SessionID,
                    Revision = manifest.Revision
                };
                HashSet<string> reportedKeys = new(StringComparer.OrdinalIgnoreCase);
                foreach (KliveTechParsedStreamDefinition definition in manifest.Definitions)
                {
                    string key = MakeStreamKey(gadget.gadgetID, definition.StreamID);
                    reportedKeys.Add(key);
                    if (!streamableStates.TryGetValue(key, out StreamableState? state))
                    {
                        state = new StreamableState
                        {
                            GadgetID = gadget.gadgetID,
                            StreamID = definition.StreamID,
                            Definition = definition,
                            SessionID = manifest.SessionID,
                            ManifestRevision = manifest.Revision
                        };
                        streamableStates.Add(key, state);
                    }
                    else
                    {
                        if (newSession || !string.Equals(state.SessionID, manifest.SessionID, StringComparison.Ordinal))
                        {
                            ClearStreamDataLocked(state);
                        }
                        state.StreamID = definition.StreamID;
                        state.Definition = definition;
                        state.SessionID = manifest.SessionID;
                        state.ManifestRevision = manifest.Revision;
                    }
                }

                StreamableState[] stale = streamableStates.Values
                    .Where(state => string.Equals(state.GadgetID, gadget.gadgetID, StringComparison.OrdinalIgnoreCase) &&
                        !reportedKeys.Contains(MakeStreamKey(state.GadgetID, state.StreamID)))
                    .ToArray();
                foreach (StreamableState state in stale)
                {
                    RemoveStreamStateLocked(state);
                }
                gadget.streamableCount = manifest.Definitions.Count;
                liveMessage = JsonConvert.SerializeObject(new
                {
                    type = "manifest",
                    gadgetID = gadget.gadgetID,
                    sessionID = manifest.SessionID,
                    revision = manifest.Revision,
                    streamables = manifest.Definitions.Select(ToPublicDefinition).ToArray()
                });
            }
            PublishStreamLiveMessage(gadget.gadgetID, string.Empty, liveMessage);
        }

        private void ApplyStreamSample(KliveTechGadget gadget, KliveTechParsedStreamEvent sample)
        {
            string? liveMessage = null;
            lock (streamableStateLock)
            {
                if (!TryGetCurrentStreamStateLocked(gadget, sample, out StreamableState? state) ||
                    state == null || state.Definition.ValueType == "binary" || sample.Value == null ||
                    !ValueMatchesDefinition(sample.Value, state.Definition.ValueType))
                {
                    CountDroppedEventLocked(state);
                    return;
                }
                if (state.LatestSequence.HasValue && sample.Sequence <= state.LatestSequence.Value)
                {
                    CountDroppedEventLocked(state);
                    return;
                }

                DateTime receivedUtc = DateTime.UtcNow;
                JToken retainedValue = sample.Value.DeepClone();
                state.LatestSequence = sample.Sequence;
                state.LatestDeviceTimestampMs = sample.TimestampMs;
                state.LatestReceivedUtc = receivedUtc;
                state.LatestValue = retainedValue;
                StreamableSample publicSample = new()
                {
                    gadgetID = gadget.gadgetID,
                    streamID = sample.StreamID,
                    sessionID = sample.SessionID,
                    sequence = sample.Sequence,
                    deviceTimestampMs = sample.TimestampMs,
                    receivedUtc = receivedUtc,
                    value = retainedValue.DeepClone()
                };
                AddSampleLocked(state, publicSample);
                liveMessage = JsonConvert.SerializeObject(new
                {
                    type = "sample",
                    gadgetID = gadget.gadgetID,
                    gadgetName = gadget.name,
                    streamID = sample.StreamID,
                    sessionID = sample.SessionID,
                    sequence = sample.Sequence,
                    deviceTimestampMs = sample.TimestampMs,
                    receivedUtc,
                    value = retainedValue
                });
            }
            if (liveMessage != null)
            {
                PublishStreamLiveMessage(gadget.gadgetID, sample.StreamID, liveMessage);
            }
        }

        private void ApplyStreamFrameChunk(KliveTechGadget gadget, KliveTechParsedStreamEvent chunk)
        {
            string? liveMessage = null;
            lock (streamableStateLock)
            {
                RemoveExpiredAssembliesLocked(DateTime.UtcNow);
                if (!TryGetCurrentStreamStateLocked(gadget, chunk, out StreamableState? state) ||
                    state == null || state.Definition.ValueType != "binary" ||
                    !string.Equals(state.Definition.MimeType, chunk.MimeType, StringComparison.Ordinal))
                {
                    CountDroppedEventLocked(state);
                    return;
                }
                if (state.LatestSequence.HasValue && chunk.Sequence <= state.LatestSequence.Value)
                {
                    CountDroppedEventLocked(state);
                    return;
                }

                string assemblyKey = MakeAssemblyKey(state, chunk.SessionID, chunk.Sequence);
                if (!incompleteStreamFrames.TryGetValue(assemblyKey, out FrameAssembly? assembly))
                {
                    EnsureIncompleteCapacityLocked(state, chunk.ChunkData.Length);
                    assembly = new FrameAssembly
                    {
                        Key = assemblyKey,
                        Owner = state,
                        SessionID = chunk.SessionID,
                        Sequence = chunk.Sequence,
                        TimestampMs = chunk.TimestampMs,
                        MimeType = chunk.MimeType,
                        FrameSize = chunk.FrameSize,
                        FrameSha256 = chunk.FrameSha256,
                        Chunks = new byte[chunk.ChunkCount][],
                        LastUpdatedUtc = DateTime.UtcNow
                    };
                    incompleteStreamFrames.Add(assemblyKey, assembly);
                }
                else if (assembly.TimestampMs != chunk.TimestampMs ||
                    assembly.FrameSize != chunk.FrameSize || assembly.Chunks.Length != chunk.ChunkCount ||
                    !string.Equals(assembly.MimeType, chunk.MimeType, StringComparison.Ordinal) ||
                    !string.Equals(assembly.FrameSha256, chunk.FrameSha256, StringComparison.OrdinalIgnoreCase))
                {
                    RemoveAssemblyLocked(assembly);
                    CountDroppedEventLocked(state);
                    return;
                }

                byte[]? existing = assembly.Chunks[chunk.ChunkIndex];
                if (existing != null)
                {
                    if (!existing.AsSpan().SequenceEqual(chunk.ChunkData))
                    {
                        RemoveAssemblyLocked(assembly);
                        CountDroppedEventLocked(state);
                    }
                    else
                    {
                        assembly.LastUpdatedUtc = DateTime.UtcNow;
                    }
                    return;
                }
                EnsurePendingBytesLocked(chunk.ChunkData.Length, assembly);
                assembly.Chunks[chunk.ChunkIndex] = chunk.ChunkData;
                assembly.ReceivedChunks++;
                assembly.ReceivedBytes += chunk.ChunkData.Length;
                assembly.LastUpdatedUtc = DateTime.UtcNow;
                incompleteFrameBytes += chunk.ChunkData.Length;
                if (assembly.ReceivedChunks != assembly.Chunks.Length ||
                    assembly.ReceivedBytes != assembly.FrameSize)
                {
                    return;
                }

                byte[] frameData = GC.AllocateUninitializedArray<byte>(assembly.FrameSize);
                int offset = 0;
                foreach (byte[]? part in assembly.Chunks)
                {
                    if (part == null)
                    {
                        return;
                    }
                    part.CopyTo(frameData, offset);
                    offset += part.Length;
                }
                string actualHash = Convert.ToHexString(SHA256.HashData(frameData));
                RemoveAssemblyLocked(assembly);
                if (!string.Equals(actualHash, assembly.FrameSha256, StringComparison.OrdinalIgnoreCase))
                {
                    CountDroppedEventLocked(state);
                    return;
                }

                DateTime receivedUtc = DateTime.UtcNow;
                StreamableBinaryData binary = new()
                {
                    Data = frameData,
                    MimeType = assembly.MimeType,
                    SessionID = assembly.SessionID,
                    Sha256 = actualHash,
                    Sequence = assembly.Sequence,
                    DeviceTimestampMs = assembly.TimestampMs,
                    ReceivedUtc = receivedUtc
                };
                state.LatestSequence = assembly.Sequence;
                state.LatestDeviceTimestampMs = assembly.TimestampMs;
                state.LatestReceivedUtc = receivedUtc;
                state.LatestValue = null;
                AddFrameLocked(state, binary);
                foreach (FrameAssembly older in incompleteStreamFrames.Values
                    .Where(candidate => ReferenceEquals(candidate.Owner, state) &&
                        candidate.Sequence <= binary.Sequence)
                    .ToArray())
                {
                    RemoveAssemblyLocked(older);
                }
                liveMessage = JsonConvert.SerializeObject(new
                {
                    type = "frame",
                    gadgetID = gadget.gadgetID,
                    gadgetName = gadget.name,
                    streamID = state.StreamID,
                    sessionID = binary.SessionID,
                    sequence = binary.Sequence,
                    deviceTimestampMs = binary.DeviceTimestampMs,
                    receivedUtc = binary.ReceivedUtc,
                    mimeType = binary.MimeType,
                    bytes = binary.Data.Length,
                    sha256 = binary.Sha256
                });
            }
            if (liveMessage != null)
            {
                PublishStreamLiveMessage(gadget.gadgetID, chunk.StreamID, liveMessage);
            }
        }

        private bool TryGetCurrentStreamStateLocked(
            KliveTechGadget gadget,
            KliveTechParsedStreamEvent streamEvent,
            out StreamableState? state)
        {
            state = null;
            return streamManifests.TryGetValue(gadget.gadgetID, out GadgetManifestState? manifest) &&
                string.Equals(manifest.SessionID, streamEvent.SessionID, StringComparison.Ordinal) &&
                streamableStates.TryGetValue(
                    MakeStreamKey(gadget.gadgetID, streamEvent.StreamID),
                    out state) &&
                string.Equals(state.SessionID, streamEvent.SessionID, StringComparison.Ordinal);
        }

        private static bool ValueMatchesDefinition(JToken value, string valueType)
        {
            return valueType switch
            {
                "integer" => value.Type == JTokenType.Integer,
                "number" => value.Type is JTokenType.Integer or JTokenType.Float,
                "boolean" => value.Type == JTokenType.Boolean,
                "string" => value.Type == JTokenType.String,
                "json" => value.Type is not (JTokenType.Undefined or JTokenType.Raw or JTokenType.Bytes),
                _ => false
            };
        }

        private void AddSampleLocked(StreamableState state, StreamableSample sample)
        {
            int sizeBytes = Encoding.UTF8.GetByteCount(sample.value.ToString(Formatting.None));
            StoredSample stored = new() { Owner = state, Value = sample, SizeBytes = sizeBytes };
            stored.PerStreamNode = state.Samples.AddLast(stored);
            stored.GlobalNode = sampleRetentionOrder.AddLast(stored);
            retainedSampleCount++;
            retainedSampleBytes += sizeBytes;
            while (state.Samples.Count > MaximumSamplesPerStream)
            {
                RemoveSampleLocked(state.Samples.First!.Value);
            }
            while ((retainedSampleCount > MaximumSamplesAcrossService ||
                    retainedSampleBytes > MaximumRetainedSampleBytes) &&
                   sampleRetentionOrder.First != null)
            {
                RemoveSampleLocked(sampleRetentionOrder.First!.Value);
            }
        }

        private void RemoveSampleLocked(StoredSample sample)
        {
            if (sample.PerStreamNode?.List != null)
            {
                sample.Owner.Samples.Remove(sample.PerStreamNode);
            }
            if (sample.GlobalNode?.List != null)
            {
                sampleRetentionOrder.Remove(sample.GlobalNode);
                retainedSampleCount--;
                retainedSampleBytes -= sample.SizeBytes;
            }
            sample.PerStreamNode = null;
            sample.GlobalNode = null;
        }

        private void AddFrameLocked(StreamableState state, StreamableBinaryData frame)
        {
            StoredBinaryFrame stored = new() { Owner = state, Value = frame };
            stored.PerStreamNode = state.Frames.AddLast(stored);
            stored.GlobalNode = frameRetentionOrder.AddLast(stored);
            retainedFrameBytes += frame.Data.Length;
            while (state.Frames.Count > MaximumRetainedFramesPerStream)
            {
                RemoveFrameLocked(state.Frames.First!.Value);
            }
            while (retainedFrameBytes > MaximumRetainedFrameBytes && frameRetentionOrder.First != null)
            {
                RemoveFrameLocked(frameRetentionOrder.First.Value);
            }
        }

        private void RemoveFrameLocked(StoredBinaryFrame frame)
        {
            if (frame.PerStreamNode?.List != null)
            {
                frame.Owner.Frames.Remove(frame.PerStreamNode);
            }
            if (frame.GlobalNode?.List != null)
            {
                frameRetentionOrder.Remove(frame.GlobalNode);
                retainedFrameBytes -= frame.Value.Data.Length;
            }
            frame.PerStreamNode = null;
            frame.GlobalNode = null;
        }

        private void ClearStreamDataLocked(StreamableState state)
        {
            foreach (StoredSample sample in state.Samples.ToArray())
            {
                RemoveSampleLocked(sample);
            }
            foreach (StoredBinaryFrame frame in state.Frames.ToArray())
            {
                RemoveFrameLocked(frame);
            }
            foreach (FrameAssembly assembly in incompleteStreamFrames.Values
                .Where(candidate => ReferenceEquals(candidate.Owner, state)).ToArray())
            {
                RemoveAssemblyLocked(assembly);
            }
            state.LatestSequence = null;
            state.LatestDeviceTimestampMs = null;
            state.LatestReceivedUtc = null;
            state.LatestValue = null;
        }

        private void RemoveStreamStateLocked(StreamableState state)
        {
            ClearStreamDataLocked(state);
            streamableStates.Remove(MakeStreamKey(state.GadgetID, state.StreamID));
        }

        private static string MakeAssemblyKey(
            StreamableState state,
            string sessionId,
            ulong sequence)
        {
            return MakeStreamKey(state.GadgetID, state.StreamID) + "\u001f" +
                sessionId + "\u001f" + sequence.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        private void EnsureIncompleteCapacityLocked(StreamableState owner, int incomingBytes)
        {
            FrameAssembly[] forStream = incompleteStreamFrames.Values
                .Where(candidate => ReferenceEquals(candidate.Owner, owner))
                .OrderBy(candidate => candidate.LastUpdatedUtc)
                .ToArray();
            while (forStream.Length >= MaximumIncompleteFramesPerStream)
            {
                RemoveAssemblyLocked(forStream[0]);
                forStream = forStream[1..];
            }
            while (incompleteStreamFrames.Count >= MaximumIncompleteFramesAcrossService)
            {
                FrameAssembly oldest = incompleteStreamFrames.Values.MinBy(candidate => candidate.LastUpdatedUtc)!;
                RemoveAssemblyLocked(oldest);
            }
            EnsurePendingBytesLocked(incomingBytes, preserved: null);
        }

        private void EnsurePendingBytesLocked(int incomingBytes, FrameAssembly? preserved)
        {
            while (incompleteFrameBytes + incomingBytes > MaximumIncompleteFrameBytes)
            {
                FrameAssembly? oldest = incompleteStreamFrames.Values
                    .Where(candidate => !ReferenceEquals(candidate, preserved))
                    .MinBy(candidate => candidate.LastUpdatedUtc);
                if (oldest == null)
                {
                    throw new InvalidDataException("Stream frame assembly memory limit was reached.");
                }
                RemoveAssemblyLocked(oldest);
            }
        }

        private void RemoveExpiredAssembliesLocked(DateTime utcNow)
        {
            foreach (FrameAssembly assembly in incompleteStreamFrames.Values
                .Where(candidate => utcNow - candidate.LastUpdatedUtc > IncompleteFrameLifetime)
                .ToArray())
            {
                RemoveAssemblyLocked(assembly);
                assembly.Owner.DroppedEvents++;
                Interlocked.Increment(ref droppedStreamEvents);
            }
        }

        private void RemoveAssemblyLocked(FrameAssembly assembly)
        {
            if (incompleteStreamFrames.Remove(assembly.Key))
            {
                incompleteFrameBytes -= assembly.ReceivedBytes;
            }
        }

        private void CountDroppedEventLocked(StreamableState? state)
        {
            if (state != null)
            {
                state.DroppedEvents++;
            }
            Interlocked.Increment(ref droppedStreamEvents);
        }

        public IReadOnlyList<StreamableCatalogEntry> GetStreamableCatalog(string? gadgetId = null)
        {
            string filter = gadgetId?.Trim() ?? string.Empty;
            lock (streamableStateLock)
            {
                return streamableStates.Values
                    .Where(state => string.IsNullOrEmpty(filter) ||
                        string.Equals(state.GadgetID, filter, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(state => state.GadgetID, StringComparer.Ordinal)
                    .ThenBy(state => state.StreamID, StringComparer.Ordinal)
                    .Select(ToCatalogEntryLocked)
                    .ToArray();
            }
        }

        public IReadOnlyList<StreamableSample> GetStreamableHistory(
            string gadgetId,
            string streamId,
            int limit,
            ulong? afterSequence = null)
        {
            if (string.IsNullOrWhiteSpace(gadgetId) || !KliveTechStreamProtocol.IsValidStreamId(streamId))
            {
                throw new InvalidDataException("gadgetID and a valid streamID are required.");
            }
            int boundedLimit = Math.Clamp(limit, 1, 512);
            lock (streamableStateLock)
            {
                if (!streamableStates.TryGetValue(
                        MakeStreamKey(gadgetId.Trim(), streamId),
                        out StreamableState? state))
                {
                    throw new KeyNotFoundException("Streamable was not found.");
                }
                return state.Samples
                    .Select(stored => stored.Value)
                    .Where(sample => !afterSequence.HasValue || sample.sequence > afterSequence.Value)
                    .TakeLast(boundedLimit)
                    .Select(CloneSample)
                    .ToArray();
            }
        }

        public StreamableBinaryData? GetLatestStreamableBinary(string gadgetId, string streamId)
        {
            if (string.IsNullOrWhiteSpace(gadgetId) || !KliveTechStreamProtocol.IsValidStreamId(streamId))
            {
                throw new InvalidDataException("gadgetID and a valid streamID are required.");
            }
            lock (streamableStateLock)
            {
                if (!streamableStates.TryGetValue(
                        MakeStreamKey(gadgetId.Trim(), streamId),
                        out StreamableState? state))
                {
                    throw new KeyNotFoundException("Streamable was not found.");
                }
                StoredBinaryFrame? latest = state.Frames.Last?.Value;
                if (latest == null)
                {
                    return null;
                }
                return new StreamableBinaryData
                {
                    Data = (byte[])latest.Value.Data.Clone(),
                    MimeType = latest.Value.MimeType,
                    SessionID = latest.Value.SessionID,
                    Sha256 = latest.Value.Sha256,
                    Sequence = latest.Value.Sequence,
                    DeviceTimestampMs = latest.Value.DeviceTimestampMs,
                    ReceivedUtc = latest.Value.ReceivedUtc
                };
            }
        }

        public async Task<StreamableCatalogEntry> ConfigureStreamableAsync(
            string gadgetId,
            string streamId,
            bool enabled,
            uint? intervalMs,
            CancellationToken callerToken)
        {
            if (!KliveTechStreamProtocol.IsValidStreamId(streamId))
            {
                throw new InvalidDataException("streamID is invalid.");
            }
            if (intervalMs.HasValue && intervalMs.Value < KliveTechStreamProtocol.MinimumStreamIntervalMs)
            {
                throw new InvalidDataException(
                    $"intervalMs must be at least {KliveTechStreamProtocol.MinimumStreamIntervalMs}.");
            }
            KliveTechGadget gadget = GetKliveTechGadgetByID(gadgetId)
                ?? throw new KeyNotFoundException("Gadget was not found.");
            if (!gadget.isOnline)
            {
                throw new IOException("Gadget is offline.");
            }
            lock (streamableStateLock)
            {
                if (!streamableStates.ContainsKey(MakeStreamKey(gadget.gadgetID, streamId)))
                {
                    throw new KeyNotFoundException("Streamable was not found.");
                }
            }

            JObject request = new()
            {
                ["StreamID"] = streamId,
                ["Enabled"] = enabled
            };
            if (intervalMs.HasValue)
            {
                request["IntervalMs"] = intervalMs.Value;
            }
            KliveTechGadgetResponse response = await SendData(
                gadget,
                KliveTechActions.OperationNumber.ConfigureStreamable,
                request.ToString(Formatting.None),
                expectResponse: true,
                ResponseTimeout,
                callerToken);
            string parseError = string.Empty;
            JObject? responseData = response.DATA as JObject;
            JObject? responseDefinition = responseData?["Streamable"] as JObject;
            KliveTechParsedStreamDefinition? parsedDefinition = null;
            if (response.STATUS != (int)HttpStatusCode.OK || responseDefinition == null ||
                !KliveTechStreamProtocol.TryParseDefinition(
                    responseDefinition,
                    out parsedDefinition,
                    out parseError) || parsedDefinition == null ||
                !string.Equals(parsedDefinition.StreamID, streamId, StringComparison.OrdinalIgnoreCase))
            {
                string gadgetError = response.DATA?["Error"]?.ToString();
                throw new InvalidDataException(
                    !string.IsNullOrWhiteSpace(gadgetError) ? gadgetError :
                    string.IsNullOrWhiteSpace(parseError)
                        ? "Gadget returned an invalid Streamables definition."
                        : parseError);
            }

            lock (streamableStateLock)
            {
                StreamableState state = streamableStates[MakeStreamKey(gadget.gadgetID, streamId)];
                state.Definition = parsedDefinition;
                ulong? revision = responseData?["Revision"]?.Value<ulong?>();
                if (revision.HasValue)
                {
                    state.ManifestRevision = revision.Value;
                    if (streamManifests.TryGetValue(gadget.gadgetID, out GadgetManifestState? manifest))
                    {
                        manifest.Revision = Math.Max(manifest.Revision, revision.Value);
                    }
                }
                return ToCatalogEntryLocked(state);
            }
        }

        private async Task RefreshStreamablesAsync(
            KliveTechGadget gadget,
            CancellationToken connectionToken)
        {
            try
            {
                KliveTechGadgetResponse response = await SendData(
                    gadget,
                    KliveTechActions.OperationNumber.GetStreamables,
                    "{}",
                    expectResponse: true,
                    ResponseTimeout,
                    connectionToken);
                if (response.STATUS != (int)HttpStatusCode.OK)
                {
                    // Older gadgets report an unknown operation. Streamables are optional.
                    return;
                }
                string parseError = string.Empty;
                KliveTechParsedStreamEvent? parsed = null;
                if (response.DATA is not JObject manifest ||
                    !KliveTechStreamProtocol.TryParseEvent(
                        manifest,
                        out parsed,
                        out parseError) || parsed?.Kind != KliveTechStreamEventKind.Manifest)
                {
                    await ServiceLogError(
                        $"Ignored invalid Streamables manifest from {gadget.name}: {parseError}");
                    return;
                }
                ApplyStreamManifest(gadget, parsed);
            }
            catch (OperationCanceledException) when (connectionToken.IsCancellationRequested)
            {
                // Normal disconnect.
            }
            catch (TimeoutException)
            {
                // A legacy or busy gadget may not support discovery; normal monitoring continues.
            }
            catch (Exception ex)
            {
                await ServiceLogError(ex, $"Could not discover Streamables for {gadget.name}.");
            }
        }

        private StreamableCatalogEntry ToCatalogEntryLocked(StreamableState state)
        {
            KliveTechGadget? gadget = GetKliveTechGadgetByID(state.GadgetID);
            StoredBinaryFrame? latestFrame = state.Frames.Last?.Value;
            return new StreamableCatalogEntry
            {
                gadgetID = state.GadgetID,
                gadgetName = gadget?.name ?? string.Empty,
                gadgetOnline = gadget?.isOnline ?? false,
                streamID = state.StreamID,
                valueType = state.Definition.ValueType,
                mimeType = state.Definition.MimeType,
                mode = state.Definition.Mode,
                intervalMs = state.Definition.IntervalMs,
                enabled = state.Definition.Enabled,
                sessionID = state.SessionID,
                manifestRevision = state.ManifestRevision,
                latestSequence = state.LatestSequence,
                latestDeviceTimestampMs = state.LatestDeviceTimestampMs,
                latestReceivedUtc = state.LatestReceivedUtc,
                latestValue = state.LatestValue?.DeepClone(),
                latestBinaryBytes = latestFrame?.Value.Data.Length,
                latestBinarySha256 = latestFrame?.Value.Sha256 ?? string.Empty,
                droppedEvents = state.DroppedEvents
            };
        }

        private static object ToPublicDefinition(KliveTechParsedStreamDefinition definition)
        {
            return new
            {
                streamID = definition.StreamID,
                valueType = definition.ValueType,
                mimeType = definition.MimeType,
                mode = definition.Mode,
                intervalMs = definition.IntervalMs,
                enabled = definition.Enabled
            };
        }

        private static StreamableSample CloneSample(StreamableSample sample)
        {
            return new StreamableSample
            {
                gadgetID = sample.gadgetID,
                streamID = sample.streamID,
                sessionID = sample.sessionID,
                sequence = sample.sequence,
                deviceTimestampMs = sample.deviceTimestampMs,
                receivedUtc = sample.receivedUtc,
                value = sample.value.DeepClone()
            };
        }

        internal async Task RegisterStreamableLiveRouteAsync()
        {
            await ExecuteServiceMethod<Omnipotent.Services.KliveAPI.KliveAPI>(
                "CreateWebSocketRoute",
                "/klivetech/streamables/live",
                (Func<HttpListenerContext, WebSocket, NameValueCollection, KMProfileManager.KMProfile?, Task>)
                    (async (_, socket, query, _) => await HandleStreamLiveConnectionAsync(socket, query)),
                KMProfileManager.KMPermissions.Klives);
        }

        private async Task HandleStreamLiveConnectionAsync(WebSocket socket, NameValueCollection query)
        {
            string gadgetId = query.Get("gadgetID")?.Trim() ?? string.Empty;
            string streamId = query.Get("streamID")?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(gadgetId) ||
                (!string.IsNullOrEmpty(streamId) && !KliveTechStreamProtocol.IsValidStreamId(streamId)))
            {
                await socket.CloseAsync(
                    WebSocketCloseStatus.PolicyViolation,
                    "gadgetID and a valid optional streamID are required",
                    CancellationToken.None);
                return;
            }
            if (streamLiveSubscribers.Count >= MaximumLiveSubscribers)
            {
                await socket.CloseAsync(
                    WebSocketCloseStatus.PolicyViolation,
                    "Too many Streamables viewers",
                    CancellationToken.None);
                return;
            }

            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken.Token);
            StreamLiveSubscriber subscriber = new()
            {
                ID = Guid.NewGuid(),
                GadgetID = gadgetId,
                StreamID = streamId,
                Socket = socket,
                Cancellation = linked
            };
            if (!streamLiveSubscribers.TryAdd(subscriber.ID, subscriber))
            {
                await socket.CloseAsync(
                    WebSocketCloseStatus.InternalServerError,
                    "Could not subscribe",
                    CancellationToken.None);
                return;
            }

            try
            {
                string snapshot = JsonConvert.SerializeObject(new
                {
                    type = "snapshot",
                    streamables = GetStreamableCatalog(gadgetId)
                    .Where(entry => string.IsNullOrEmpty(streamId) ||
                            string.Equals(entry.streamID, streamId, StringComparison.OrdinalIgnoreCase))
                        .ToArray()
                });
                subscriber.Messages.Writer.TryWrite(snapshot);
                Task sender = SendStreamLiveMessagesAsync(subscriber, linked.Token);
                Task receiver = ReceiveStreamLiveCloseAsync(socket, linked.Token);
                await Task.WhenAny(sender, receiver);
                linked.Cancel();
                try { await Task.WhenAll(sender, receiver); } catch (OperationCanceledException) { }
            }
            catch (WebSocketException)
            {
                // Viewer disconnected without a close frame.
            }
            finally
            {
                streamLiveSubscribers.TryRemove(subscriber.ID, out _);
                subscriber.Messages.Writer.TryComplete();
                try
                {
                    if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                    {
                        await socket.CloseAsync(
                            WebSocketCloseStatus.NormalClosure,
                            "Stream ended",
                            CancellationToken.None);
                    }
                }
                catch { }
            }
        }

        private static async Task SendStreamLiveMessagesAsync(
            StreamLiveSubscriber subscriber,
            CancellationToken token)
        {
            await foreach (string message in subscriber.Messages.Reader.ReadAllAsync(token))
            {
                byte[] bytes = Encoding.UTF8.GetBytes(message);
                await subscriber.Socket.SendAsync(
                    bytes.AsMemory(),
                    WebSocketMessageType.Text,
                    endOfMessage: true,
                    token);
            }
        }

        private static async Task ReceiveStreamLiveCloseAsync(
            WebSocket socket,
            CancellationToken token)
        {
            byte[] buffer = new byte[256];
            while (socket.State == WebSocketState.Open && !token.IsCancellationRequested)
            {
                WebSocketReceiveResult result = await socket.ReceiveAsync(
                    new ArraySegment<byte>(buffer),
                    token);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return;
                }
            }
        }

        private void PublishStreamLiveMessage(string gadgetId, string streamId, string message)
        {
            foreach (StreamLiveSubscriber subscriber in streamLiveSubscribers.Values)
            {
                if (string.Equals(subscriber.GadgetID, gadgetId, StringComparison.OrdinalIgnoreCase) &&
                    (string.IsNullOrEmpty(subscriber.StreamID) || string.IsNullOrEmpty(streamId) ||
                     string.Equals(subscriber.StreamID, streamId, StringComparison.OrdinalIgnoreCase)))
                {
                    subscriber.Messages.Writer.TryWrite(message);
                }
            }
        }

        private bool ShouldLogInvalidStreamEvent(string gadgetId)
        {
            long now = DateTime.UtcNow.Ticks;
            long interval = TimeSpan.FromSeconds(10).Ticks;
            while (true)
            {
                if (!invalidStreamLogTicks.TryGetValue(gadgetId, out long previous))
                {
                    return invalidStreamLogTicks.TryAdd(gadgetId, now);
                }
                if (now - previous < interval)
                {
                    return false;
                }
                if (invalidStreamLogTicks.TryUpdate(gadgetId, now, previous))
                {
                    return true;
                }
            }
        }

        // Focused tests use the same parser and state path without starting the full service host.
        internal void ResetStreamablesForTests() => ResetStreamableRuntime();

        internal bool AcceptStreamEventForTests(
            KliveTechGadget gadget,
            JObject value,
            out string error)
        {
            if (!KliveTechStreamProtocol.TryParseEvent(value, out KliveTechParsedStreamEvent? parsed, out error) ||
                parsed == null)
            {
                return false;
            }
            ApplyStreamEvent(gadget, parsed);
            return true;
        }
    }
}
