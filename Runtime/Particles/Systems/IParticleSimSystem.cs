#nullable enable

using Apotheosis.Runtime.Particles.Core;
using Microsoft.Xna.Framework;

namespace Apotheosis.Runtime.Particles.Systems
{
    /// <summary>单步模拟上下文（栈上传递，零堆分配，显式携带当前 <see cref="ParticleStoreSlot"/> 对应的宿主元数据）。</summary>
    public readonly ref struct ParticleSimContext
    {
        /// <summary>本步有效模拟时间增量（秒，已乘以子系统 <c>TimeScale</c>）。</summary>
        public readonly float DeltaTime;

        /// <summary>当前存储是否处于局部空间模式。</summary>
        public readonly bool IsLocalSpace;

        /// <summary>当前存储对应的宿主原点世界矩阵（全局存储时为单位矩阵）。</summary>
        public readonly Matrix OriginWorldMatrix;

        /// <summary>当前存储对应的宿主原点世界逆矩阵（用于局部空间力场反变换）。</summary>
        public readonly Matrix InverseOriginMatrix;

        /// <summary>当前存储是否允许受全局世界力场影响。</summary>
        public readonly bool AffectByGlobalFields;

        /// <summary>当前存储允许响应的全局力场位掩码。</summary>
        public readonly ParticleFieldMask GlobalFieldMask;

        public ParticleSimContext(float deltaTime, bool isLocalSpace, scoped in Matrix originWorldMatrix, scoped in Matrix inverseOriginMatrix,
            bool affectByGlobalFields = true, ParticleFieldMask globalFieldMask = ParticleFieldMask.All)
        {
            DeltaTime = deltaTime;
            IsLocalSpace = isLocalSpace;
            OriginWorldMatrix = originWorldMatrix;
            InverseOriginMatrix = inverseOriginMatrix;
            AffectByGlobalFields = affectByGlobalFields;
            GlobalFieldMask = globalFieldMask;
        }

        /// <summary>构造常驻全局世界存储对应的默认模拟上下文。</summary>
        public static ParticleSimContext ForGlobalWorld(float deltaTime)
        {
            Matrix identity = Matrix.Identity;
            return new ParticleSimContext(deltaTime, isLocalSpace: false, in identity, in identity, affectByGlobalFields: true, ParticleFieldMask.All);
        }

        /// <summary>构造指定小型独立粒子子系统对应的模拟上下文。</summary>
        public static ParticleSimContext ForInstance(ParticleInstance instance, float effectiveDeltaTime)
        {
            ParticleOriginSnapshot origin = instance.OriginSnapshot;
            return new ParticleSimContext(effectiveDeltaTime, instance.IsLocalSpace, in origin.WorldMatrix, in origin.InverseWorldMatrix,
                instance.AffectByGlobalFields, instance.GlobalFieldMask);
        }
    }

    /// <summary>粒子模拟系统统一接口（全局仅维护 1 份实例，由粒子工作线程依次驱动各个活跃 <see cref="ParticleStoreSlot"/>）。</summary>
    public interface IParticleSimSystem
    {
        /// <summary>在粒子工作线程中对指定存储槽执行单步模拟。</summary>
        void Execute(ParticleStoreSlot slot, in ParticleSimContext ctx);
    }
}
