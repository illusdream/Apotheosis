#nullable enable

using Microsoft.Xna.Framework.Graphics;
using Terraria;

namespace Apotheosis.Runtime.Foundation.Transform.Resolvers
{
    /// <summary>
    /// 原版 <see cref="WorldItem"/>（掉落于世界中的物品实体）的纯空间变换解析器。
    /// <para>
    /// 映射字段：<c>Center</c>、<c>scale</c>、<c>direction</c>（↔ <c>FlipX</c>）。
    /// </para>
    /// </summary>
    public class WorldItemTransformResolver : TransformResolver<WorldItem>
    {
        /// <summary>默认无状态共享实例。</summary>
        public static WorldItemTransformResolver Default { get; } = new();

        public override void ReadFrom(TransformNode node, WorldItem target, TransformSyncFlags flags = TransformSyncFlags.All)
        {
            bool setFlip = (flags & TransformSyncFlags.Facing) != 0 && target.direction != 0;
            node.ApplyWorld2DSync((flags & TransformSyncFlags.Position) != 0, target.Center,
                setRotation: false, worldRotationZ: 0f,
                (flags & TransformSyncFlags.Scale) != 0, target.scale,
                setFlip, flipX: target.direction < 0, flipY: false);
        }

        public override void WriteTo(TransformNode node, WorldItem target, TransformSyncFlags flags = TransformSyncFlags.All)
        {
            SpriteTransform2D sprite = node.ToSprite2D(preferHorizontalFlip: true);

            if ((flags & TransformSyncFlags.Position) != 0)
            {
                target.Center = sprite.Position;
            }

            if ((flags & TransformSyncFlags.Scale) != 0)
            {
                target.inner.scale = sprite.UniformScale;
            }

            if ((flags & TransformSyncFlags.Facing) != 0)
            {
                target.direction = (sprite.Effects & SpriteEffects.FlipHorizontally) != 0 ? -1 : 1;
            }
        }
    }
}
