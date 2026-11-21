#nullable enable

using Microsoft.Xna.Framework;
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
            if ((flags & TransformSyncFlags.Position) != 0)
            {
                node.WorldPosition = new Vector3(target.Center, node.WorldPosition.Z);
            }

            if ((flags & TransformSyncFlags.Rotation) != 0)
            {
                node.WorldRotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, target.fullRotation);
            }

            if ((flags & TransformSyncFlags.Facing) != 0)
            {
                Vector3 parentSign = node.Parent?.WorldScaleSign ?? Vector3.One;
                node.SetFlipX((target.direction < 0) ^ (parentSign.X < 0f));
                node.SetFlipY((target.gravDir < 0f) ^ (parentSign.Y < 0f));
            }
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
