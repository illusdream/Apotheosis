#nullable enable

using Apotheosis.Runtime.Foundation;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Terraria;
using Terraria.Graphics;

namespace Apotheosis.Runtime.Rendering
{
    /// <summary>单帧渲染全局上下文快照。</summary>
    public readonly struct RenderFrameContext
    {
        /// <summary>当前渲染帧号。</summary>
        public readonly ulong FrameId;

        /// <summary>视图上下文修订号。</summary>
        public readonly ulong ContextRevision;

        /// <summary>全局循环时间（对应 <c>uTime</c>）。</summary>
        public readonly float Time;

        /// <summary>当前帧时间增量（秒）。</summary>
        public readonly float DeltaTime;

        /// <summary>当前活动视口像素分辨率（对应 <c>uScreenResolution</c>）。</summary>
        public readonly Vector2 ScreenResolution;

        /// <summary>当前摄像机缩放倍率（对应 <c>uZoom</c>）。</summary>
        public readonly Vector2 Zoom;

        /// <summary>当前活动视口的正交投影矩阵。</summary>
        public readonly Matrix ProjectionMatrix;

        /// <summary>世界空间到 NDC 裁剪空间的变换矩阵（对应 <c>uTransformMatrix</c>）。</summary>
        public readonly Matrix WorldTransformMatrix;

        /// <summary>屏幕空间到 NDC 裁剪空间的变换矩阵。</summary>
        public readonly Matrix SpriteTransformMatrix;

        public RenderFrameContext(ulong frameId, ulong contextRevision, float time, float deltaTime,
            Vector2 screenResolution, Vector2 zoom, in Matrix projectionMatrix, in Matrix worldTransformMatrix, in Matrix spriteTransformMatrix)
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

        /// <summary>从当前 Terraria 全局状态与活动视口采集帧上下文。</summary>
        public static RenderFrameContext Capture(ulong frameId, ulong contextRevision, float deltaTime)
        {
            SpriteViewMatrix viewMatrix = Main.GameViewMatrix;
            Viewport viewport = Main.graphics?.GraphicsDevice?.Viewport ?? default;
            int width = viewport.Width > 0 ? viewport.Width : (Main.screenWidth > 0 ? Main.screenWidth : 1);
            int height = viewport.Height > 0 ? viewport.Height : (Main.screenHeight > 0 ? Main.screenHeight : 1);

            int logicalWidth = Main.screenWidth > 0 ? Main.screenWidth : width;
            int logicalHeight = Main.screenHeight > 0 ? Main.screenHeight : height;

            var resolution = new Vector2(width, height);
            Vector2 zoom = viewMatrix.Zoom;
            Matrix viewTransform = viewMatrix.TransformationMatrix;

            // 宽深度正交投影；世界/精灵矩阵按逻辑屏幕尺寸投影，保证非 1:1 比例离屏 RT 与主屏 1:1 对齐
            Matrix projection = Matrix.CreateOrthographicOffCenter(0f, width, height, 0f, RuntimeConstants.OrthoNearPlane, RuntimeConstants.OrthoFarPlane);
            Matrix logicalProjection = (logicalWidth == width && logicalHeight == height)
                ? projection
                : Matrix.CreateOrthographicOffCenter(0f, logicalWidth, logicalHeight, 0f, RuntimeConstants.OrthoNearPlane, RuntimeConstants.OrthoFarPlane);
            Matrix spriteTransform = viewTransform * logicalProjection;
            Matrix worldTransform = Matrix.CreateTranslation(-Main.screenPosition.X, -Main.screenPosition.Y, 0f) * spriteTransform;

            return new RenderFrameContext(frameId, contextRevision, Main.GlobalTimeWrappedHourly, deltaTime,
                resolution, zoom, in projection, in worldTransform, in spriteTransform);
        }
    }
}
