#nullable enable

using Microsoft.Xna.Framework.Graphics;
using Terraria;

namespace Apotheosis.Runtime.Foundation.Transform.Resolvers
{
    /// <summary>
    /// 原版 <see cref="Projectile"/> 的纯空间变换解析器。
    /// <para>
    /// 映射字段：<c>Center</c>、<c>rotation</c>、<c>scale</c>、<c>spriteDirection</c> / <c>direction</c>（↔ <c>FlipX</c>）。
    /// </para>
    /// </summary>
    public class ProjectileTransformResolver : TransformResolver<Projectile>
    {
        /// <summary>默认无状态共享实例。</summary>
        public static ProjectileTransformResolver Default { get; } = new();

        public override void ReadFrom(TransformNode node, Projectile target, TransformSyncFlags flags = TransformSyncFlags.All)
        {
            int dir = target.spriteDirection != 0 ? target.spriteDirection : target.direction;
            bool setFlip = (flags & TransformSyncFlags.Facing) != 0 && dir != 0;
            node.ApplyWorld2DSync((flags & TransformSyncFlags.Position) != 0, target.Center,
                (flags & TransformSyncFlags.Rotation) != 0, target.rotation,
                (flags & TransformSyncFlags.Scale) != 0, target.scale,
                setFlip, flipX: dir < 0, flipY: false);
        }

        public override void WriteTo(TransformNode node, Projectile target, TransformSyncFlags flags = TransformSyncFlags.All)
        {
            SpriteTransform2D sprite = node.ToSprite2D(preferHorizontalFlip: true);

            if ((flags & TransformSyncFlags.Position) != 0)
            {
                target.Center = sprite.Position;
            }

            if ((flags & TransformSyncFlags.Rotation) != 0)
            {
                target.rotation = sprite.Rotation;
            }

            if ((flags & TransformSyncFlags.Scale) != 0)
            {
                target.scale = sprite.UniformScale;
            }

            if ((flags & TransformSyncFlags.Facing) != 0)
            {
                int dir = (sprite.Effects & SpriteEffects.FlipHorizontally) != 0 ? -1 : 1;
                target.spriteDirection = dir;
                target.direction = dir;
            }
        }
    }
}
