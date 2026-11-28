#nullable enable

using Apotheosis.Runtime.Particles.Core;
using Apotheosis.Runtime.Utils;
using Friflo.Engine.ECS;
using Microsoft.Xna.Framework;

namespace Apotheosis.Runtime.Particles.Components
{
    /// <summary>3D 粒子姿态的朝向模式。</summary>
    public enum ParticleBillboardMode : byte
    {
        /// <summary>保持完整 3D 空间旋转姿态（翻滚碎片、花瓣、空间光环）。</summary>
        None = 0,

        /// <summary>始终朝向屏幕，仅应用局部绕 Z 轴自转（适合 3D 空间轨道运动的光点/烟雾）。</summary>
        CameraFacing = 1,
    }

    /// <summary>粒子与物块发生碰撞后的响应模式。</summary>
    public enum ParticleTileCollisionMode : byte
    {
        /// <summary>碰撞后立即销毁。</summary>
        Destroy = 0,

        /// <summary>碰撞后按弹性与摩擦系数反弹。</summary>
        Bounce = 1,

        /// <summary>碰撞后速度归零并黏附在表面。</summary>
        Stick = 2,
    }

    // ==================== 核心必备模拟组件 ====================

    /// <summary>粒子生命周期组件（单位：秒）。</summary>
    public struct ParticleLife : IComponent
    {
        /// <summary>当前已存活时长（秒）。</summary>
        public float Age;

        /// <summary>最大存活总时长（秒）。</summary>
        public float MaxAge;

        /// <summary>归一化寿命进度（<c>0..1</c>，由寿命系统每步更新）。</summary>
        public float NormalizedAge;

        public ParticleLife(float maxAge)
        {
            Age = 0f;
            MaxAge = maxAge > 1e-4f ? maxAge : 1e-4f;
            NormalizedAge = 0f;
        }
    }

    /// <summary>统一 3D 空间位置与速度组件（2D 粒子将 <c>Position.Z</c> 用作景深 <c>DepthZ</c>，<c>Velocity.Z = 0</c>）。</summary>
    public struct ParticleSpatial : IComponent
    {
        /// <summary>空间位置（像素；局部空间子系统下为相对原点的局部坐标）。</summary>
        public Vector3 Position;

        /// <summary>线速度（像素/秒）。</summary>
        public Vector3 Velocity;

        public ParticleSpatial(Vector3 position, Vector3 velocity = default)
        {
            Position = position;
            Velocity = velocity;
        }

        public ParticleSpatial(Vector2 position2D, Vector2 velocity2D = default, float depthZ = 0f)
        {
            Position = new Vector3(position2D, depthZ);
            Velocity = new Vector3(velocity2D, 0f);
        }
    }

    /// <summary>2D 姿态组件（纯 <c>SinCos</c> 渲染快路径，无四元数开销）。</summary>
    public struct ParticlePose2D : IComponent
    {
        /// <summary>绕 <c>+Z</c> 轴的 2D 顺时针旋转弧度。</summary>
        public float Rotation;

        /// <summary>2D 旋转角速度（弧度/秒）。</summary>
        public float AngularVelocity;

        /// <summary>XY 平面缩放倍率。</summary>
        public Vector2 Scale;

        public ParticlePose2D()
        {
            Rotation = 0f;
            AngularVelocity = 0f;
            Scale = Vector2.One;
        }

        public ParticlePose2D(float rotation, float angularVelocity, Vector2 scale)
        {
            Rotation = rotation;
            AngularVelocity = angularVelocity;
            Scale = scale;
        }

        public ParticlePose2D(float rotation, float angularVelocity = 0f, float uniformScale = 1f)
            : this(rotation, angularVelocity, new Vector2(uniformScale, uniformScale))
        {
        }
    }

    /// <summary>3D 姿态组件（支持完整 3D 四元数翻滚、朝向屏幕与可选伪透视景深缩放）。</summary>
    public struct ParticlePose3D : IComponent
    {
        /// <summary>3D 空间朝向四元数。</summary>
        public Quaternion Rotation;

        /// <summary>局部 X/Y/Z 三轴角速度（弧度/秒）。</summary>
        public Vector3 AngularVelocity;

        /// <summary>3D 缩放倍率。</summary>
        public Vector3 Scale;

        /// <summary>面片朝向模式（自由 3D 翻滚或始终面向屏幕）。</summary>
        public ParticleBillboardMode Billboard;

        /// <summary>伪透视景深缩放强度（<c>0</c> 为纯正交投影，<c>&gt; 0</c> 按深度做近大远小缩放）。</summary>
        public float PerspectiveFactor;

        public ParticlePose3D()
        {
            Rotation = Quaternion.Identity;
            AngularVelocity = Vector3.Zero;
            Scale = Vector3.One;
            Billboard = ParticleBillboardMode.None;
            PerspectiveFactor = 0f;
        }

        public ParticlePose3D(Quaternion rotation, Vector3 angularVelocity, Vector3 scale, ParticleBillboardMode billboard = ParticleBillboardMode.None, float perspectiveFactor = 0f)
        {
            Rotation = rotation;
            AngularVelocity = angularVelocity;
            Scale = scale;
            Billboard = billboard;
            PerspectiveFactor = perspectiveFactor;
        }
    }

    // ==================== 按需组合的功能行为组件 ====================

    /// <summary>自定义恒定加速度组件（重力或浮力）。</summary>
    public struct ParticleGravity : IComponent
    {
        /// <summary>恒定加速度向量（像素/秒²，<c>+Y</c> 向下；局部空间子系统下按局部坐标系施加）。</summary>
        public Vector3 Acceleration;

        public ParticleGravity(Vector3 acceleration) => Acceleration = acceleration;
        public ParticleGravity(Vector2 acceleration2D) => Acceleration = new Vector3(acceleration2D, 0f);
        public ParticleGravity(float gravityY) => Acceleration = new Vector3(0f, gravityY, 0f);
    }

    /// <summary>线性与角速度阻尼组件（每秒衰减系数）。</summary>
    public struct ParticleDrag : IComponent
    {
        /// <summary>线速度指数阻尼系数（每秒衰减率，<c>0</c> 为无阻尼）。</summary>
        public float LinearDrag;

        /// <summary>角速度指数阻尼系数（每秒衰减率，<c>0</c> 为无阻尼）。</summary>
        public float AngularDrag;

        public ParticleDrag(float linearDrag, float angularDrag = 0f)
        {
            LinearDrag = linearDrag;
            AngularDrag = angularDrag;
        }
    }

    /// <summary>随生命周期渐变 HDR 颜色组件。</summary>
    public struct ParticleColorOverLife : IComponent
    {
        /// <summary>出生时的起始 HDR RGBA 颜色。</summary>
        public Vector4 StartColor;

        /// <summary>生命末期的目标 HDR RGBA 颜色。</summary>
        public Vector4 EndColor;

        /// <summary>颜色插值所用的缓动曲线类型。</summary>
        public EaseType Ease;

        public ParticleColorOverLife(Vector4 startColor, Vector4 endColor, EaseType ease = EaseType.Linear)
        {
            StartColor = startColor;
            EndColor = endColor;
            Ease = ease;
        }

        public ParticleColorOverLife(Color startColor, Color endColor, EaseType ease = EaseType.Linear)
            : this(startColor.ToVector4(), endColor.ToVector4(), ease)
        {
        }
    }

    /// <summary>随生命周期渐变缩放倍率组件（2D 姿态取 <c>XY</c>，3D 姿态取 <c>XYZ</c>）。</summary>
    public struct ParticleScaleOverLife : IComponent
    {
        /// <summary>出生时的起始缩放倍率。</summary>
        public Vector3 StartScale;

        /// <summary>生命末期的目标缩放倍率。</summary>
        public Vector3 EndScale;

        /// <summary>缩放插值所用的缓动曲线类型。</summary>
        public EaseType Ease;

        public ParticleScaleOverLife(Vector3 startScale, Vector3 endScale, EaseType ease = EaseType.Linear)
        {
            StartScale = startScale;
            EndScale = endScale;
            Ease = ease;
        }

        public ParticleScaleOverLife(Vector2 startScale2D, Vector2 endScale2D, EaseType ease = EaseType.Linear)
            : this(new Vector3(startScale2D, 1f), new Vector3(endScale2D, 1f), ease)
        {
        }

        public ParticleScaleOverLife(float startUniform, float endUniform, EaseType ease = EaseType.Linear)
            : this(new Vector3(startUniform), new Vector3(endUniform), ease)
        {
        }
    }

    /// <summary>2D 速度方向自动对齐与速度拉伸组件（仅作用于 <see cref="ParticlePose2D"/>，适合火花、剑气碎片）。</summary>
    public struct ParticleVelocityAlign : IComponent
    {
        /// <summary>附加旋转偏移弧度（用于适配非水平朝向的基准贴图）。</summary>
        public float RotationOffset;

        /// <summary>沿局部 X 轴的速度拉伸倍率系数（基准贴图朝向 <c>+X</c>；<c>0</c> 表示仅对齐角度不拉伸）。</summary>
        public float LengthStretchFactor;

        /// <summary>触发朝向更新的最小速率平方阈值（避免低速抖动）。</summary>
        public float MinSpeedSq;

        // 记录无 ScaleOverLife 时上一帧已应用的拉伸倍率，防止每帧连乘爆炸
        internal float AppliedStretch;

        public ParticleVelocityAlign() : this(0f, 0f, 1f) { }

        public ParticleVelocityAlign(float rotationOffset, float lengthStretchFactor = 0f, float minSpeedSq = 1f)
        {
            RotationOffset = rotationOffset;
            LengthStretchFactor = lengthStretchFactor;
            MinSpeedSq = minSpeedSq;
            AppliedStretch = 1f;
        }
    }

    /// <summary>力场接收器组件，控制粒子响应哪些分类的全局/局部力场及受力倍率。</summary>
    public struct ParticleFieldReceiver : IComponent
    {
        /// <summary>允许响应的力场分类位掩码。</summary>
        public ParticleFieldMask Mask;

        /// <summary>整体受力缩放倍率（默认 <c>1.0</c>）。</summary>
        public float ForceScale;

        public ParticleFieldReceiver()
        {
            Mask = ParticleFieldMask.All;
            ForceScale = 1f;
        }

        public ParticleFieldReceiver(ParticleFieldMask mask, float forceScale = 1f)
        {
            Mask = mask;
            ForceScale = forceScale;
        }
    }

    /// <summary>Terraria 物块地形碰撞组件。</summary>
    public struct ParticleTileCollision : IComponent
    {
        /// <summary>法向反弹弹性系数（<c>0..1</c>）。</summary>
        public float Restitution;

        /// <summary>碰撞表面切向摩擦系数（<c>0..1</c>）。</summary>
        public float Friction;

        /// <summary>简易碰撞半径（像素）。</summary>
        public float Radius;

        /// <summary>撞击物块后的处理模式。</summary>
        public ParticleTileCollisionMode Mode;

        /// <summary>最大允许反弹次数（<c>0</c> 表示不限次数，超限后自动销毁）。</summary>
        public byte MaxBounces;

        /// <summary>当前已发生的反弹次数。</summary>
        public byte BounceCount;

        public ParticleTileCollision() : this(ParticleTileCollisionMode.Bounce) { }

        public ParticleTileCollision(ParticleTileCollisionMode mode, float restitution = 0.5f, float friction = 0.2f, float radius = 2f, byte maxBounces = 0)
        {
            Mode = mode;
            Restitution = restitution;
            Friction = friction;
            Radius = radius;
            MaxBounces = maxBounces;
            BounceCount = 0;
        }
    }

    /// <summary>序列帧图集动画组件（<c>FrameRate &gt; 0</c> 固定帧率循环；<c>FrameRate == 0</c> 随寿命播完一遍；<c>FrameRate &lt; 0</c> 静态锁定起始帧）。</summary>
    public struct ParticleSheetAnim : IComponent
    {
        /// <summary>图集水平列数。</summary>
        public ushort Columns;

        /// <summary>图集垂直行数。</summary>
        public ushort Rows;

        /// <summary>有效动画总帧数。</summary>
        public ushort FrameCount;

        /// <summary>当前播放帧索引（0-based）。</summary>
        public ushort CurrentFrame;

        /// <summary>播放帧率模式（<c>&gt; 0</c> 固定帧率循环；<c>== 0</c> 随归一化寿命线性播完一遍；<c>&lt; 0</c> 静态锁定当前帧）。</summary>
        public float FrameRate;

        /// <summary>固定帧率模式下的帧间隔累积计时器（秒）。</summary>
        public float FrameTimer;

        // 标记首帧是否已将当前帧格写入 QuadAtlasRenderData.UvRect，避免配合 FillQuadRender 时首帧显示整张大图
        internal bool UvSynced;

        public ParticleSheetAnim(ushort columns, ushort rows, ushort frameCount = 0, float frameRate = 0f, ushort startFrame = 0)
        {
            Columns = columns > 0 ? columns : (ushort)1;
            Rows = rows > 0 ? rows : (ushort)1;
            long total = (long)Columns * Rows;
            ushort maxFrames = total > ushort.MaxValue ? ushort.MaxValue : (ushort)total;
            FrameCount = frameCount > 0 && frameCount <= maxFrames ? frameCount : maxFrames;
            CurrentFrame = startFrame < FrameCount ? startFrame : (ushort)0;
            FrameRate = frameRate;
            FrameTimer = 0f;
            UvSynced = false;
        }
    }
}
