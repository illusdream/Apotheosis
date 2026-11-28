#nullable enable

using System;
using System.Runtime.CompilerServices;

namespace Apotheosis.Runtime.Utils
{
    /// <summary>通用缓动曲线类型。</summary>
    public enum EaseType : byte
    {
        Linear = 0,
        InQuad = 1,
        OutQuad = 2,
        InOutQuad = 3,
        InCubic = 4,
        OutCubic = 5,
        InOutCubic = 6,
        SmoothStep = 7,
        /// <summary>钟形曲线（<c>sin(π·t)</c>，起点与终点归零、中点为 1，适合淡入淡出）。</summary>
        Bell = 8,
    }

    /// <summary>通用无状态数学与缓动曲线工具。</summary>
    public static class MathUtils
    {
        /// <summary>按指定缓动曲线计算归一化进度 <paramref name="t"/>（0..1）对应的插值系数。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float EvaluateEase(EaseType ease, float t)
        {
            // 显式拦截 NaN 并钳位至 [0, 1]（Math.Clamp 会透传 NaN）
            t = !(t > 0f) ? 0f : (t >= 1f ? 1f : t);
            switch (ease)
            {
                case EaseType.InQuad:
                    return t * t;

                case EaseType.OutQuad:
                {
                    float inv = 1f - t;
                    return 1f - inv * inv;
                }

                case EaseType.InOutQuad:
                {
                    if (t < 0.5f)
                        return 2f * t * t;
                    float u = -2f * t + 2f;
                    return 1f - u * u * 0.5f;
                }

                case EaseType.InCubic:
                    return t * t * t;

                case EaseType.OutCubic:
                {
                    float inv = 1f - t;
                    return 1f - inv * inv * inv;
                }

                case EaseType.InOutCubic:
                {
                    if (t < 0.5f)
                        return 4f * t * t * t;
                    float u = -2f * t + 2f;
                    return 1f - u * u * u * 0.5f;
                }

                case EaseType.SmoothStep:
                    return t * t * (3f - 2f * t);

                case EaseType.Bell:
                    // 端点精确归零，避免 MathF.Sin(MathF.PI) 浮点残余产生微小负值
                    return t <= 0f || t >= 1f ? 0f : MathF.Sin(t * MathF.PI);

                default:
                    return t;
            }
        }
    }
}
