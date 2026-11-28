#nullable enable

using System;
using Apotheosis.Runtime.Particles.Components;
using Apotheosis.Runtime.Particles.Fragments;
using Friflo.Engine.ECS;

namespace Apotheosis.Runtime.Particles.Emitters.Modules
{
    /// <summary>序列帧图集动画初始化模块（提供 <see cref="ParticleSheetAnim"/> 并同步写入首帧 <see cref="QuadAtlasRenderData.UvRect"/>）。</summary>
    [ProvidesComponents(typeof(ParticleSheetAnim))]
    [RequiresComponents(typeof(QuadAtlasRenderData))]
    public sealed class InitSheetAnimModule : IParticleSpawnModule
    {
        private static readonly ComponentTypes Provided = ComponentTypes.Get<ParticleSheetAnim>();

        /// <summary>序列帧基础配置。</summary>
        public ParticleSheetAnim SheetAnim { get; }

        /// <summary>是否随机化每个粒子的起始帧索引（适用于 <c>FrameRate &gt; 0</c> 循环或 <c>&lt; 0</c> 静态随机单帧）。</summary>
        public bool RandomStartFrame { get; }

        public ParticleSpawnPhase Phase => ParticleSpawnPhase.Behavior;
        public ComponentTypes ProvidedComponents => Provided;

        public InitSheetAnimModule(ParticleSheetAnim sheetAnim, bool randomStartFrame = false)
        {
            SheetAnim = sheetAnim;
            RandomStartFrame = randomStartFrame;
        }

        public InitSheetAnimModule(ushort columns, ushort rows, ushort frameCount = 0, float frameRate = 0f, ushort startFrame = 0, bool randomStartFrame = false)
            : this(new ParticleSheetAnim(columns, rows, frameCount, frameRate, startFrame), randomStartFrame)
        {
        }

        public void Initialize(in ParticleSpawnBatch batch, in ParticleEmitContext ctx, ref ParticleRandom rng)
        {
            bool hasQuadAtlas = batch.Has<QuadAtlasRenderData>();
            ParticleSheetAnim tpl = SheetAnim;
            ushort cols = tpl.Columns > 0 ? tpl.Columns : (ushort)1;
            ushort rows = tpl.Rows > 0 ? tpl.Rows : (ushort)1;
            long maxFramesLong = (long)cols * rows;
            ushort maxFrames = maxFramesLong > ushort.MaxValue ? ushort.MaxValue : (ushort)maxFramesLong;
            ushort frameCount = tpl.FrameCount > 0 && tpl.FrameCount <= maxFrames ? tpl.FrameCount : maxFrames;
            ushort defaultStart = tpl.CurrentFrame < frameCount ? tpl.CurrentFrame : (ushort)0;

            if (!RandomStartFrame || frameCount <= 1)
            {
                ParticleSheetAnim single = new(cols, rows, frameCount, tpl.FrameRate, defaultStart);
                single.UvSynced = hasQuadAtlas;
                batch.Fill(single);
                if (hasQuadAtlas)
                    batch.Fill(QuadAtlasRenderData.FromGridCell(cols, rows, defaultStart));
                return;
            }

            Span<ParticleSheetAnim> anims = batch.GetSpan<ParticleSheetAnim>();
            Span<QuadAtlasRenderData> quads = hasQuadAtlas ? batch.GetSpan<QuadAtlasRenderData>() : default;
            for (int i = 0; i < anims.Length; i++)
            {
                ushort startFrame = (ushort)rng.NextInt(0, frameCount);
                ParticleSheetAnim anim = new(cols, rows, frameCount, tpl.FrameRate, startFrame);
                anim.UvSynced = hasQuadAtlas;
                anims[i] = anim;
                if (hasQuadAtlas)
                    quads[i] = QuadAtlasRenderData.FromGridCell(cols, rows, startFrame);
            }
        }
    }
}
