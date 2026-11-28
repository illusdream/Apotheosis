#nullable enable

using System;
using Apotheosis.Runtime.Particles.Components;
using Apotheosis.Runtime.Particles.Fragments;
using Apotheosis.Runtime.Utils;
using Friflo.Engine.ECS;
using Microsoft.Xna.Framework;

namespace Apotheosis.Runtime.Particles.Emitters.Modules
{
    /// <summary>生命周期颜色渐变初始化模块（提供 <see cref="ParticleColorOverLife"/>，默认自动继承每个粒子已写入的 <see cref="ParticleRenderBase.Color"/> 作为起始色）。</summary>
    [ProvidesComponents(typeof(ParticleColorOverLife))]
    [RequiresComponents(typeof(ParticleRenderBase))]
    public sealed class InitColorOverLifeModule : IParticleSpawnModule
    {
        private static readonly ComponentTypes Provided = ComponentTypes.Get<ParticleColorOverLife>();

        /// <summary>可选固定起始色覆盖（为 <c>null</c> 时自动继承每个粒子当前的 <see cref="ParticleRenderBase.Color"/>）。</summary>
        public Vector4? StartColorOverride { get; }

        /// <summary>生命末期 HDR 目标颜色。</summary>
        public Vector4 EndColor { get; }

        /// <summary>颜色插值缓动曲线。</summary>
        public EaseType Ease { get; }

        public ParticleSpawnPhase Phase => ParticleSpawnPhase.Behavior;
        public ComponentTypes ProvidedComponents => Provided;

        public InitColorOverLifeModule(Vector4 endColor, EaseType ease = EaseType.Linear, Vector4? startColorOverride = null)
        {
            EndColor = endColor;
            Ease = ease;
            StartColorOverride = startColorOverride;
        }

        public InitColorOverLifeModule(Color endColor, EaseType ease = EaseType.Linear, Color? startColorOverride = null)
            : this(endColor.ToVector4(), ease, startColorOverride?.ToVector4())
        {
        }

        public void Initialize(in ParticleSpawnBatch batch, in ParticleEmitContext ctx, ref ParticleRandom rng)
        {
            if (StartColorOverride is Vector4 fixedStart)
            {
                batch.Fill(new ParticleColorOverLife(fixedStart, EndColor, Ease));
                // 同步回写基础颜色，保证在游戏暂停或 TimeScale == 0 爆发时首帧视觉与渐变起点一致
                if (batch.Has<ParticleRenderBase>())
                {
                    Span<ParticleRenderBase> renders = batch.GetSpan<ParticleRenderBase>();
                    for (int i = 0; i < renders.Length; i++)
                        renders[i].Color = fixedStart;
                }

                return;
            }

            Span<ParticleColorOverLife> curves = batch.GetSpan<ParticleColorOverLife>();
            if (batch.Has<ParticleRenderBase>())
            {
                ReadOnlySpan<ParticleRenderBase> renders = batch.GetSpan<ParticleRenderBase>();
                Vector4 endCol = EndColor;
                EaseType ease = Ease;
                for (int i = 0; i < curves.Length; i++)
                    curves[i] = new ParticleColorOverLife(renders[i].Color, endCol, ease);
            }
            else
            {
                batch.Fill(new ParticleColorOverLife(Vector4.One, EndColor, Ease));
            }
        }
    }
}
