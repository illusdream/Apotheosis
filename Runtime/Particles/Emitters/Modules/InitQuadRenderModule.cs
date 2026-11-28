#nullable enable

using System;
using Apotheosis.Runtime.Particles.Components;
using Apotheosis.Runtime.Particles.Fragments;
using Apotheosis.Runtime.Rendering.Geometry;
using Apotheosis.Runtime.Rendering.Materials;
using Apotheosis.Runtime.Rendering.Targets;
using Friflo.Engine.ECS;
using Microsoft.Xna.Framework;

namespace Apotheosis.Runtime.Particles.Emitters.Modules
{
    /// <summary>基础图集面片渲染初始化模块（提供并初始化 <see cref="ParticleRenderBase"/> 与 <see cref="QuadAtlasRenderData"/>）。</summary>
    [ProvidesComponents(typeof(ParticleRenderBase), typeof(QuadAtlasRenderData))]
    public sealed class InitQuadRenderModule : IParticleSpawnModule
    {
        private static readonly ComponentTypes Provided = ComponentTypes.Get<ParticleRenderBase, QuadAtlasRenderData>();

        /// <summary>目标渲染目标句柄。</summary>
        public RenderTargetHandle Target { get; }

        /// <summary>粒子使用的受管硬件实例化材质句柄。</summary>
        public MaterialHandle Material { get; }

        /// <summary>未缩放时的基准像素宽高。</summary>
        public Vector2 BaseSize { get; }

        /// <summary>起始 HDR 颜色（固定色或随机下界）。</summary>
        public Vector4 StartColor { get; }

        /// <summary>可选起始 HDR 颜色上界（非空时每个粒子在两色间随机插值）。</summary>
        public Vector4? StartColorMax { get; }

        /// <summary>归一化图集 UV 矩形。</summary>
        public Vector4 UvRect { get; }

        /// <summary>同目标内的子图层编号。</summary>
        public byte SubLayer { get; }

        /// <summary>同子图层内的有符号排序权重。</summary>
        public short SortOrder { get; }

        /// <summary>相对面片中心的归一化锚点偏移。</summary>
        public Vector2 Pivot { get; }

        public ParticleSpawnPhase Phase => ParticleSpawnPhase.Render;
        public ComponentTypes ProvidedComponents => Provided;

        public InitQuadRenderModule(RenderTargetHandle target, MaterialHandle material, Vector2 baseSize,
            Vector4? startColor = null, Vector4? startColorMax = null, Vector4? uvRect = null,
            byte subLayer = 0, short sortOrder = 0, Vector2 pivot = default)
        {
            Target = target;
            Material = material;
            BaseSize = baseSize;
            StartColor = startColor ?? Vector4.One;
            StartColorMax = startColorMax;
            UvRect = uvRect ?? InstanceData.FullUvRect;
            SubLayer = subLayer;
            SortOrder = sortOrder;
            Pivot = pivot;
        }

        public void Initialize(in ParticleSpawnBatch batch, in ParticleEmitContext ctx, ref ParticleRandom rng)
        {
            batch.Fill(new QuadAtlasRenderData(UvRect));

            if (StartColorMax is null)
            {
                batch.Fill(new ParticleRenderBase(Target, Material, BaseSize, StartColor, SubLayer, SortOrder, Pivot));
                return;
            }

            Vector4 cMin = StartColor;
            Vector4 cMax = StartColorMax.Value;
            Span<ParticleRenderBase> renders = batch.GetSpan<ParticleRenderBase>();
            for (int i = 0; i < renders.Length; i++)
                renders[i] = new ParticleRenderBase(Target, Material, BaseSize, rng.NextColorLerp(cMin, cMax), SubLayer, SortOrder, Pivot);
        }
    }
}
