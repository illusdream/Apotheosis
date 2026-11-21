#nullable enable

using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Terraria;

namespace Apotheosis.Runtime.Foundation.Transform.Resolvers
{
    /// <summary>
    /// 原版 <see cref="NPC"/> 的纯空间变换解析器。
    /// <para>
    /// 映射字段：<c>Center</c>、<c>rotation</c>、<c>scale</c>、<c>spriteDirection</c> / <c>direction</c>（↔ <c>FlipX</c>）。
    /// </para>
    /// </summary>
    public class NpcTransformResolver : TransformResolver<NPC>
    {
        /// <summary>默认无状态共享实例。</summary>
        public static NpcTransformResolver Default { get; } = new();

        public override void ReadFrom(TransformNode node, NPC target, TransformSyncFlags flags = TransformSyncFlags.All)
        {
            if ((flags & TransformSyncFlags.Position) != 0)
            {
                node.WorldPosition = new Vector3(target.Center, node.WorldPosition.Z);
            }

            if ((flags & TransformSyncFlags.Rotation) != 0)
            {
                node.WorldRotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, target.rotation);
            }

            if ((flags & TransformSyncFlags.Scale) != 0)
            {
                float mag = MathF.Abs(target.scale);
                Vector3 parentScale = node.Parent?.WorldScale ?? Vector3.One;
                float localMagX = MathF.Abs(parentScale.X) > 1e-6f ? mag / MathF.Abs(parentScale.X) : mag;
                float localMagY = MathF.Abs(parentScale.Y) > 1e-6f ? mag / MathF.Abs(parentScale.Y) : mag;
                Vector3 current = node.LocalScale;
                node.LocalScale = new Vector3(
                    current.X < 0f ? -localMagX : localMagX,
                    current.Y < 0f ? -localMagY : localMagY,
                    current.Z);
            }

            if ((flags & TransformSyncFlags.Facing) != 0)
            {
                int dir = target.spriteDirection != 0 ? target.spriteDirection : target.direction;
                if (dir != 0)
                {
                    Vector3 parentSign = node.Parent?.WorldScaleSign ?? Vector3.One;
                    node.SetFlipX((dir < 0) ^ (parentSign.X < 0f));
                    node.SetFlipY(parentSign.Y < 0f);
                }
            }
        }

        public override void WriteTo(TransformNode node, NPC target, TransformSyncFlags flags = TransformSyncFlags.All)
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
