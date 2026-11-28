#nullable enable

using System.Runtime.CompilerServices;
using Apotheosis.Runtime.Particles.Components;
using Apotheosis.Runtime.Rendering;
using Apotheosis.Runtime.Rendering.Geometry;
using Microsoft.Xna.Framework;

namespace Apotheosis.Runtime.Particles.Systems
{
    /// <summary>默认图集 2D/3D 面片硬件实例化渲染系统（对应 <c>DefaultBatch.fx / InstancedTech</c> 与 <c>UnitQuad2D</c>）。</summary>
    public sealed class BasicQuadRenderSystem : InstancedShaderRenderSystemBase<QuadAtlasRenderData>
    {
        private MeshHandle _cachedUnitQuad;

        protected override void OnBeginPack() => _cachedUnitQuad = RenderManager.Meshes.UnitQuad2D;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        protected override MeshHandle ResolveBaseMesh(in ParticleRenderBase render, in QuadAtlasRenderData shaderData) => _cachedUnitQuad;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        protected override Vector4 PackCustomData(in ParticleRenderBase render, in QuadAtlasRenderData shaderData)
            => shaderData.UvRect == default ? InstanceData.FullUvRect : shaderData.UvRect;
    }
}
