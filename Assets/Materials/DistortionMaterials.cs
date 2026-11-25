#nullable enable

using System;
using Apotheosis.Runtime.Foundation.Collections;
using Apotheosis.Runtime.Rendering;
using Apotheosis.Runtime.Rendering.Materials;
using Apotheosis.Runtime.Rendering.PostProcess;
using Apotheosis.Shaders;
using Microsoft.Xna.Framework.Graphics;

namespace Apotheosis.Materials
{
    /// <summary>常用扭曲通道写入材质预设（配合 <c>RenderManager.PostProcess.DistortionMask</c> 使用）。</summary>
    public static class DistortionMaterials
    {
        private static readonly object _lock = new();
        private static EpochCachedHandle<MaterialHandle> _normalMap;
        private static EpochCachedHandle<MaterialHandle> _radialRing;

        /// <summary>法线/流向贴图扭曲模板材质（受保护单例，请使用 <see cref="CreateNormalMapTransient"/> 或克隆后绑定贴图）。</summary>
        public static MaterialHandle NormalMap => _normalMap.GetOrCreate(_lock, static () => RenderManager.IsUnloading, static () => RenderManager.Materials.Epoch, CreateNormalMapTemplate);

        /// <summary>程序化径向环形冲击波扭曲模板材质（受保护单例，可直接提交或克隆改参）。</summary>
        public static MaterialHandle RadialRing => _radialRing.GetOrCreate(_lock, static () => RenderManager.IsUnloading, static () => RenderManager.Materials.Epoch, CreateRadialRingTemplate);

        /// <summary>克隆单帧瞬时法线扭曲材质并绑定贴图与强度参数（帧末自动回收）。</summary>
        public static MaterialHandle CreateNormalMapTransient(Texture2D normalMap, float distortionStrength = 1f, float chromaticStrength = 1f)
        {
            ArgumentNullException.ThrowIfNull(normalMap);

            MaterialHandle template = NormalMap;
            if (!template.IsValid)
                return MaterialHandle.Invalid;

            MaterialHandle clone = RenderManager.Materials.CloneTransient(template);
            if (RenderManager.Materials.TryGet(clone, out ManagedMaterial? mat))
            {
                mat.SetTexture(PostProcess_DistortionWriteShader.Slot_uImage0, normalMap);
                mat.SetFloat(PostProcess_DistortionWriteShader.Slot_uDistortionStrength, distortionStrength);
                mat.SetFloat(PostProcess_DistortionWriteShader.Slot_uChromaticStrength, chromaticStrength);
            }

            return clone;
        }

        /// <summary>克隆单帧瞬时径向冲击波扭曲材质并配置环宽与强度参数（帧末自动回收）。</summary>
        public static MaterialHandle CreateRadialRingTransient(float innerRadius = 0.22f, float outerRadius = 0.42f, float edgeSoftness = 0.08f,
            float distortionStrength = 1f, float chromaticStrength = 1f)
        {
            MaterialHandle template = RadialRing;
            if (!template.IsValid)
                return MaterialHandle.Invalid;

            MaterialHandle clone = RenderManager.Materials.CloneTransient(template);
            if (RenderManager.Materials.TryGet(clone, out ManagedMaterial? mat))
            {
                mat.SetFloat(PostProcess_DistortionWriteShader.Slot_uInnerRadius, innerRadius);
                mat.SetFloat(PostProcess_DistortionWriteShader.Slot_uOuterRadius, outerRadius);
                mat.SetFloat(PostProcess_DistortionWriteShader.Slot_uEdgeSoftness, edgeSoftness);
                mat.SetFloat(PostProcess_DistortionWriteShader.Slot_uDistortionStrength, distortionStrength);
                mat.SetFloat(PostProcess_DistortionWriteShader.Slot_uChromaticStrength, chromaticStrength);
            }

            return clone;
        }

        private static MaterialHandle CreateNormalMapTemplate()
        {
            MaterialHandle handle = RenderManager.Materials.CreateProtected(Assets.Shaders.PostProcess.DistortionWrite,
                PostProcess_DistortionWriteShader.Technique_NormalMapTech, PostProcess_DistortionWriteShader.Pass_NormalMapTech_DefaultPass,
                PostProcessModule.DistortionBlendState, DepthStencilState.None, RasterizerState.CullNone, SamplerState.LinearClamp, fallbackToMagicPixel: false);

            if (RenderManager.Materials.TryGet(handle, out ManagedMaterial? mat))
            {
                // 显式清空主贴图槽，防止共享 Shader 残留上一次调用绑定的法线贴图
                mat.SetTexture(PostProcess_DistortionWriteShader.Slot_uImage0, null);
                mat.SetFloat(PostProcess_DistortionWriteShader.Slot_uDistortionStrength, 1.0f);
                mat.SetFloat(PostProcess_DistortionWriteShader.Slot_uChromaticStrength, 1.0f);
            }

            return handle;
        }

        private static MaterialHandle CreateRadialRingTemplate()
        {
            MaterialHandle handle = RenderManager.Materials.CreateProtected(Assets.Shaders.PostProcess.DistortionWrite,
                PostProcess_DistortionWriteShader.Technique_RadialRingTech, PostProcess_DistortionWriteShader.Pass_RadialRingTech_DefaultPass,
                PostProcessModule.DistortionBlendState, DepthStencilState.None, RasterizerState.CullNone, SamplerState.LinearClamp, fallbackToMagicPixel: false);

            if (RenderManager.Materials.TryGet(handle, out ManagedMaterial? mat))
            {
                // uOuterRadius(0.42) + uEdgeSoftness(0.08) == 0.50，确保冲击波在 Quad 四边平滑归零无接缝
                mat.SetFloat(PostProcess_DistortionWriteShader.Slot_uDistortionStrength, 1.0f);
                mat.SetFloat(PostProcess_DistortionWriteShader.Slot_uChromaticStrength, 1.0f);
                mat.SetFloat(PostProcess_DistortionWriteShader.Slot_uInnerRadius, 0.22f);
                mat.SetFloat(PostProcess_DistortionWriteShader.Slot_uOuterRadius, 0.42f);
                mat.SetFloat(PostProcess_DistortionWriteShader.Slot_uEdgeSoftness, 0.08f);
            }

            return handle;
        }
    }
}
