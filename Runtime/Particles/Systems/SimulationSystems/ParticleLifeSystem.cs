#nullable enable

using System;
using Apotheosis.Runtime.Particles.Components;
using Apotheosis.Runtime.Particles.Core;
using Friflo.Engine.ECS;

namespace Apotheosis.Runtime.Particles.Systems
{
    /// <summary>寿命推进系统：更新存活时长与归一化寿命进度，并将到期粒子录制进 <see cref="CommandBuffer"/> 批量销毁。</summary>
    public sealed class ParticleLifeSystem : IParticleSimSystem
    {
        private static readonly ParticleQuerySlot QuerySlot = ParticleQuerySlot.Allocate();

        public void Execute(ParticleStoreSlot slot, in ParticleSimContext ctx)
        {
            float dt = ctx.DeltaTime;
            if (!(dt > 0f) || slot.EntityCount == 0)
                return;

            var query = slot.GetOrCreateQuery(QuerySlot, static s => s.Query<ParticleLife>());
            CommandBuffer cmd = slot.CommandBuffer;

            foreach (var (lifeChunk, entities) in query.Chunks)
            {
                Span<ParticleLife> lives = lifeChunk.Span;
                ReadOnlySpan<int> ids = entities.Ids;
                for (int i = 0; i < lives.Length; i++)
                {
                    ref ParticleLife life = ref lives[i];
                    float prevAge = life.Age;
                    // 先判旧 Age 再推进：既拦截 NaN/非正寿命，又保证 MaxAge <= dt 的单帧闪光至少渲染 1 帧、1.0s 粒子精确渲染 60 帧
                    if (!(life.MaxAge > 0f) || !(prevAge < life.MaxAge))
                    {
                        life.NormalizedAge = 1f;
                        cmd.DeleteEntity(ids[i]);
                    }
                    else
                    {
                        life.NormalizedAge = prevAge > 0f ? prevAge / life.MaxAge : 0f;
                        life.Age = prevAge + dt;
                    }
                }
            }

            // 遍历结束后立即回放删除命令，避免到期粒子白跑后续系统并在碰撞等系统二次 DeleteEntity 时抛异常
            slot.PlaybackCommandBuffer();
        }
    }
}
