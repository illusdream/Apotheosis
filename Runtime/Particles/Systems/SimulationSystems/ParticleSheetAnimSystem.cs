#nullable enable

using System;
using Apotheosis.Runtime.Particles.Components;
using Apotheosis.Runtime.Particles.Core;

namespace Apotheosis.Runtime.Particles.Systems
{
    /// <summary>序列帧图集动画系统（更新帧索引并同步写入 <see cref="QuadAtlasRenderData.UvRect"/>）。</summary>
    public sealed class ParticleSheetAnimSystem : IParticleSimSystem
    {
        private static readonly ParticleQuerySlot QuerySlot = ParticleQuerySlot.Allocate();

        public void Execute(ParticleStoreSlot slot, in ParticleSimContext ctx)
        {
            float dt = ctx.DeltaTime;
            if (!float.IsFinite(dt) || !(dt >= 0f) || slot.EntityCount == 0)
                return;

            var query = slot.GetOrCreateQuery(QuerySlot, static s => s.Query<ParticleLife, ParticleSheetAnim, QuadAtlasRenderData>());
            foreach (var (lifeChunk, animChunk, quadChunk, _) in query.Chunks)
            {
                ReadOnlySpan<ParticleLife> lives = lifeChunk.Span;
                Span<ParticleSheetAnim> anims = animChunk.Span;
                Span<QuadAtlasRenderData> quads = quadChunk.Span;
                for (int i = 0; i < anims.Length; i++)
                {
                    ref ParticleSheetAnim anim = ref anims[i];
                    if (anim.Columns == 0 || anim.Rows == 0 || anim.FrameCount == 0)
                        continue;

                    ushort nextFrame = anim.CurrentFrame < anim.FrameCount ? anim.CurrentFrame : (ushort)0;
                    // FrameRate < 0 表示静态锁定当前起始帧（仅在首帧同步 UV，不推进帧号）
                    if (anim.FrameCount > 1 && dt > 0f && !(anim.FrameRate < 0f))
                    {
                        if (anim.FrameRate > 0f && float.IsFinite(anim.FrameRate))
                        {
                            // 用 double 算整帧步数并先取模再累加，杜绝 float 倒数截断卡帧、高帧率整数溢出及非有限 FrameTimer 污染
                            anim.FrameTimer += dt;
                            double rawSteps = Math.Floor((double)anim.FrameTimer * anim.FrameRate);
                            if (!double.IsFinite(rawSteps))
                            {
                                anim.FrameTimer = 0f;
                            }
                            else if (rawSteps >= 1.0)
                            {
                                anim.FrameTimer = MathF.Max(0f, (float)(anim.FrameTimer - rawSteps / anim.FrameRate));
                                long delta = (long)(rawSteps % anim.FrameCount);
                                nextFrame = (ushort)((nextFrame + delta) % anim.FrameCount);
                            }
                        }
                        else if (anim.FrameRate == 0f)
                        {
                            float norm = lives[i].NormalizedAge;
                            float clamped = !(norm > 0f) ? 0f : (norm >= 1f ? 1f : norm);
                            int frameIdx = (int)(clamped * anim.FrameCount);
                            nextFrame = (ushort)Math.Clamp(frameIdx, 0, anim.FrameCount - 1);
                        }
                    }

                    // 首帧强制同步一次，避免配合 FillQuadRender 默认 FullUvRect 显示整张序列帧大图
                    if (!anim.UvSynced || nextFrame != anim.CurrentFrame)
                    {
                        anim.CurrentFrame = nextFrame;
                        anim.UvSynced = true;
                        quads[i] = QuadAtlasRenderData.FromGridCell(anim.Columns, anim.Rows, nextFrame);
                    }
                }
            }
        }
    }
}
