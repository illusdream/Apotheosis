#nullable enable

using System;
using System.Runtime.CompilerServices;
using Apotheosis.Runtime.Particles.Components;
using Apotheosis.Runtime.Particles.Core;
using Friflo.Engine.ECS;
using Microsoft.Xna.Framework;
using Terraria;

namespace Apotheosis.Runtime.Particles.Systems
{
    /// <summary>Terraria 物块地形碰撞模拟系统（支持实心块、半砖、普通/半格平台、高速子步扫掠、真墙顶黏附与局部空间子系统）。</summary>
    public sealed class ParticleTileCollisionSystem : IParticleSimSystem
    {
        private const float TileSize = 16f;
        private const float InvTileSize = 1f / 16f;
        private const float SurfaceEpsilon = 0.05f;
        private const float SubstepMaxDisplacement = 7f;
        private const int MaxSubsteps = 8;
        private const float MinBounceCountSpeed = 30f;

        private const byte StuckRight = 1 << 0;
        private const byte StuckLeft = 1 << 1;
        private const byte StuckDown = 1 << 2;
        private const byte StuckUp = 1 << 3;
        private const byte StuckInside = 1 << 4;

        private static readonly ParticleQuerySlot QuerySlot = ParticleQuerySlot.Allocate();

        public void Execute(ParticleStoreSlot slot, in ParticleSimContext ctx)
        {
            float dt = ctx.DeltaTime;
            if (ctx.UpdateDomain != ParticleUpdateDomain.World || !float.IsFinite(dt) || !(dt > 0f) || slot.EntityCount == 0)
                return;

            // 世界未就绪或卸载过渡期安全早退（WorldUnload 已由同步栅栏保护，此处叠加边界防线）
            Tilemap tilemap = Main.tile;
            int maxX = Math.Min(Main.maxTilesX, tilemap.Width);
            int maxY = Math.Min(Main.maxTilesY, tilemap.Height);
            bool[]? tileSolid = Main.tileSolid;
            bool[]? tileSolidTop = Main.tileSolidTop;
            if (maxX <= 0 || maxY <= 0 || tileSolid is null || tileSolidTop is null)
                return;

            bool isLocal = ctx.IsLocalSpace;
            if (isLocal)
            {
                float det = ctx.OriginWorldMatrix.Determinant();
                if (!float.IsFinite(det) || !(MathF.Abs(det) > 1e-12f))
                    return;
            }

            var query = slot.GetOrCreateQuery(QuerySlot, static s => s.Query<ParticleSpatial, ParticleTileCollision>());
            CommandBuffer cmd = slot.CommandBuffer;
            bool anyDeleted = false;

            foreach (var (spatialChunk, collisionChunk, entities) in query.Chunks)
            {
                Span<ParticleSpatial> spatials = spatialChunk.Span;
                Span<ParticleTileCollision> collisions = collisionChunk.Span;
                ReadOnlySpan<int> ids = entities.Ids;

                for (int i = 0; i < spatials.Length; i++)
                {
                    ref ParticleSpatial spatial = ref spatials[i];
                    ref ParticleTileCollision col = ref collisions[i];

                    Vector3 worldPos = isLocal ? Vector3.Transform(spatial.Position, ctx.OriginWorldMatrix) : spatial.Position;
                    Vector3 worldVel = isLocal ? Vector3.TransformNormal(spatial.Velocity, ctx.OriginWorldMatrix) : spatial.Velocity;
                    if (!IsFinite(worldPos) || !IsFinite(worldVel))
                        continue;

                    Vector3 prevPos = worldPos - worldVel * dt;
                    if (!IsFinite(prevPos))
                        continue;

                    float radius = float.IsFinite(col.Radius) && col.Radius > 0f ? MathF.Min(col.Radius, 7.5f) : 0f;

                    // 已处于 Stick 黏附态时检查支撑物块是否仍在：仍在则回锁表面并抵消本帧重力/力场，已被挖除则立即解除黏附恢复自由运动
                    if (col.Mode == ParticleTileCollisionMode.Stick && col.StuckMask != 0)
                    {
                        if (IsStillSupported(tilemap, tileSolid, tileSolidTop, maxX, maxY, prevPos.X, prevPos.Y, radius, col.StuckMask))
                        {
                            WriteBackSpatial(ref spatial, prevPos, Vector3.Zero, isLocal, in ctx.InverseOriginMatrix);
                            continue;
                        }

                        col.StuckMask = 0;
                    }

                    // 高速粒子（>7px/步，覆盖半砖与单格墙）启用上限 8 段零 GC 子步扫掠，低速粒子直走单步快路径
                    float totalDx = worldPos.X - prevPos.X;
                    float totalDy = worldPos.Y - prevPos.Y;
                    float maxDisp = MathF.Max(MathF.Abs(totalDx), MathF.Abs(totalDy));
                    int steps = maxDisp > SubstepMaxDisplacement ? Math.Min(MaxSubsteps, (int)MathF.Ceiling(maxDisp / SubstepMaxDisplacement)) : 1;

                    float stepDx = totalDx / steps;
                    float stepDy = totalDy / steps;
                    float curX = prevPos.X;
                    float curY = prevPos.Y;
                    float resolvedX = worldPos.X;
                    float resolvedY = worldPos.Y;
                    bool hitX = false;
                    bool hitY = false;
                    byte hitMask = 0;

                    for (int s = 0; s < steps; s++)
                    {
                        float nextX = s == steps - 1 ? worldPos.X : curX + stepDx;
                        float nextY = s == steps - 1 ? worldPos.Y : curY + stepDy;
                        resolvedX = nextX;
                        resolvedY = nextY;

                        if (MathF.Abs(stepDx) > 1e-5f)
                        {
                            float probeX = stepDx > 0f ? nextX + radius : nextX - radius;
                            if (HitSolidAtPoint(tilemap, tileSolid, tileSolidTop, maxX, maxY, probeX, curY, out int hitTx))
                            {
                                hitX = true;
                                hitMask |= stepDx > 0f ? StuckRight : StuckLeft;
                                resolvedX = stepDx > 0f
                                    ? hitTx * TileSize - radius - SurfaceEpsilon
                                    : (hitTx + 1) * TileSize + radius + SurfaceEpsilon;
                            }
                        }

                        if (MathF.Abs(stepDy) > 1e-5f)
                        {
                            float probeY = stepDy > 0f ? nextY + radius : nextY - radius;
                            bool fallingDown = stepDy > 0f;
                            if (HitVerticalAtPoint(tilemap, tileSolid, tileSolidTop, maxX, maxY, resolvedX, probeY, curY + radius, fallingDown, out int hitTy, out float surfaceTopY))
                            {
                                hitY = true;
                                hitMask |= fallingDown ? StuckDown : StuckUp;
                                resolvedY = fallingDown
                                    ? surfaceTopY - radius - SurfaceEpsilon
                                    : (hitTy + 1) * TileSize + radius + SurfaceEpsilon;
                            }
                        }
                        else if (!hitX && s == 0 && HitSolidAtPoint(tilemap, tileSolid, tileSolidTop, maxX, maxY, resolvedX, resolvedY, out _))
                        {
                            hitY = true;
                            hitMask |= StuckInside;
                        }

                        if (hitX || hitY)
                            break;

                        curX = nextX;
                        curY = nextY;
                    }

                    if (!hitX && !hitY)
                        continue;

                    switch (col.Mode)
                    {
                        case ParticleTileCollisionMode.Destroy:
                            cmd.DeleteEntity(ids[i]);
                            anyDeleted = true;
                            continue;

                        case ParticleTileCollisionMode.Stick:
                            worldPos.X = resolvedX;
                            worldPos.Y = resolvedY;
                            worldVel = Vector3.Zero;
                            col.StuckMask = hitMask != 0 ? hitMask : StuckInside;
                            break;

                        default: // Bounce
                        {
                            float restitution = float.IsFinite(col.Restitution) ? Math.Clamp(col.Restitution, 0f, 1f) : 0.5f;
                            float friction = float.IsFinite(col.Friction) ? Math.Clamp(col.Friction, 0f, 1f) : 0.2f;
                            float tangentKeep = 1f - friction;
                            bool significantBounce = (hitX && MathF.Abs(worldVel.X) > MinBounceCountSpeed)
                                || (hitY && MathF.Abs(worldVel.Y) > MinBounceCountSpeed);

                            worldPos.X = resolvedX;
                            worldPos.Y = resolvedY;

                            if (hitX)
                            {
                                worldVel.X = MathF.Abs(worldVel.X) > MinBounceCountSpeed ? -worldVel.X * restitution : 0f;
                                worldVel.Y *= tangentKeep;
                                worldVel.Z *= tangentKeep;
                            }

                            if (hitY)
                            {
                                worldVel.Y = MathF.Abs(worldVel.Y) > MinBounceCountSpeed ? -worldVel.Y * restitution : 0f;
                                worldVel.X *= tangentKeep;
                                worldVel.Z *= tangentKeep;
                            }

                            if (significantBounce)
                            {
                                if (col.BounceCount < byte.MaxValue)
                                    col.BounceCount++;

                                if (col.MaxBounces > 0 && col.BounceCount >= col.MaxBounces)
                                {
                                    cmd.DeleteEntity(ids[i]);
                                    anyDeleted = true;
                                    continue;
                                }
                            }

                            break;
                        }
                    }

                    WriteBackSpatial(ref spatial, worldPos, worldVel, isLocal, in ctx.InverseOriginMatrix);
                }
            }

            if (anyDeleted)
                slot.PlaybackCommandBuffer();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void WriteBackSpatial(ref ParticleSpatial spatial, Vector3 worldPos, Vector3 worldVel, bool isLocal, in Matrix inverseOrigin)
        {
            if (isLocal)
            {
                spatial.Position = Vector3.Transform(worldPos, inverseOrigin);
                spatial.Velocity = Vector3.TransformNormal(worldVel, inverseOrigin);
            }
            else
            {
                spatial.Position = worldPos;
                spatial.Velocity = worldVel;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool IsStillSupported(Tilemap tilemap, bool[] tileSolid, bool[] tileSolidTop, int maxX, int maxY,
            float x, float y, float radius, byte stuckMask)
        {
            const float probePad = 1.5f;
            if ((stuckMask & StuckRight) != 0 && HitSolidAtPoint(tilemap, tileSolid, tileSolidTop, maxX, maxY, x + radius + probePad, y, out _))
                return true;
            if ((stuckMask & StuckLeft) != 0 && HitSolidAtPoint(tilemap, tileSolid, tileSolidTop, maxX, maxY, x - radius - probePad, y, out _))
                return true;
            if ((stuckMask & StuckDown) != 0 && HitVerticalAtPoint(tilemap, tileSolid, tileSolidTop, maxX, maxY, x, y + radius + probePad, y + radius, fallingDown: true, out _, out _))
                return true;
            if ((stuckMask & StuckUp) != 0 && HitVerticalAtPoint(tilemap, tileSolid, tileSolidTop, maxX, maxY, x, y - radius - probePad, y + radius, fallingDown: false, out _, out _))
                return true;
            if ((stuckMask & StuckInside) != 0 && HitSolidAtPoint(tilemap, tileSolid, tileSolidTop, maxX, maxY, x, y, out _))
                return true;
            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool HitSolidAtPoint(Tilemap tilemap, bool[] tileSolid, bool[] tileSolidTop, int maxX, int maxY, float worldX, float worldY, out int hitTx)
        {
            int tx = (int)MathF.Floor(worldX * InvTileSize);
            int ty = (int)MathF.Floor(worldY * InvTileSize);
            hitTx = tx;

            if ((uint)tx >= (uint)maxX || (uint)ty >= (uint)maxY)
                return false;

            Tile tile = tilemap[tx, ty];
            if (!tile.HasUnactuatedTile)
                return false;

            ushort tileType = tile.TileType;
            if ((uint)tileType >= (uint)tileSolid.Length || !tileSolid[tileType])
                return false;

            // 平台（tileSolidTop）不阻挡水平运动
            if ((uint)tileType < (uint)tileSolidTop.Length && tileSolidTop[tileType])
                return false;

            // 半砖仅占据单元格下半部分 [ty*16+8, ty*16+16]
            if (tile.IsHalfBlock && worldY < ty * TileSize + 8f)
                return false;

            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool HitVerticalAtPoint(Tilemap tilemap, bool[] tileSolid, bool[] tileSolidTop, int maxX, int maxY,
            float worldX, float probeY, float prevBottomY, bool fallingDown, out int hitTy, out float surfaceTopY)
        {
            int tx = (int)MathF.Floor(worldX * InvTileSize);
            int ty = (int)MathF.Floor(probeY * InvTileSize);
            hitTy = ty;
            surfaceTopY = ty * TileSize;

            if ((uint)tx >= (uint)maxX || (uint)ty >= (uint)maxY)
                return false;

            Tile tile = tilemap[tx, ty];
            if (!tile.HasUnactuatedTile)
                return false;

            ushort tileType = tile.TileType;
            if ((uint)tileType >= (uint)tileSolid.Length || !tileSolid[tileType])
                return false;

            bool isPlatform = (uint)tileType < (uint)tileSolidTop.Length && tileSolidTop[tileType];
            if (isPlatform)
            {
                if (!fallingDown)
                    return false;

                // 兼容被锤击成下半格的平台（IsHalfBlock 顶面位于 ty*16 + 8）
                float top = tile.IsHalfBlock ? ty * TileSize + 8f : ty * TileSize;
                if (probeY < top || prevBottomY > top + 6f)
                    return false;

                surfaceTopY = top;
                return true;
            }

            if (tile.IsHalfBlock)
            {
                float halfTop = ty * TileSize + 8f;
                if (probeY < halfTop)
                    return false;

                surfaceTopY = halfTop;
                return true;
            }

            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool IsFinite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
    }
}
