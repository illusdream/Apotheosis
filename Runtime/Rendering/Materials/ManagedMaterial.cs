#nullable enable

using System;
using Apotheosis.Runtime.Rendering.Shaders;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Terraria.GameContent;

namespace Apotheosis.Runtime.Rendering.Materials
{
    /// <summary>受管材质实例，封装着色器、GPU 管线状态及槽位化参数表。</summary>
    public sealed class ManagedMaterial
    {
        private enum ParamKind : byte
        {
            None = 0,
            Bool,
            Int,
            Float,
            FloatArray,
            Vector2,
            Vector2Array,
            Vector3,
            Vector3Array,
            Vector4,
            Vector4Array,
            Matrix,
            MatrixArray,
            Texture,
        }

        private struct ParamEntry
        {
            public int Slot;
            public ParamKind Kind;
            public int IntValue;
            public Vector4 Numeric;   // 存 Float / Vector2 / Vector3 / Vector4（零装箱）
            public Matrix MatrixValue;
            public object? RefValue;  // 存 Texture / float[] / Vector2[] / Vector3[] / Vector4[] / Matrix[]
        }

        private readonly object _paramLock = new();
        private ParamEntry[] _params = Array.Empty<ParamEntry>();
        private int _paramCount;

        public MaterialHandle Handle { get; internal set; }
        public ShaderHandle Shader { get; set; }
        public int TechniqueIndex { get; set; }
        public int PassIndex { get; set; }

        public BlendState BlendState { get; set; } = BlendState.AlphaBlend;
        public DepthStencilState DepthStencilState { get; set; } = DepthStencilState.None;
        public RasterizerState RasterizerState { get; set; } = RasterizerState.CullNone;
        public SamplerState Sampler0 { get; set; } = SamplerState.LinearClamp;
        public SamplerState? Sampler1 { get; set; }
        public SamplerState? Sampler2 { get; set; }
        public SamplerState? Sampler3 { get; set; }

        /// <summary>主贴图未绑定时是否自动回退到白色像素 <see cref="TextureAssets.MagicPixel"/>。</summary>
        public bool FallbackToMagicPixel { get; set; }

        /// <summary>回退白色像素写入的槽位索引（默认 0）。</summary>
        public int PrimaryTextureSlot { get; set; }

        internal ManagedMaterial() { }

        internal void Reset(MaterialHandle handle, ShaderHandle shader, int techniqueIndex, int passIndex,
            BlendState? blendState, DepthStencilState? depthStencilState, RasterizerState? rasterizerState, SamplerState? sampler0, bool fallbackToMagicPixel)
        {
            lock (_paramLock)
            {
                Handle = handle;
                Shader = shader;
                TechniqueIndex = techniqueIndex;
                PassIndex = passIndex;
                BlendState = blendState ?? BlendState.AlphaBlend;
                DepthStencilState = depthStencilState ?? DepthStencilState.None;
                RasterizerState = rasterizerState ?? RasterizerState.CullNone;
                Sampler0 = sampler0 ?? SamplerState.LinearClamp;
                Sampler1 = null;
                Sampler2 = null;
                Sampler3 = null;
                FallbackToMagicPixel = fallbackToMagicPixel;
                PrimaryTextureSlot = 0;

                if (_paramCount > 0)
                {
                    Array.Clear(_params, 0, _paramCount);
                    _paramCount = 0;
                }
            }
        }

        internal void CopyFrom(ManagedMaterial source, MaterialHandle newHandle)
        {
            ParamEntry[]? srcCopy = null;
            int srcCount;
            lock (source._paramLock)
            {
                srcCount = source._paramCount;
                if (srcCount > 0)
                {
                    srcCopy = new ParamEntry[srcCount];
                    Array.Copy(source._params, srcCopy, srcCount);
                }

                Handle = newHandle;
                Shader = source.Shader;
                TechniqueIndex = source.TechniqueIndex;
                PassIndex = source.PassIndex;
                BlendState = source.BlendState;
                DepthStencilState = source.DepthStencilState;
                RasterizerState = source.RasterizerState;
                Sampler0 = source.Sampler0;
                Sampler1 = source.Sampler1;
                Sampler2 = source.Sampler2;
                Sampler3 = source.Sampler3;
                FallbackToMagicPixel = source.FallbackToMagicPixel;
                PrimaryTextureSlot = source.PrimaryTextureSlot;
            }

            lock (_paramLock)
            {
                if (srcCount > 0 && srcCopy is not null)
                {
                    if (_params.Length < srcCount)
                        _params = new ParamEntry[srcCount];

                    Array.Copy(srcCopy, _params, srcCount);
                }

                if (_paramCount > srcCount)
                    Array.Clear(_params, srcCount, _paramCount - srcCount);

                _paramCount = srcCount;
            }
        }

        // ==================== 槽位化强类型参数写入（对应生成代码中的 Slot_xxx 常量，线程安全） ====================

        public ManagedMaterial SetBool(int slot, bool value)
        {
            lock (_paramLock)
            {
                ref ParamEntry entry = ref GetOrAddSlotLocked(slot);
                entry.Kind = ParamKind.Bool;
                entry.IntValue = value ? 1 : 0;
                entry.RefValue = null;
            }
            return this;
        }

        public ManagedMaterial SetInt(int slot, int value)
        {
            lock (_paramLock)
            {
                ref ParamEntry entry = ref GetOrAddSlotLocked(slot);
                entry.Kind = ParamKind.Int;
                entry.IntValue = value;
                entry.RefValue = null;
            }
            return this;
        }

        public ManagedMaterial SetFloat(int slot, float value)
        {
            lock (_paramLock)
            {
                ref ParamEntry entry = ref GetOrAddSlotLocked(slot);
                entry.Kind = ParamKind.Float;
                entry.Numeric = new Vector4(value, 0f, 0f, 0f);
                entry.RefValue = null;
            }
            return this;
        }

        public ManagedMaterial SetFloatArray(int slot, float[] values)
        {
            lock (_paramLock)
            {
                ref ParamEntry entry = ref GetOrAddSlotLocked(slot);
                entry.Kind = ParamKind.FloatArray;
                entry.RefValue = values;
            }
            return this;
        }

        public ManagedMaterial SetVector2(int slot, Vector2 value)
        {
            lock (_paramLock)
            {
                ref ParamEntry entry = ref GetOrAddSlotLocked(slot);
                entry.Kind = ParamKind.Vector2;
                entry.Numeric = new Vector4(value.X, value.Y, 0f, 0f);
                entry.RefValue = null;
            }
            return this;
        }

        public ManagedMaterial SetVector2Array(int slot, Vector2[] values)
        {
            lock (_paramLock)
            {
                ref ParamEntry entry = ref GetOrAddSlotLocked(slot);
                entry.Kind = ParamKind.Vector2Array;
                entry.RefValue = values;
            }
            return this;
        }

        public ManagedMaterial SetVector3(int slot, Vector3 value)
        {
            lock (_paramLock)
            {
                ref ParamEntry entry = ref GetOrAddSlotLocked(slot);
                entry.Kind = ParamKind.Vector3;
                entry.Numeric = new Vector4(value.X, value.Y, value.Z, 0f);
                entry.RefValue = null;
            }
            return this;
        }

        public ManagedMaterial SetVector3Array(int slot, Vector3[] values)
        {
            lock (_paramLock)
            {
                ref ParamEntry entry = ref GetOrAddSlotLocked(slot);
                entry.Kind = ParamKind.Vector3Array;
                entry.RefValue = values;
            }
            return this;
        }

        public ManagedMaterial SetVector4(int slot, Vector4 value)
        {
            lock (_paramLock)
            {
                ref ParamEntry entry = ref GetOrAddSlotLocked(slot);
                entry.Kind = ParamKind.Vector4;
                entry.Numeric = value;
                entry.RefValue = null;
            }
            return this;
        }

        public ManagedMaterial SetVector4Array(int slot, Vector4[] values)
        {
            lock (_paramLock)
            {
                ref ParamEntry entry = ref GetOrAddSlotLocked(slot);
                entry.Kind = ParamKind.Vector4Array;
                entry.RefValue = values;
            }
            return this;
        }

        public ManagedMaterial SetColor(int slot, Color value)
            => SetVector4(slot, value.ToVector4());

        public ManagedMaterial SetMatrix(int slot, in Matrix value)
        {
            lock (_paramLock)
            {
                ref ParamEntry entry = ref GetOrAddSlotLocked(slot);
                entry.Kind = ParamKind.Matrix;
                entry.MatrixValue = value;
                entry.RefValue = null;
            }
            return this;
        }

        public ManagedMaterial SetMatrixArray(int slot, Matrix[] values)
        {
            lock (_paramLock)
            {
                ref ParamEntry entry = ref GetOrAddSlotLocked(slot);
                entry.Kind = ParamKind.MatrixArray;
                entry.RefValue = values;
            }
            return this;
        }

        public ManagedMaterial SetTexture(int slot, Texture? texture)
        {
            lock (_paramLock)
            {
                ref ParamEntry entry = ref GetOrAddSlotLocked(slot);
                entry.Kind = ParamKind.Texture;
                entry.RefValue = texture;
            }
            return this;
        }

        /// <summary>将材质管线状态与参数应用到 GPU 并激活对应 Pass。</summary>
        internal bool ApplyToGpu(GraphicsDevice gd, in RenderFrameContext context, bool applyPipelineStates, bool applyUniforms, bool hasWorldMatrix, in Matrix worldMatrix)
        {
            if (!RenderManager.Shaders.TryGet(Shader, out ManagedShader? shader) || !shader.EnsureLoaded())
                return false;

            if (applyPipelineStates)
            {
                gd.BlendState = BlendState;
                gd.DepthStencilState = DepthStencilState;
                gd.RasterizerState = RasterizerState;
                gd.SamplerStates[0] = Sampler0;
                // 显式归位 Sampler1..3，防止上一材质的多槽采样器状态串台
                gd.SamplerStates[1] = Sampler1 ?? SamplerState.LinearClamp;
                gd.SamplerStates[2] = Sampler2 ?? SamplerState.LinearClamp;
                gd.SamplerStates[3] = Sampler3 ?? SamplerState.LinearClamp;
            }

            if (applyUniforms)
            {
                bool primaryTextureBound = false;
                lock (_paramLock)
                {
                    int count = _paramCount;
                    ParamEntry[] entries = _params;

                    for (int i = 0; i < count; i++)
                    {
                        ref readonly ParamEntry e = ref entries[i];
                        switch (e.Kind)
                        {
                            case ParamKind.Bool:
                                shader.SetBool(e.Slot, e.IntValue != 0);
                                break;
                            case ParamKind.Int:
                                shader.SetInt(e.Slot, e.IntValue);
                                break;
                            case ParamKind.Float:
                                shader.SetFloat(e.Slot, e.Numeric.X);
                                break;
                            case ParamKind.FloatArray:
                                if (e.RefValue is float[] fa) shader.SetFloatArray(e.Slot, fa);
                                break;
                            case ParamKind.Vector2:
                                shader.SetVector2(e.Slot, new Vector2(e.Numeric.X, e.Numeric.Y));
                                break;
                            case ParamKind.Vector2Array:
                                if (e.RefValue is Vector2[] v2a) shader.SetVector2Array(e.Slot, v2a);
                                break;
                            case ParamKind.Vector3:
                                shader.SetVector3(e.Slot, new Vector3(e.Numeric.X, e.Numeric.Y, e.Numeric.Z));
                                break;
                            case ParamKind.Vector3Array:
                                if (e.RefValue is Vector3[] v3a) shader.SetVector3Array(e.Slot, v3a);
                                break;
                            case ParamKind.Vector4:
                                shader.SetVector4(e.Slot, e.Numeric);
                                break;
                            case ParamKind.Vector4Array:
                                if (e.RefValue is Vector4[] v4a) shader.SetVector4Array(e.Slot, v4a);
                                break;
                            case ParamKind.Matrix:
                                shader.SetMatrix(e.Slot, in e.MatrixValue);
                                break;
                            case ParamKind.MatrixArray:
                                if (e.RefValue is Matrix[] ma) shader.SetMatrixArray(e.Slot, ma);
                                break;
                            case ParamKind.Texture:
                                var tex = e.RefValue as Texture;
                                if (e.Slot == PrimaryTextureSlot && tex is not null)
                                    primaryTextureBound = true;
                                shader.SetTexture(e.Slot, tex);
                                break;
                        }
                    }
                }

                if (FallbackToMagicPixel && !primaryTextureBound && shader.ParameterSlotCount > PrimaryTextureSlot)
                {
                    Texture2D? magicPixel = TextureAssets.MagicPixel?.Value;
                    if (magicPixel is not null && !magicPixel.IsDisposed)
                        shader.SetTexture(PrimaryTextureSlot, magicPixel);
                }
            }

            if (hasWorldMatrix)
            {
                Matrix wvp = worldMatrix * context.WorldTransformMatrix;
                shader.SetTransformMatrix(in wvp);
            }

            return shader.TryApply(in context, TechniqueIndex, PassIndex);
        }

        private ref ParamEntry GetOrAddSlotLocked(int slot)
        {
            for (int i = 0; i < _paramCount; i++)
            {
                if (_params[i].Slot == slot)
                    return ref _params[i];
            }

            if (_paramCount >= _params.Length)
            {
                int newCap = _params.Length == 0 ? 4 : _params.Length * 2;
                Array.Resize(ref _params, newCap);
            }

            int idx = _paramCount++;
            _params[idx] = new ParamEntry { Slot = slot };
            return ref _params[idx];
        }
    }
}
