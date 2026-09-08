using System;

namespace PurrNet.Prediction
{
    /// <summary>
    /// Allocation-free scalar diagnostics for attributing retained prediction memory.
    /// The snapshot owns no prediction objects, collections, histories, or packet buffers.
    /// </summary>
    [Serializable]
    public readonly struct PredictionRetentionSnapshot
    {
        public readonly int systemCount;
        public readonly int instanceCount;
        public readonly int hierarchySpawnedRecordCount;
        public readonly int hierarchyRecordCount;
        public readonly int hierarchyInstanceCount;
        public readonly int verifiedStoreCount;
        public readonly int pendingVerifiedRetirementCount;
        public readonly long verifiedEntryCount;
        public readonly long verifiedEntryCapacity;
        public readonly long verifiedRetirementsScheduled;
        public readonly long verifiedRetirementsCancelled;
        public readonly long verifiedRetirementsCompleted;
        public readonly long verifiedStoresDisposed;
        public readonly int serverInputPlayerCount;
        public readonly int serverQueuedInputCount;
        public readonly long serverQueuedInputBufferCapacityBytes;
        public readonly int serverClientFrameCount;
        public readonly long serverClientFrameBufferCapacityBytes;
        public readonly int inputCacheInitializedSlotCount;
        public readonly long inputCacheBufferCapacityBytes;
        public readonly int newestInputInitializedSlotCount;
        public readonly long newestInputBufferCapacityBytes;
        public readonly int visibilityTimelinePlayerCount;
        public readonly int visibilityStatePlayerCount;
        public readonly int visibilityScratchPlayerCount;
        public readonly int desyncTrackingPlayerCount;

        internal PredictionRetentionSnapshot(
            int systemCount,
            int instanceCount,
            int hierarchySpawnedRecordCount,
            int hierarchyRecordCount,
            int hierarchyInstanceCount,
            int verifiedStoreCount,
            int pendingVerifiedRetirementCount,
            long verifiedEntryCount,
            long verifiedEntryCapacity,
            long verifiedRetirementsScheduled,
            long verifiedRetirementsCancelled,
            long verifiedRetirementsCompleted,
            long verifiedStoresDisposed,
            int serverInputPlayerCount,
            int serverQueuedInputCount,
            long serverQueuedInputBufferCapacityBytes,
            int serverClientFrameCount,
            long serverClientFrameBufferCapacityBytes,
            int inputCacheInitializedSlotCount,
            long inputCacheBufferCapacityBytes,
            int newestInputInitializedSlotCount,
            long newestInputBufferCapacityBytes,
            int visibilityTimelinePlayerCount,
            int visibilityStatePlayerCount,
            int visibilityScratchPlayerCount,
            int desyncTrackingPlayerCount)
        {
            this.systemCount = systemCount;
            this.instanceCount = instanceCount;
            this.hierarchySpawnedRecordCount = hierarchySpawnedRecordCount;
            this.hierarchyRecordCount = hierarchyRecordCount;
            this.hierarchyInstanceCount = hierarchyInstanceCount;
            this.verifiedStoreCount = verifiedStoreCount;
            this.pendingVerifiedRetirementCount = pendingVerifiedRetirementCount;
            this.verifiedEntryCount = verifiedEntryCount;
            this.verifiedEntryCapacity = verifiedEntryCapacity;
            this.verifiedRetirementsScheduled = verifiedRetirementsScheduled;
            this.verifiedRetirementsCancelled = verifiedRetirementsCancelled;
            this.verifiedRetirementsCompleted = verifiedRetirementsCompleted;
            this.verifiedStoresDisposed = verifiedStoresDisposed;
            this.serverInputPlayerCount = serverInputPlayerCount;
            this.serverQueuedInputCount = serverQueuedInputCount;
            this.serverQueuedInputBufferCapacityBytes = serverQueuedInputBufferCapacityBytes;
            this.serverClientFrameCount = serverClientFrameCount;
            this.serverClientFrameBufferCapacityBytes = serverClientFrameBufferCapacityBytes;
            this.inputCacheInitializedSlotCount = inputCacheInitializedSlotCount;
            this.inputCacheBufferCapacityBytes = inputCacheBufferCapacityBytes;
            this.newestInputInitializedSlotCount = newestInputInitializedSlotCount;
            this.newestInputBufferCapacityBytes = newestInputBufferCapacityBytes;
            this.visibilityTimelinePlayerCount = visibilityTimelinePlayerCount;
            this.visibilityStatePlayerCount = visibilityStatePlayerCount;
            this.visibilityScratchPlayerCount = visibilityScratchPlayerCount;
            this.desyncTrackingPlayerCount = desyncTrackingPlayerCount;
        }
    }

    public partial class PredictionManager
    {
        /// <summary>
        /// Captures current retained collection and packet-buffer sizes without changing
        /// prediction state or retaining references to the measured objects.
        /// </summary>
        public PredictionRetentionSnapshot CaptureRetentionSnapshot()
        {
            var verified = CaptureVerifiedHistoryDiagnostics();

            int queuedInputs = 0;
            long queuedInputBytes = 0;
            foreach (var queue in _clientTicks.Values)
            {
                queuedInputs += queue.byTick.Count;
                foreach (var value in queue.byTick.Values)
                {
                    var buffer = value.inputPacket?.buffer;
                    if (buffer != null)
                        queuedInputBytes += buffer.Length;
                }
            }

            long clientFrameBytes = 0;
            for (var i = 0; i < _clientFrames.Count; i++)
            {
                var buffer = _clientFrames[i].packer?.buffer;
                if (buffer != null)
                    clientFrameBytes += buffer.Length;
            }

            int inputCacheSlots = 0;
            long inputCacheBytes = 0;
            if (_inputBlockCache != null)
            {
                for (var i = 0; i < _inputBlockCache.Length; i++)
                {
                    var slot = _inputBlockCache[i];
                    if (slot.packer == null && slot.guaranteedFramedPacker == null)
                        continue;
                    inputCacheSlots++;
                    if (slot.packer?.buffer != null)
                        inputCacheBytes += slot.packer.buffer.Length;
                    if (slot.guaranteedFramedPacker?.buffer != null)
                        inputCacheBytes += slot.guaranteedFramedPacker.buffer.Length;
                }
            }

            int newestInputSlots = 0;
            long newestInputBytes = 0;
            if (_newestInputRing != null)
            {
                for (var i = 0; i < _newestInputRing.Length; i++)
                {
                    var bits = _newestInputRing[i].bits;
                    if (bits == null)
                        continue;
                    newestInputSlots++;
                    if (bits.buffer != null)
                        newestInputBytes += bits.buffer.Length;
                }
            }

            int visibilityStatePlayers = _hiddenVisibility.Count +
                                         _visibilityAcquisitions.Count +
                                         _dirtyVisibility.Count +
                                         _hiddenVisibilityAckCandidates.Count +
                                         _retiredVisibilityRoots.Count +
                                         _pendingVisibilityDeletes.Count;
            int visibilityScratchPlayers = _hierarchyBaselineScratchByPlayer.Count +
                                           _hiddenPiecesScratchByPlayer.Count;
            int desyncPlayers = _pendingDesyncHeals.Count +
                                _desyncHealServedTick.Count +
                                _desyncResyncServedTick.Count +
                                _desyncNoticeCooldownTick.Count;

            int hierarchySpawnedRecords = hierarchy
                ? hierarchy.retentionSpawnedRecordCount
                : 0;
            int hierarchyRecords = hierarchy ? hierarchy.retentionRecordCount : 0;
            int hierarchyInstances = hierarchy ? hierarchy.retentionInstanceCount : 0;

            return new PredictionRetentionSnapshot(
                _systemsCount,
                _instanceMap.Count,
                hierarchySpawnedRecords,
                hierarchyRecords,
                hierarchyInstances,
                verified.storeCount,
                verified.retiredComponentCount,
                verified.storedEntryCount,
                verified.entryCapacity,
                verified.retirementsScheduled,
                verified.retirementsCancelled,
                verified.retirementsCompleted,
                verified.storesDisposed,
                _clientTicks.Count,
                queuedInputs,
                queuedInputBytes,
                _clientFrames.Count,
                clientFrameBytes,
                inputCacheSlots,
                inputCacheBytes,
                newestInputSlots,
                newestInputBytes,
                _playerVisibility.Count,
                visibilityStatePlayers,
                visibilityScratchPlayers,
                desyncPlayers);
        }
    }
}
