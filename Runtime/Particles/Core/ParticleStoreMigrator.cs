#nullable enable

using System;
using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using Apotheosis.Runtime.Foundation.Transform;
using Apotheosis.Runtime.Particles.Components;
using Apotheosis.Runtime.Rendering.Targets;
using Friflo.Engine.ECS;
using Microsoft.Xna.Framework;

namespace Apotheosis.Runtime.Particles.Core
{
    /// <summary>跨 <see cref="EntityStore"/> 零 GC 批量迁移器：实现 <see cref="ParticleHostLostPolicy.DetachToGlobal"/> 的 Archetype 整段复制、局部→世界坐标烘焙与元数据保留。</summary>
    internal static class ParticleStoreMigrator
    {
        private delegate void ComponentSliceCopier(Archetype src, Archetype dst, int dstStart, int count);

        private static readonly ConcurrentDictionary<Type, ComponentSliceCopier?> CopierCache = new();
        private static readonly MethodInfo CopySliceGenericDef = typeof(ParticleStoreMigrator).GetMethod(nameof(CopySliceGeneric), BindingFlags.NonPublic | BindingFlags.Static)!;

        /// <summary>仅限工作线程调用：将 <paramref name="sourceInstance"/> 的全部活跃粒子按 Archetype 批量迁移至 <paramref name="globalSlot"/> 并清空源槽位。</summary>
        public static int MigrateToGlobalOnWorker(ParticleInstance sourceInstance, ParticleStoreSlot globalSlot)
        {
            ParticleStoreSlot? srcSlot = sourceInstance.StoreSlot;
            if (srcSlot is null)
                return 0;

            // 迁移前先回放源槽位未提交命令缓冲，防止已标记 DeleteEntity 的僵尸实体被迁入全局存储
            srcSlot.PlaybackCommandBuffer();
            if (srcSlot.EntityCount == 0)
                return 0;

            EntityStore srcStore = srcSlot.Store;
            EntityStore dstStore = globalSlot.Store;
            bool bakeLocalToWorld = sourceInstance.IsLocalSpace;
            Matrix originWorld = sourceInstance.OriginSnapshot.WorldMatrix;
            RenderTargetHandle? targetOverride = sourceInstance.TargetOverride;
            bool bakeFieldMask = !sourceInstance.AffectByGlobalFields || sourceInstance.GlobalFieldMask != ParticleFieldMask.All;
            ParticleFieldMask allowedGlobalMask = sourceInstance.AffectByGlobalFields ? sourceInstance.GlobalFieldMask : ParticleFieldMask.None;

            // 预分解宿主世界变换（含 2D/3D 镜像负缩放符号与右手旋转）
            float originRot2D = 0f;
            Vector2 originScale2D = Vector2.One;
            bool originMirror2D = false;
            Vector3 originScale3DSigned = Vector3.One;
            Vector3 originScale3DAbs = Vector3.One;
            Vector3 originSign3D = Vector3.One;
            Quaternion originRot3D = Quaternion.Identity;
            if (bakeLocalToWorld)
            {
                ExtractOriginBakeParams(in originWorld, out originRot2D, out originScale2D, out originMirror2D,
                    out originScale3DSigned, out originScale3DAbs, out originSign3D, out originRot3D);
            }

            int totalMigrated = 0;

            foreach (Archetype srcArch in srcStore.Archetypes)
            {
                int count = srcArch.Count;
                if (count <= 0)
                    continue;

                ComponentTypes types = srcArch.ComponentTypes;
                Tags tags = srcArch.Tags;
                Archetype dstArch = dstStore.GetArchetype(in types, in tags);
                int dstStart = dstArch.Count;
                dstArch.CreateEntities(count);

                // 1. 按组件类型整段 Span 零装箱批量拷贝（自动兼容所有内置与未来自定义组件）
                foreach (ComponentType compType in types)
                {
                    ComponentSliceCopier? copier = GetOrCreateCopier(compType.Type);
                    copier?.Invoke(srcArch, dstArch, dstStart, count);
                }

                // 2. 若源子系统处于 LocalSpace，一次性将位置、速度、姿态（含镜像符号）、缩放曲线与重力烘焙至世界空间
                if (bakeLocalToWorld)
                {
                    BakeSliceToWorld(dstArch, dstStart, count, in originWorld, originRot2D, originScale2D, originMirror2D,
                        originScale3DSigned, originScale3DAbs, originSign3D, originRot3D);
                }

                // 3. 若源子系统配置了 TargetOverride，将其固化至粒子自身的 ParticleRenderBase.Target
                if (targetOverride.HasValue && types.Has<ParticleRenderBase>())
                {
                    RenderTargetHandle target = targetOverride.Value;
                    Span<ParticleRenderBase> renders = dstArch.Components<ParticleRenderBase>().Slice(dstStart, count);
                    for (int i = 0; i < count; i++)
                        renders[i].Target = target;
                }

                // 4. 若源子系统屏蔽了部分或全部全局力场，固化至粒子的 ParticleFieldReceiver.Mask 避免脱钩后突然受力
                if (bakeFieldMask && types.Has<ParticleFieldReceiver>())
                {
                    Span<ParticleFieldReceiver> receivers = dstArch.Components<ParticleFieldReceiver>().Slice(dstStart, count);
                    for (int i = 0; i < count; i++)
                        receivers[i].Mask &= allowedGlobalMask;
                }

                totalMigrated += count;
            }

            srcSlot.ClearEntities();
            return totalMigrated;
        }

        private static void BakeSliceToWorld(Archetype dstArch, int dstStart, int count, in Matrix originWorld,
            float originRot2D, Vector2 originScale2D, bool originMirror2D,
            Vector3 originScale3DSigned, Vector3 originScale3DAbs, Vector3 originSign3D, Quaternion originRot3D)
        {
            ComponentTypes types = dstArch.ComponentTypes;

            if (types.Has<ParticleSpatial>())
            {
                Span<ParticleSpatial> spatials = dstArch.Components<ParticleSpatial>().Slice(dstStart, count);
                for (int i = 0; i < count; i++)
                {
                    ref ParticleSpatial s = ref spatials[i];
                    s.Position = Vector3.Transform(s.Position, originWorld);
                    s.Velocity = Vector3.TransformNormal(s.Velocity, originWorld);
                }
            }

            if (types.Has<ParticleGravity>())
            {
                Span<ParticleGravity> gravities = dstArch.Components<ParticleGravity>().Slice(dstStart, count);
                for (int i = 0; i < count; i++)
                    gravities[i].Acceleration = Vector3.TransformNormal(gravities[i].Acceleration, originWorld);
            }

            bool hasScaleCurve = types.Has<ParticleScaleOverLife>();
            if (types.Has<ParticlePose2D>())
            {
                Span<ParticlePose2D> poses = dstArch.Components<ParticlePose2D>().Slice(dstStart, count);
                if (originMirror2D)
                {
                    for (int i = 0; i < count; i++)
                    {
                        ref ParticlePose2D p = ref poses[i];
                        p.Rotation = originRot2D - p.Rotation;
                        p.AngularVelocity = -p.AngularVelocity;
                        p.Scale *= originScale2D;
                    }
                }
                else
                {
                    for (int i = 0; i < count; i++)
                    {
                        ref ParticlePose2D p = ref poses[i];
                        p.Rotation += originRot2D;
                        p.Scale *= originScale2D;
                    }
                }

                if (hasScaleCurve)
                {
                    Span<ParticleScaleOverLife> curves = dstArch.Components<ParticleScaleOverLife>().Slice(dstStart, count);
                    for (int i = 0; i < count; i++)
                    {
                        ref ParticleScaleOverLife c = ref curves[i];
                        c.StartScale.X *= originScale2D.X;
                        c.StartScale.Y *= originScale2D.Y;
                        c.EndScale.X *= originScale2D.X;
                        c.EndScale.Y *= originScale2D.Y;
                    }
                }
            }
            else if (types.Has<ParticlePose3D>())
            {
                Span<ParticlePose3D> poses = dstArch.Components<ParticlePose3D>().Slice(dstStart, count);
                bool hasMirror3D = originSign3D.X < 0f || originSign3D.Y < 0f || originSign3D.Z < 0f;
                Vector3 angSign = new(originSign3D.Y * originSign3D.Z, originSign3D.X * originSign3D.Z, originSign3D.X * originSign3D.Y);

                for (int i = 0; i < count; i++)
                {
                    ref ParticlePose3D p = ref poses[i];
                    if (p.Billboard == ParticleBillboardMode.None)
                    {
                        // 含负缩放反射时先用 ConjugateBySign 反射局部旋转与角速度，再左乘宿主旋转（FNA 行向量 M(p)*M(w) = M(w*p)）
                        Quaternion localRot = hasMirror3D ? Transform3D.ConjugateBySign(p.Rotation, originSign3D) : p.Rotation;
                        p.Rotation = NormalizeSafe(originRot3D * localRot);
                        if (hasMirror3D)
                            p.AngularVelocity *= angSign;
                        p.Scale *= originScale3DSigned;
                    }
                    else
                    {
                        // CameraFacing Billboard 渲染时不继承宿主旋转与镜像符号，仅叠加无符号缩放
                        p.Scale *= originScale3DAbs;
                    }
                }

                if (hasScaleCurve)
                {
                    Span<ParticleScaleOverLife> curves = dstArch.Components<ParticleScaleOverLife>().Slice(dstStart, count);
                    for (int i = 0; i < count; i++)
                    {
                        Vector3 scaleFactor = poses[i].Billboard == ParticleBillboardMode.None ? originScale3DSigned : originScale3DAbs;
                        ref ParticleScaleOverLife c = ref curves[i];
                        c.StartScale *= scaleFactor;
                        c.EndScale *= scaleFactor;
                    }
                }
            }
        }

        private static void ExtractOriginBakeParams(in Matrix world, out float rot2D, out Vector2 scale2D, out bool mirror2D,
            out Vector3 scale3DSigned, out Vector3 scale3DAbs, out Vector3 sign3D, out Quaternion rot3D)
        {
            Vector3 axisX = new(world.M11, world.M12, world.M13);
            Vector3 axisY = new(world.M21, world.M22, world.M23);

            float sx = axisX.Length();
            float sy = axisY.Length();
            float safeSx = float.IsFinite(sx) && sx > 1e-6f ? sx : 1f;
            float safeSy = float.IsFinite(sy) && sy > 1e-6f ? sy : 1f;

            // 2D 平面行列式判定镜像：负值时保留 X 轴负缩放并反转旋转方向，防止水平翻转宿主脱钩后倒翻 180°
            float det2D = axisX.X * axisY.Y - axisX.Y * axisY.X;
            mirror2D = det2D < 0f;
            if (mirror2D)
            {
                scale2D = new Vector2(-safeSx, safeSy);
                rot2D = float.IsFinite(axisX.X) && float.IsFinite(axisX.Y) && (axisX.X != 0f || axisX.Y != 0f)
                    ? MathF.Atan2(-axisX.Y, -axisX.X)
                    : 0f;
            }
            else
            {
                scale2D = new Vector2(safeSx, safeSy);
                rot2D = float.IsFinite(axisX.X) && float.IsFinite(axisX.Y) && (axisX.X != 0f || axisX.Y != 0f)
                    ? MathF.Atan2(axisX.Y, axisX.X)
                    : 0f;
            }

            // 3D 分解直接复用项目内支持负缩放反射的 Transform3D.DecomposeMatrix
            Transform3D decomposed = Transform3D.DecomposeMatrix(in world);
            Vector3 s = decomposed.Scale;
            scale3DSigned = new Vector3(
                float.IsFinite(s.X) && MathF.Abs(s.X) > 1e-6f ? s.X : 1f,
                float.IsFinite(s.Y) && MathF.Abs(s.Y) > 1e-6f ? s.Y : 1f,
                float.IsFinite(s.Z) && MathF.Abs(s.Z) > 1e-6f ? s.Z : 1f);
            scale3DAbs = new Vector3(MathF.Abs(scale3DSigned.X), MathF.Abs(scale3DSigned.Y), MathF.Abs(scale3DSigned.Z));
            sign3D = new Vector3(scale3DSigned.X < 0f ? -1f : 1f, scale3DSigned.Y < 0f ? -1f : 1f, scale3DSigned.Z < 0f ? -1f : 1f);
            rot3D = NormalizeSafe(decomposed.Rotation);
        }

        private static ComponentSliceCopier? GetOrCreateCopier(Type componentType)
        {
            return CopierCache.GetOrAdd(componentType, static t =>
            {
                if (!t.IsValueType || !typeof(IComponent).IsAssignableFrom(t) || t.ContainsGenericParameters)
                    return null;

                MethodInfo closed = CopySliceGenericDef.MakeGenericMethod(t);
                return (ComponentSliceCopier)Delegate.CreateDelegate(typeof(ComponentSliceCopier), closed);
            });
        }

        private static void CopySliceGeneric<T>(Archetype src, Archetype dst, int dstStart, int count) where T : struct, IComponent
            => src.Components<T>().Slice(0, count).CopyTo(dst.Components<T>().Slice(dstStart, count));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Quaternion NormalizeSafe(Quaternion q)
        {
            float lenSq = q.LengthSquared();
            if (!(lenSq > 1e-12f) || !float.IsFinite(lenSq))
                return Quaternion.Identity;

            float invLen = 1f / MathF.Sqrt(lenSq);
            return new Quaternion(q.X * invLen, q.Y * invLen, q.Z * invLen, q.W * invLen);
        }
    }
}
