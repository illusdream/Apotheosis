#nullable enable

using Apotheosis.Runtime.Particles.Core;
using Apotheosis.Runtime.Rendering.Targets;
using Microsoft.Xna.Framework;

namespace Apotheosis.Runtime.Particles.Systems
{
    /// <summary>单 Store 渲染打包上下文（栈上传递，零堆分配）。</summary>
    public readonly ref struct ParticleRenderContext
    {
        /// <summary>当前存储是否处于局部空间模式。</summary>
        public readonly bool IsLocalSpace;

        /// <summary>当前存储对应的宿主原点世界矩阵（仅 <see cref="IsLocalSpace"/> 为真时生效）。</summary>
        public readonly Matrix OriginWorldMatrix;

        /// <summary>可选渲染目标覆盖（非空时覆盖粒子自身 <c>ParticleRenderBase.Target</c>）。</summary>
        public readonly RenderTargetHandle? TargetOverride;

        public ParticleRenderContext(bool isLocalSpace, scoped in Matrix originWorldMatrix, RenderTargetHandle? targetOverride = null)
        {
            IsLocalSpace = isLocalSpace;
            OriginWorldMatrix = originWorldMatrix;
            TargetOverride = targetOverride;
        }

        /// <summary>构造全局世界存储对应的默认渲染上下文。</summary>
        public static ParticleRenderContext ForGlobalWorld()
        {
            Matrix identity = Matrix.Identity;
            return new ParticleRenderContext(isLocalSpace: false, in identity, targetOverride: null);
        }

        /// <summary>构造指定小型独立粒子子系统对应的渲染上下文。</summary>
        public static ParticleRenderContext ForInstance(ParticleInstance instance)
        {
            ParticleOriginSnapshot origin = instance.OriginSnapshot;
            return new ParticleRenderContext(instance.IsLocalSpace, in origin.WorldMatrix, instance.TargetOverride);
        }
    }

    /// <summary>以 Shader 为粒度的粒子渲染系统接口（工作线程打包写入三缓冲，主线程无阻塞提交 <c>RenderCommand</c>）。</summary>
    public interface IParticleRenderSystem
    {
        /// <summary>工作线程开始打包新渲染帧前准备当前 <c>Write</c> 槽。</summary>
        void BeginPack();

        /// <summary>工作线程将指定存储槽中匹配本 Shader 渲染组件的粒子打包进当前 <c>Write</c> 槽。</summary>
        void PackStore(ParticleStoreSlot slot, in ParticleRenderContext ctx);

        /// <summary>工作线程完成全部存储打包后，将当前 <c>Write</c> 槽发布至 <c>Ready</c> 槽。</summary>
        void EndPack();

        /// <summary>打包中途发生异常时中止本次写入而不发布半成品快照。</summary>
        void AbortPack();

        /// <summary>工作线程清空当前 <c>Write</c> 槽并立即发布空快照（严禁触碰主线程正在绘制的 <c>Reading</c> 槽）。</summary>
        void PublishClearedSnapshot();

        /// <summary>主线程在渲染前无等待接管最新 <c>Ready</c> 槽并向 <c>RenderManager.Commands</c> 提交渲染命令。</summary>
        void SubmitOnMainThread();

        /// <summary>Mod 卸载且工作线程退出后注销全部已注册的网格句柄并释放缓冲（运行于 tML 后台卸载线程）。</summary>
        void DisposeResources();
    }
}
