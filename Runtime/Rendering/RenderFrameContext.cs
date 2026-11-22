#nullable enable

using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Terraria;
using Terraria.Graphics;

namespace Apotheosis.Runtime.Rendering
{
    /// <summary>
    /// 单帧渲染全局上下文只读快照：由 <see cref="RenderManager"/> 在每帧首次进入实际绘制读取时惰性采集唯一一次，
    /// 确保拿到全部 <c>ModSystem.ModifyTransformMatrix</c> 与相机更新完毕后的最终视图矩阵，
    /// 供 <see cref="Shaders.ShaderModule"/> 及后续合批、图元、RenderTarget 等模块共享。
    /// </summary>
    public readonly struct RenderFrameContext
    {
        /// <summary>递增帧号（每次 <see cref="Main.OnPreDraw"/> 递增 1）。</summary>
        public readonly ulong FrameId;

        /// <summary>
        /// 上下文修订号（每次实际重新采集视图上下文时递增 1），
        /// 供 <see cref="Shaders.ManagedShader"/> 判定全局参数是否需要重新同步。
        /// </summary>
        public readonly ulong ContextRevision;

        /// <summary>全局循环时间（对应 <c>uTime</c>，来源 <see cref="Main.GlobalTimeWrappedHourly"/>）。</summary>
        public readonly float Time;

        /// <summary>当前帧时间增量（秒）。</summary>
        public readonly float DeltaTime;

        /// <summary>当前活动视口/RenderTarget 像素分辨率（对应 <c>uScreenResolution</c>）。</summary>
        public readonly Vector2 ScreenResolution;

        /// <summary>当前摄像机缩放倍率（对应 <c>uZoom</c>，来源 <see cref="Main.GameViewMatrix"/>）。</summary>
        public readonly Vector2 Zoom;

        /// <summary>
        /// 当前活动视口的正交投影矩阵（屏幕像素空间到 NDC 裁剪空间），
        /// 适用于 <see cref="SpriteBatch"/> 已将顶点变换到屏幕空间的着色器（由 <c>PrepareForSpriteBatch</c> 自动写入 <c>uTransformMatrix</c>）。
        /// </summary>
        public readonly Matrix ProjectionMatrix;

        /// <summary>
        /// 默认世界空间到 NDC 裁剪空间的变换矩阵（对应 <c>uTransformMatrix</c>），
        /// 适用于自定义顶点图元与硬件实例化渲染。
        /// </summary>
        public readonly Matrix WorldTransformMatrix;

        /// <summary>
        /// 屏幕空间（含 <see cref="Main.GameViewMatrix"/> 变换）到 NDC 裁剪空间的变换矩阵，
        /// 适用于与 <c>SpriteBatch</c> 坐标系对齐但未经过 <c>SpriteBatch</c> 预变换的自定义绘制。
        /// </summary>
        public readonly Matrix SpriteTransformMatrix;

        public RenderFrameContext(
            ulong frameId,
            ulong contextRevision,
            float time,
            float deltaTime,
            Vector2 screenResolution,
            Vector2 zoom,
            in Matrix projectionMatrix,
            in Matrix worldTransformMatrix,
            in Matrix spriteTransformMatrix)
        {
            FrameId = frameId;
            ContextRevision = contextRevision;
            Time = time;
            DeltaTime = deltaTime;
            ScreenResolution = screenResolution;
            Zoom = zoom;
            ProjectionMatrix = projectionMatrix;
            WorldTransformMatrix = worldTransformMatrix;
            SpriteTransformMatrix = spriteTransformMatrix;
        }

        /// <summary>从当前 tModLoader / Terraria 全局状态与活动 <see cref="Viewport"/> 采集渲染上下文。</summary>
        public static RenderFrameContext Capture(ulong frameId, ulong contextRevision, float deltaTime)
        {
            SpriteViewMatrix viewMatrix = Main.GameViewMatrix;
            Viewport viewport = Main.graphics?.GraphicsDevice?.Viewport ?? default;
            int width = viewport.Width > 0 ? viewport.Width : (Main.screenWidth > 0 ? Main.screenWidth : 1);
            int height = viewport.Height > 0 ? viewport.Height : (Main.screenHeight > 0 ? Main.screenHeight : 1);

            var resolution = new Vector2(width, height);
            Vector2 zoom = viewMatrix.Zoom;
            Matrix viewTransform = viewMatrix.TransformationMatrix;

            Matrix projection = Matrix.CreateOrthographicOffCenter(0f, width, height, 0f, -1f, 1f);
            Matrix spriteTransform = viewTransform * projection;
            Matrix worldTransform =
                Matrix.CreateTranslation(-Main.screenPosition.X, -Main.screenPosition.Y, 0f)
                * spriteTransform;

            return new RenderFrameContext(
                frameId: frameId,
                contextRevision: contextRevision,
                time: Main.GlobalTimeWrappedHourly,
                deltaTime: deltaTime,
                screenResolution: resolution,
                zoom: zoom,
                projectionMatrix: in projection,
                worldTransformMatrix: in worldTransform,
                spriteTransformMatrix: in spriteTransform);
        }
    }
}
