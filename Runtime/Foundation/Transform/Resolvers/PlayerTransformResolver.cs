#nullable enable

using Microsoft.Xna.Framework.Graphics;
using Terraria;

namespace Apotheosis.Runtime.Foundation.Transform.Resolvers
{
    /// <summary>
    /// 原版 <see cref="Player"/> 的纯空间变换解析器。
    /// <para>
    /// 映射字段：<c>Center</c>、<c>fullRotation</c>、<c>direction</c>（↔ <c>FlipX</c>）、<c>gravDir</c>（↔ <c>FlipY</c>）。
    /// </para>
    /// </summary>
    public class PlayerTransformResolver : TransformResolver<Player>
    {
        /// <summary>默认无状态共享实例。</summary>
        public static PlayerTransformResolver Default { get; } = new();

        public override void ReadFrom(TransformNode node, Player target, TransformSyncFlags flags = TransformSyncFlags.All)
        {
            node.ApplyWorld2DSync((flags & TransformSyncFlags.Position) != 0, target.Center,
                (flags & TransformSyncFlags.Rotation) != 0, target.fullRotation,
                setUniformScale: false, uniformScale: 1f,
                (flags & TransformSyncFlags.Facing) != 0, flipX: target.direction < 0, flipY: target.gravDir < 0f);
        }

        public override void WriteTo(TransformNode node, Player target, TransformSyncFlags flags = TransformSyncFlags.All)
        {
            SpriteTransform2D sprite = node.ToSprite2D(preferHorizontalFlip: true);

            if ((flags & TransformSyncFlags.Position) != 0)
            {
                target.Center = sprite.Position;
            }

            if ((flags & TransformSyncFlags.Rotation) != 0)
            {
                target.fullRotation = sprite.Rotation;
            }

            if ((flags & TransformSyncFlags.Facing) != 0)
            {
                target.direction = (sprite.Effects & SpriteEffects.FlipHorizontally) != 0 ? -1 : 1;
            }
        }
    }
}
