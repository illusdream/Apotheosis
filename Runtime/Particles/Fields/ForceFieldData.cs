#nullable enable

using System;
using Apotheosis.Runtime.Particles.Core;
using Microsoft.Xna.Framework;

namespace Apotheosis.Runtime.Particles.Fields
{
    /// <summary>粒子力场类型。</summary>
    public enum ForceFieldKind : byte
    {
        /// <summary>定向风场 / 线性力场（沿 <see cref="ForceFieldData.Direction"/> 施加恒定或按半径衰减的加速度）。</summary>
        Directional = 0,

        /// <summary>点引力井 / 径向爆炸冲击波（正强度吸引，负强度排斥）。</summary>
        PointRadial = 1,

        /// <summary>旋涡场（绕 <see cref="ForceFieldData.Direction"/> 轴施加切向旋转加速度，可叠加向心拉力）。</summary>
        Vortex = 2,

        /// <summary>程序化空间湍流场（无纹理依赖，按 <see cref="ForceFieldData.Direction"/> 作为各轴幅值权重）。</summary>
        Turbulence = 3,
    }

    /// <summary>有界力场的距离衰减曲线模式。</summary>
    public enum ForceFieldFalloff : byte
    {
        /// <summary>作用范围内恒定强度，超出边界归零。</summary>
        None = 0,

        /// <summary>随归一化距离线性衰减至边界归零。</summary>
        Linear = 1,

        /// <summary>随归一化距离二次平滑衰减至边界归零。</summary>
        Quadratic = 2,
    }

    /// <summary>粒子力场作用区域几何形状。</summary>
    public enum ForceFieldShape : byte
    {
        /// <summary>圆形/球形范围（<see cref="ForceFieldData.Radius"/> &lt;= 0 时为全局无限范围）。</summary>
        Sphere = 0,

        /// <summary>矩形/长方体范围（由 <see cref="ForceFieldData.BoxHalfExtents"/> 与可选 <see cref="ForceFieldData.BoxRotation2D"/> 定义）。</summary>
        Box = 1,
    }

    /// <summary>通用粒子力场描述数据（紧凑值类型，同时适用于全局世界力场与子系统局部力场）。</summary>
    public struct ForceFieldData
    {
        /// <summary>力场类型。</summary>
        public ForceFieldKind Kind;

        /// <summary>有界距离衰减模式。</summary>
        public ForceFieldFalloff Falloff;

        /// <summary>作用区域形状（默认 <see cref="ForceFieldShape.Sphere"/>）。</summary>
        public ForceFieldShape Shape;

        /// <summary>力场分类位掩码（与 <c>ParticleFieldReceiver.Mask</c> 及实例 <c>GlobalFieldMask</c> 按位与过滤）。</summary>
        public ParticleFieldMask Mask;

        /// <summary>力场中心坐标（全局力场为世界坐标，局部子系统力场为其模拟空间坐标）。</summary>
        public Vector3 Position;

        /// <summary>方向/轴向参数（定向风向、旋涡旋转轴或湍流三轴权重）。</summary>
        public Vector3 Direction;

        /// <summary>主加速度强度（像素/秒²；点力场正值吸引、负值排斥）。</summary>
        public float Strength;

        /// <summary>球形/圆形作用半径（像素；<see cref="Shape"/> 为 <see cref="ForceFieldShape.Sphere"/> 且 <c>&lt;= 0</c> 时表示无限范围）。</summary>
        public float Radius;

        /// <summary>点力场与旋涡中心的最小钳位距离（像素，防止粒子贴近奇异点时速度暴冲）。</summary>
        public float MinDistance;

        /// <summary>旋涡场附加向心拉力强度（像素/秒²；正值向心收束，负值离心甩出）。</summary>
        public float InwardStrength;

        /// <summary>湍流空间频率（弧度/像素，默认约 <c>0.015..0.03</c>）。</summary>
        public float Frequency;

        /// <summary>矩形/长方体半尺寸（<c>X=半宽, Y=半高, Z=半深</c>；<c>Z &lt;= 0</c> 时沿 Z 轴无限延伸，适配 2D 游戏）。</summary>
        public Vector3 BoxHalfExtents;

        /// <summary>矩形范围绕 <c>+Z</c> 轴的 2D 旋转弧度（默认 <c>0</c> 为轴对齐矩形）。</summary>
        public float BoxRotation2D;

        /// <summary>将本力场作用域切换为 2D 矩形（Z 轴无限延伸，支持任意 2D 旋转角）。</summary>
        public readonly ForceFieldData WithBox2D(Vector2 halfExtents, float rotationRad = 0f, ForceFieldFalloff? falloff = null)
        {
            ForceFieldData copy = this;
            copy.Shape = ForceFieldShape.Box;
            copy.BoxHalfExtents = new Vector3(halfExtents.X, halfExtents.Y, 0f);
            copy.BoxRotation2D = rotationRad;
            if (falloff.HasValue)
                copy.Falloff = falloff.Value;
            return copy;
        }

        /// <summary>将本力场作用域切换为 3D 长方体（<c>halfExtents.Z &lt;= 0</c> 时沿 Z 轴无限延伸）。</summary>
        public readonly ForceFieldData WithBox3D(Vector3 halfExtents, float rotationZRad = 0f, ForceFieldFalloff? falloff = null)
        {
            ForceFieldData copy = this;
            copy.Shape = ForceFieldShape.Box;
            copy.BoxHalfExtents = halfExtents;
            copy.BoxRotation2D = rotationZRad;
            if (falloff.HasValue)
                copy.Falloff = falloff.Value;
            return copy;
        }

        /// <summary>将本力场作用域切换为圆形/球形（<paramref name="radius"/> &lt;= 0 表示无限范围）。</summary>
        public readonly ForceFieldData WithSphere(float radius, ForceFieldFalloff? falloff = null)
        {
            ForceFieldData copy = this;
            copy.Shape = ForceFieldShape.Sphere;
            copy.Radius = radius;
            if (falloff.HasValue)
                copy.Falloff = falloff.Value;
            return copy;
        }

        /// <summary>构造定向风场 / 线性力场。</summary>
        public static ForceFieldData Directional(Vector3 direction, float strength, ParticleFieldMask mask = ParticleFieldMask.Wind,
            Vector3 position = default, float radius = 0f, ForceFieldFalloff falloff = ForceFieldFalloff.Linear)
        {
            return new ForceFieldData
            {
                Kind = ForceFieldKind.Directional,
                Falloff = falloff,
                Mask = mask,
                Position = position,
                Direction = direction,
                Strength = strength,
                Radius = radius,
                MinDistance = 16f,
            };
        }

        /// <summary>构造 2D 定向风场。</summary>
        public static ForceFieldData Directional2D(Vector2 direction, float strength, ParticleFieldMask mask = ParticleFieldMask.Wind,
            Vector2 position = default, float radius = 0f, ForceFieldFalloff falloff = ForceFieldFalloff.Linear)
            => Directional(new Vector3(direction, 0f), strength, mask, new Vector3(position, 0f), radius, falloff);

        /// <summary>构造点引力井或爆炸冲击波（<paramref name="strength"/> 正值吸引、负值排斥）。</summary>
        public static ForceFieldData PointRadial(Vector3 position, float strength, float radius = 0f, float minDistance = 16f,
            ForceFieldFalloff falloff = ForceFieldFalloff.Quadratic, ParticleFieldMask mask = ParticleFieldMask.GravityWell)
        {
            return new ForceFieldData
            {
                Kind = ForceFieldKind.PointRadial,
                Falloff = falloff,
                Mask = mask,
                Position = position,
                Direction = Vector3.UnitZ,
                Strength = strength,
                Radius = radius,
                MinDistance = minDistance > 1f ? minDistance : 1f,
            };
        }

        /// <summary>构造 2D 点引力井或径向爆炸冲击波。</summary>
        public static ForceFieldData PointRadial2D(Vector2 position, float strength, float radius = 0f, float minDistance = 16f,
            ForceFieldFalloff falloff = ForceFieldFalloff.Quadratic, ParticleFieldMask mask = ParticleFieldMask.GravityWell)
            => PointRadial(new Vector3(position, 0f), strength, radius, minDistance, falloff, mask);

        /// <summary>构造旋涡场（默认绕 +Z 轴在 2D 平面旋转，可指定 3D 旋转轴与向心拉力）。</summary>
        public static ForceFieldData Vortex(Vector3 position, float swirlStrength, float inwardStrength = 0f, float radius = 0f,
            Vector3? axis = null, float minDistance = 16f, ForceFieldFalloff falloff = ForceFieldFalloff.Linear, ParticleFieldMask mask = ParticleFieldMask.Vortex)
        {
            return new ForceFieldData
            {
                Kind = ForceFieldKind.Vortex,
                Falloff = falloff,
                Mask = mask,
                Position = position,
                Direction = axis ?? Vector3.UnitZ,
                Strength = swirlStrength,
                InwardStrength = inwardStrength,
                Radius = radius,
                MinDistance = minDistance > 1f ? minDistance : 1f,
            };
        }

        /// <summary>构造 2D 平面旋涡场。</summary>
        public static ForceFieldData Vortex2D(Vector2 position, float swirlStrength, float inwardStrength = 0f, float radius = 0f,
            float minDistance = 16f, ForceFieldFalloff falloff = ForceFieldFalloff.Linear, ParticleFieldMask mask = ParticleFieldMask.Vortex)
            => Vortex(new Vector3(position, 0f), swirlStrength, inwardStrength, radius, Vector3.UnitZ, minDistance, falloff, mask);

        /// <summary>构造程序化空间湍流场（<paramref name="axisWeights"/> 默认 <c>(1, 1, 0)</c> 保持 2D 粒子不漂移 Z 深度）。</summary>
        public static ForceFieldData Turbulence(float strength, float frequency = 0.02f, Vector3 position = default, float radius = 0f,
            Vector3? axisWeights = null, ForceFieldFalloff falloff = ForceFieldFalloff.Linear, ParticleFieldMask mask = ParticleFieldMask.Turbulence)
        {
            return new ForceFieldData
            {
                Kind = ForceFieldKind.Turbulence,
                Falloff = falloff,
                Mask = mask,
                Position = position,
                Direction = axisWeights ?? new Vector3(1f, 1f, 0f),
                Strength = strength,
                Radius = radius,
                Frequency = float.IsFinite(frequency) && frequency > 0f ? frequency : 0.02f,
                MinDistance = 16f,
            };
        }
    }

    /// <summary>工作线程侧托管的活跃力场记录（含可选自动过期时长）。</summary>
    internal struct ActiveForceFieldEntry
    {
        public uint FieldId;
        public ForceFieldData Data;
        public float RemainingDuration;
        public bool HasDuration;
    }
}
