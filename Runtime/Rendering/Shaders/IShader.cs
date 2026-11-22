#nullable enable

using Microsoft.Xna.Framework;

namespace Apotheosis.Runtime.Rendering.Shaders
{
    /// <summary>
    /// 统一着色器接口：提供帧级/视图级常用全局参数的统一设置入口与 Pass 应用能力，
    /// 使 <see cref="ShaderModule"/> 与 <see cref="RenderManager"/> 能够对所有受管着色器执行一致的参数下发与调度。
    /// </summary>
    public interface IShader
    {
        /// <summary>对应于 <see cref="RenderManager.Shaders"/> 中的唯一 2 字节句柄。</summary>
        ShaderHandle Handle { get; }

        /// <summary>底层着色器资源是否已就绪且可应用。</summary>
        bool IsReady { get; }

        /// <summary>设置全局时间参数（<c>uTime</c>）。</summary>
        void SetTime(float time);

        /// <summary>设置顶点/视图变换矩阵（<c>uTransformMatrix</c>）。</summary>
        void SetTransformMatrix(in Matrix matrix);

        /// <summary>设置当前屏幕/目标分辨率（<c>uScreenResolution</c>）。</summary>
        void SetScreenResolution(Vector2 resolution);

        /// <summary>设置当前摄像机缩放倍率（<c>uZoom</c>）。</summary>
        void SetZoom(Vector2 zoom);

        /// <summary>同步必要的帧级全局参数并应用指定 Technique 与 Pass。</summary>
        void Apply(int techniqueIndex = 0, int passIndex = 0);
    }
}
