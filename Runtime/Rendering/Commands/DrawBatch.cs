#nullable enable

using Apotheosis.Runtime.Rendering.Geometry;
using Apotheosis.Runtime.Rendering.Materials;
using Apotheosis.Runtime.Rendering.Targets;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Apotheosis.Runtime.Rendering.Commands
{
    internal enum DrawBatchKind : byte
    {
        DynamicIndexed = 0,
        StaticIndexed = 1,
        Instanced = 2,
    }

    /// <summary>合批聚拢后的 GPU 绘制单元。</summary>
    internal struct DrawBatch
    {
        public DrawBatchKind Kind;
        public RenderTargetHandle Target;
        public MaterialHandle Material;
        public PrimitiveType PrimitiveType;
        public ulong BatchKey;

        // DynamicIndexed：共享索引缓冲起始位置与图元数
        public int StartIndex;
        public int PrimitiveCount;

        // StaticIndexed / Instanced：静态 GPU 网格及可选世界矩阵
        public IStaticGpuMesh? StaticMesh;
        public MeshHandle BaseMeshHandle;
        public bool HasWorldMatrix;
        public Matrix WorldMatrix;

        // Instanced：共享实例缓冲起始偏移与实例总数
        public int InstanceOffset;
        public int InstanceCount;
    }

    /// <summary>单个渲染时机的单帧统计快照。</summary>
    public readonly struct RenderTimingStats
    {
        public readonly int CommandsSubmitted;
        public readonly int DrawCallsIssued;
        public readonly int BatchesMerged;
        public readonly int DynamicVertices;
        public readonly int PrimitivesDrawn;
        public readonly int InstancesDrawn;

        public RenderTimingStats(int commandsSubmitted, int drawCallsIssued, int batchesMerged, int dynamicVertices, int primitivesDrawn, int instancesDrawn)
        {
            CommandsSubmitted = commandsSubmitted;
            DrawCallsIssued = drawCallsIssued;
            BatchesMerged = batchesMerged;
            DynamicVertices = dynamicVertices;
            PrimitivesDrawn = primitivesDrawn;
            InstancesDrawn = instancesDrawn;
        }
    }
}
