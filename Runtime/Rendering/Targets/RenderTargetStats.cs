#nullable enable

namespace Apotheosis.Runtime.Rendering.Targets
{
    /// <summary>RT 池与持久目标运行期遥测统计快照。</summary>
    public readonly struct RenderTargetStats
    {
        public readonly int ReferenceWidth;
        public readonly int ReferenceHeight;
        public readonly int ActiveTransientCount;
        public readonly int ActiveChannelLeaseCount;
        public readonly int IdlePooledCount;
        public readonly int RegisteredChannelCount;
        public readonly int PersistentCount;
        public readonly long EstimatedPoolVramBytes;
        public readonly long EstimatedPersistentVramBytes;
        public readonly long TotalCreated;
        public readonly long TotalReused;
        public readonly long TotalEvicted;
        public readonly long ResizeRebuilds;
        public readonly int SuspectedLeakCount;

        public RenderTargetStats(int referenceWidth, int referenceHeight, int activeTransientCount, int activeChannelLeaseCount, int idlePooledCount,
            int registeredChannelCount, int persistentCount, long estimatedPoolVramBytes, long estimatedPersistentVramBytes, long totalCreated,
            long totalReused, long totalEvicted, long resizeRebuilds, int suspectedLeakCount)
        {
            ReferenceWidth = referenceWidth;
            ReferenceHeight = referenceHeight;
            ActiveTransientCount = activeTransientCount;
            ActiveChannelLeaseCount = activeChannelLeaseCount;
            IdlePooledCount = idlePooledCount;
            RegisteredChannelCount = registeredChannelCount;
            PersistentCount = persistentCount;
            EstimatedPoolVramBytes = estimatedPoolVramBytes;
            EstimatedPersistentVramBytes = estimatedPersistentVramBytes;
            TotalCreated = totalCreated;
            TotalReused = totalReused;
            TotalEvicted = totalEvicted;
            ResizeRebuilds = resizeRebuilds;
            SuspectedLeakCount = suspectedLeakCount;
        }

        /// <summary>池与持久目标合计估算显存占用（字节）。</summary>
        public long TotalEstimatedVramBytes => EstimatedPoolVramBytes + EstimatedPersistentVramBytes;
    }
}
