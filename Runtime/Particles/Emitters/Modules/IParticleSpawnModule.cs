#nullable enable

using System;
using Apotheosis.Runtime.Particles.Fragments;
using Friflo.Engine.ECS;

namespace Apotheosis.Runtime.Particles.Emitters.Modules
{
    /// <summary>发射器初始化模块的执行阶段（<see cref="ParticleEmitter.Bake"/> 按此升序稳定排序，同阶段保留注册顺序）。</summary>
    public enum ParticleSpawnPhase : int
    {
        /// <summary>核心基础阶段：寿命与空间位置/初速度（如 <c>ParticleLife</c>、<c>ParticleSpatial</c>）。</summary>
        Core = 100,

        /// <summary>姿态阶段：2D 或 3D 初始旋转、角速度与缩放（如 <c>ParticlePose2D</c>、<c>ParticlePose3D</c>）。</summary>
        Pose = 200,

        /// <summary>渲染阶段：渲染路由、颜色、基准尺寸与 Shader 专属组件（如 <c>ParticleRenderBase</c>、<c>QuadAtlasRenderData</c>）。</summary>
        Render = 300,

        /// <summary>行为阶段：重力、阻尼、生命周期渐变曲线、序列帧、力场接收器、碰撞等行为组件。</summary>
        Behavior = 400,

        /// <summary>后置修饰阶段：依赖前述速度/姿态/缩放/颜色结果的衍生初始化（如速度对齐拉伸、自定义后置 Lambda）。</summary>
        PostInit = 500,
    }

    /// <summary>声明该发射器模块向粒子原型提供并负责初始化的 ECS 组件类型（供 Roslyn 编译期分析器校验）。</summary>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = true, Inherited = true)]
    public sealed class ProvidesComponentsAttribute : Attribute
    {
        /// <summary>本模块提供的组件类型列表。</summary>
        public Type[] ComponentTypes { get; }

        public ProvidesComponentsAttribute(params Type[] componentTypes) => ComponentTypes = componentTypes;
    }

    /// <summary>声明该发射器模块强依赖的全部前置 ECS 组件类型（AND 关系，供 Roslyn 编译期分析器校验）。</summary>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = true, Inherited = true)]
    public sealed class RequiresComponentsAttribute : Attribute
    {
        /// <summary>必须全部存在的前置组件类型列表。</summary>
        public Type[] ComponentTypes { get; }

        public RequiresComponentsAttribute(params Type[] componentTypes) => ComponentTypes = componentTypes;
    }

    /// <summary>声明该发射器模块依赖的候选前置 ECS 组件类型（OR 关系，至少须存在其一，供 Roslyn 编译期分析器校验）。</summary>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = true, Inherited = true)]
    public sealed class RequiresAnyComponentAttribute : Attribute
    {
        /// <summary>候选前置组件类型列表（至少需满足其一）。</summary>
        public Type[] ComponentTypes { get; }

        public RequiresAnyComponentAttribute(params Type[] componentTypes) => ComponentTypes = componentTypes;
    }

    /// <summary>Niagara 风格粒子出生初始化模块接口（构造即固化为不可变只读对象，在粒子工作线程按批次 <see cref="ParticleSpawnBatch"/> 连续初始化）。</summary>
    public interface IParticleSpawnModule
    {
        /// <summary>模块所属执行阶段，用于 <see cref="ParticleEmitter.Bake"/> 自动拓扑排序。</summary>
        ParticleSpawnPhase Phase { get; }

        /// <summary>本模块向粒子原型贡献并负责初始化的 ECS 组件类型集合。</summary>
        ComponentTypes ProvidedComponents { get; }

        /// <summary>在粒子工作线程对刚连续分配的新粒子批次执行单遍初始化。</summary>
        void Initialize(in ParticleSpawnBatch batch, in ParticleEmitContext ctx, ref ParticleRandom rng);
    }
}
