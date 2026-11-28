#nullable enable

using System.Runtime.CompilerServices;
using Apotheosis.Runtime.Particles.Components;
using Apotheosis.Runtime.Rendering.Geometry;
using Apotheosis.Runtime.Rendering.Materials;
using Apotheosis.Runtime.Rendering.Targets;
using Microsoft.Xna.Framework;

namespace Apotheosis.Runtime.Particles.Fragments
{
    /// <summary>引擎内置基础多组件布局碎片（零散单组件请直接通过 <c>.With&lt;T&gt;()</c> 挂接；玩法特化组合请放于 <c>Content/</c>）。</summary>
    public static class BaseParticleFragments
    {
        /// <summary>最小核心模拟布局（<c>ParticleLife + ParticleSpatial</c>）。</summary>
        public static readonly ParticleFragment Core = ParticleFragment.From<ParticleLife, ParticleSpatial>();

        /// <summary>基础 2D 图集面片标配布局（<c>Life + Spatial + Pose2D + RenderBase + QuadAtlas</c>）。</summary>
        public static readonly ParticleFragment Basic2DQuad = ParticleFragment.From<ParticleLife, ParticleSpatial, ParticlePose2D, ParticleRenderBase, QuadAtlasRenderData>();

        /// <summary>基础 3D 翻滚/Billboard 图集面片标配布局（<c>Life + Spatial + Pose3D + RenderBase + QuadAtlas</c>）。</summary>
        public static readonly ParticleFragment Basic3DQuad = ParticleFragment.From<ParticleLife, ParticleSpatial, ParticlePose3D, ParticleRenderBase, QuadAtlasRenderData>();

        /// <summary>生命周期颜色与缩放渐变组合布局（<c>ParticleColorOverLife + ParticleScaleOverLife</c>）。</summary>
        public static readonly ParticleFragment OverLifeCurves = ParticleFragment.From<ParticleColorOverLife, ParticleScaleOverLife>();

        /// <summary>序列帧图集动画布局（<c>ParticleSheetAnim + QuadAtlasRenderData</c>）。</summary>
        public static readonly ParticleFragment SheetAnim = ParticleFragment.From<ParticleSheetAnim, QuadAtlasRenderData>();
    }

    /// <summary>面向发射器编写的无状态批次快捷填充助手（仅作单次 <c>Fill</c> 语法糖，不引入多余覆盖）。</summary>
    public static class ParticleSpawnBatchExtensions
    {
        /// <summary>一次性填充基础图集面片的共享渲染路由与 UV 矩形。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void FillQuadRender(this in ParticleSpawnBatch batch, RenderTargetHandle target, MaterialHandle material, Vector2 baseSize,
            Vector4? color = null, Vector4? uvRect = null, byte subLayer = 0, short sortOrder = 0, Vector2 pivot = default)
        {
            batch.Fill(new ParticleRenderBase(target, material, baseSize, color, subLayer, sortOrder, pivot));
            batch.Fill(new QuadAtlasRenderData(uvRect ?? InstanceData.FullUvRect));
        }
    }
}
