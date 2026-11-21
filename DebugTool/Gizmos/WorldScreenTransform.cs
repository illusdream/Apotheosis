#if DEBUG
#nullable enable

using System;
using Microsoft.Xna.Framework;
using Terraria;

namespace Apotheosis.DebugTool.Gizmos
{
    /// <summary>
    /// 泰拉瑞亚世界坐标与 FNA BackBuffer 像素坐标的双向变换快照。
    /// <para>
    /// 完整兼容 <see cref="Main.GameViewMatrix"/> 的画面缩放（Zoom）与重力药剂上下翻转（FlipVertically），
    /// 并同步提供 <see cref="ScreenToWorld"/> 逆变换，供未来独立 Handles 基建复用。
    /// </para>
    /// </summary>
    internal readonly struct WorldScreenTransform
    {
        private const float SingularEpsilon = 1e-8f;

        public readonly Vector2 ScreenPosition;
        public readonly Matrix ViewMatrix;
        public readonly Matrix InverseViewMatrix;
        public readonly float ScaleX;
        public readonly float ScaleY;
        public readonly int BackBufferWidth;
        public readonly int BackBufferHeight;
        public readonly float PixelsPerWorldUnit;
        public readonly bool IsYFlipped;

        /// <summary>最近一次捕获的视口变换快照（供未来独立的 Handles 基建直接复用）。</summary>
        public static WorldScreenTransform Current { get; private set; }
        public static bool HasCurrent { get; private set; }

        private WorldScreenTransform(
            Vector2 screenPosition,
            Matrix viewMatrix,
            Matrix inverseViewMatrix,
            float scaleX,
            float scaleY,
            int backBufferWidth,
            int backBufferHeight,
            float pixelsPerWorldUnit,
            bool isYFlipped)
        {
            ScreenPosition = screenPosition;
            ViewMatrix = viewMatrix;
            InverseViewMatrix = inverseViewMatrix;
            ScaleX = scaleX;
            ScaleY = scaleY;
            BackBufferWidth = backBufferWidth;
            BackBufferHeight = backBufferHeight;
            PixelsPerWorldUnit = pixelsPerWorldUnit;
            IsYFlipped = isYFlipped;
        }

        /// <summary>捕获当前帧泰拉瑞亚视口与 BackBuffer 的坐标映射快照。</summary>
        public static WorldScreenTransform Capture(int backBufferWidth, int backBufferHeight)
        {
            Matrix view = Main.GameViewMatrix.TransformationMatrix;
            Matrix invView = MathF.Abs(view.Determinant()) > SingularEpsilon
                ? Matrix.Invert(view)
                : Matrix.Identity;

            float scaleX = Main.screenWidth > 0 ? (float)backBufferWidth / Main.screenWidth : 1f;
            float scaleY = Main.screenHeight > 0 ? (float)backBufferHeight / Main.screenHeight : 1f;

            // 估算世界空间 1 像素在当前 BackBuffer 中占据的屏幕像素数（用于自适应圆弧细分与可见性估算）
            float axisX = new Vector2(view.M11 * scaleX, view.M12 * scaleY).Length();
            float axisY = new Vector2(view.M21 * scaleX, view.M22 * scaleY).Length();
            float ppu = MathF.Max(0.01f, MathF.Max(axisX, axisY));

            bool yFlipped = view.M22 < 0f;

            var snapshot = new WorldScreenTransform(
                Main.screenPosition,
                view,
                invView,
                scaleX,
                scaleY,
                backBufferWidth,
                backBufferHeight,
                ppu,
                yFlipped);

            Current = snapshot;
            HasCurrent = true;
            return snapshot;
        }

        /// <summary>模组卸载时清空缓存快照。</summary>
        public static void ResetCurrent()
        {
            Current = default;
            HasCurrent = false;
        }

        /// <summary>将世界坐标映射为 FNA BackBuffer 像素坐标（与 ImGui DrawList 坐标系一致）。</summary>
        public System.Numerics.Vector2 WorldToScreen(Vector2 worldPos)
        {
            Vector2 local = worldPos - ScreenPosition;
            Vector2 viewPos = Vector2.Transform(local, ViewMatrix);
            return new System.Numerics.Vector2(viewPos.X * ScaleX, viewPos.Y * ScaleY);
        }

        /// <summary>将 FNA BackBuffer 像素坐标反算回泰拉瑞亚世界坐标。</summary>
        public Vector2 ScreenToWorld(System.Numerics.Vector2 pixelPos)
        {
            float invScaleX = MathF.Abs(ScaleX) > SingularEpsilon ? 1f / ScaleX : 1f;
            float invScaleY = MathF.Abs(ScaleY) > SingularEpsilon ? 1f / ScaleY : 1f;
            Vector2 viewPos = new(pixelPos.X * invScaleX, pixelPos.Y * invScaleY);
            return Vector2.Transform(viewPos, InverseViewMatrix) + ScreenPosition;
        }

        /// <summary>判断单个屏幕点是否位于带边距的视口内。</summary>
        public bool IsPointVisible(System.Numerics.Vector2 p, float margin = 48f)
            => p.X >= -margin && p.X <= BackBufferWidth + margin &&
               p.Y >= -margin && p.Y <= BackBufferHeight + margin;

        /// <summary>判断屏幕轴对齐包围盒是否与带边距视口相交。</summary>
        public bool IsBoundsVisible(System.Numerics.Vector2 min, System.Numerics.Vector2 max, float margin = 32f)
            => max.X >= -margin && min.X <= BackBufferWidth + margin &&
               max.Y >= -margin && min.Y <= BackBufferHeight + margin;

        /// <summary>判断线段屏幕包围盒是否可能进入视口。</summary>
        public bool IsSegmentVisible(System.Numerics.Vector2 a, System.Numerics.Vector2 b, float margin = 32f)
        {
            float minX = MathF.Min(a.X, b.X);
            float maxX = MathF.Max(a.X, b.X);
            float minY = MathF.Min(a.Y, b.Y);
            float maxY = MathF.Max(a.Y, b.Y);
            return maxX >= -margin && minX <= BackBufferWidth + margin &&
                   maxY >= -margin && minY <= BackBufferHeight + margin;
        }

        /// <summary>判断圆（世界半径）在屏幕上是否可能进入视口。</summary>
        public bool IsCircleVisible(System.Numerics.Vector2 screenCenter, float worldRadius, float margin = 16f)
        {
            float screenRadius = MathF.Abs(worldRadius) * PixelsPerWorldUnit;
            float pad = screenRadius + margin;
            return screenCenter.X >= -pad && screenCenter.X <= BackBufferWidth + pad &&
                   screenCenter.Y >= -pad && screenCenter.Y <= BackBufferHeight + pad;
        }
    }
}
#endif
