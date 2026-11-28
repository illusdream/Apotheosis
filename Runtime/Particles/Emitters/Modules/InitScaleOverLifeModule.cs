#nullable enable

using System;
using Apotheosis.Runtime.Particles.Components;
using Apotheosis.Runtime.Particles.Fragments;
using Apotheosis.Runtime.Utils;
using Friflo.Engine.ECS;
using Microsoft.Xna.Framework;

namespace Apotheosis.Runtime.Particles.Emitters.Modules
{
    /// <summary>生命周期缩放渐变初始化模块（提供 <see cref="ParticleScaleOverLife"/>，默认自动继承每个粒子已写入的 2D/3D 初始缩放作为起始缩放）。</summary>
    [ProvidesComponents(typeof(ParticleScaleOverLife))]
    [RequiresAnyComponent(typeof(ParticlePose2D), typeof(ParticlePose3D))]
    public sealed class InitScaleOverLifeModule : IParticleSpawnModule
    {
        private static readonly ComponentTypes Provided = ComponentTypes.Get<ParticleScaleOverLife>();

        /// <summary>可选固定起始缩放覆盖（为 <c>null</c> 时自动继承每个粒子当前的姿态缩放）。</summary>
        public Vector3? StartScaleOverride { get; }

        /// <summary>生命末期目标缩放倍率。</summary>
        public Vector3 EndScale { get; }

        /// <summary>缩放插值缓动曲线。</summary>
        public EaseType Ease { get; }

        public ParticleSpawnPhase Phase => ParticleSpawnPhase.Behavior;
        public ComponentTypes ProvidedComponents => Provided;

        public InitScaleOverLifeModule(Vector3 endScale, EaseType ease = EaseType.Linear, Vector3? startScaleOverride = null)
        {
            EndScale = endScale;
            Ease = ease;
            StartScaleOverride = startScaleOverride;
        }

        public InitScaleOverLifeModule(Vector2 endScale2D, EaseType ease = EaseType.Linear, Vector2? startScale2DOverride = null)
            : this(new Vector3(endScale2D, 1f), ease, startScale2DOverride.HasValue ? new Vector3(startScale2DOverride.Value, 1f) : null)
        {
        }

        public InitScaleOverLifeModule(float endUniform, EaseType ease = EaseType.Linear, float? startUniformOverride = null)
            : this(new Vector3(endUniform), ease, startUniformOverride.HasValue ? new Vector3(startUniformOverride.Value) : null)
        {
        }

        public void Initialize(in ParticleSpawnBatch batch, in ParticleEmitContext ctx, ref ParticleRandom rng)
        {
            if (StartScaleOverride is Vector3 fixedStart)
            {
                batch.Fill(new ParticleScaleOverLife(fixedStart, EndScale, Ease));
                // 同步回写基础姿态缩放，确保暂停帧或后置 VelocityAlign 基于覆盖后的起始缩放计算
                if (batch.Has<ParticlePose2D>())
                {
                    Span<ParticlePose2D> poses = batch.GetSpan<ParticlePose2D>();
                    var scale2D = new Vector2(fixedStart.X, fixedStart.Y);
                    for (int i = 0; i < poses.Length; i++)
                        poses[i].Scale = scale2D;
                }
                else if (batch.Has<ParticlePose3D>())
                {
                    Span<ParticlePose3D> poses = batch.GetSpan<ParticlePose3D>();
                    for (int i = 0; i < poses.Length; i++)
                        poses[i].Scale = fixedStart;
                }

                return;
            }

            Span<ParticleScaleOverLife> curves = batch.GetSpan<ParticleScaleOverLife>();
            Vector3 endScale = EndScale;
            EaseType ease = Ease;

            if (batch.Has<ParticlePose2D>())
            {
                ReadOnlySpan<ParticlePose2D> poses = batch.GetSpan<ParticlePose2D>();
                Vector2 end2D = new(endScale.X, endScale.Y);
                for (int i = 0; i < curves.Length; i++)
                    curves[i] = new ParticleScaleOverLife(poses[i].Scale, end2D, ease);
            }
            else if (batch.Has<ParticlePose3D>())
            {
                ReadOnlySpan<ParticlePose3D> poses = batch.GetSpan<ParticlePose3D>();
                for (int i = 0; i < curves.Length; i++)
                    curves[i] = new ParticleScaleOverLife(poses[i].Scale, endScale, ease);
            }
            else
            {
                batch.Fill(new ParticleScaleOverLife(Vector3.One, endScale, ease));
            }
        }
    }
}
