#nullable enable

using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Apotheosis.Runtime.Foundation.Transform
{
    /// <summary>
    /// 3D 变换投影到 Terraria 2D 渲染空间（<see cref="SpriteBatch"/>）后的结果。
    /// </summary>
    public readonly struct SpriteTransform2D
    {
        /// <summary>世界空间 2D 坐标（对应 3D 位置的 XY 分量）。</summary>
        public readonly Vector2 Position;

        /// <summary>2D 顺时针旋转弧度（与 Terraria 原版 <c>rotation</c> 约定一致）。</summary>
        public readonly float Rotation;

        /// <summary>非负 2D 缩放大小（翻转信息已提取至 <see cref="Effects"/>）。</summary>
        public readonly Vector2 Scale;

        /// <summary>提取后的水平/垂直镜像翻转标记。</summary>
        public readonly SpriteEffects Effects;

        /// <summary>世界空间景深（Z 分量，+Z 指向屏幕内）。</summary>
        public readonly float DepthZ;

        /// <summary>在 XY 屏幕投影平面内是否处于镜像/背面状态（2D 行列式小于 0）。</summary>
        public readonly bool IsBackFacing;

        /// <summary>缩放的标量平均值，适用于仅接受单个 <c>float</c> 缩放参数的原版 API。</summary>
        public float UniformScale => (Scale.X + Scale.Y) * 0.5f;

        public SpriteTransform2D(
            Vector2 position,
            float rotation,
            Vector2 scale,
            SpriteEffects effects,
            float depthZ,
            bool isBackFacing)
        {
            Position = position;
            Rotation = rotation;
            Scale = scale;
            Effects = effects;
            DepthZ = depthZ;
            IsBackFacing = isBackFacing;
        }
    }
}
