#nullable enable

using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Apotheosis.Runtime.Rendering.Geometry
{
    /// <summary>向 <see cref="DynamicMesh"/> 追加常用 2D/3D 几何图形的扩展构建方法。</summary>
    public static class GeometryBuilder
    {
        private static readonly Vector3 DefaultNormal2D = new(0f, 0f, -1f);

        /// <summary>追加一个 2D 精灵四边形。</summary>
        public static void AddSpriteQuad(this DynamicMesh mesh, Vector2 position, Vector2 size, Color color,
            float rotation = 0f, Vector2 origin = default, Vector4? uvRect = null, Vector4 custom = default, float depthZ = 0f)
        {
            Vector4 uv = uvRect ?? InstanceData.FullUvRect;
            float u0 = uv.X;
            float v0 = uv.Y;
            float u1 = uv.X + uv.Z;
            float v1 = uv.Y + uv.W;

            float left = -origin.X;
            float top = -origin.Y;
            float right = size.X - origin.X;
            float bottom = size.Y - origin.Y;

            Vector3 p0, p1, p2, p3;
            if (MathF.Abs(rotation) <= 1e-6f)
            {
                p0 = new Vector3(position.X + left, position.Y + top, depthZ);
                p1 = new Vector3(position.X + right, position.Y + top, depthZ);
                p2 = new Vector3(position.X + right, position.Y + bottom, depthZ);
                p3 = new Vector3(position.X + left, position.Y + bottom, depthZ);
            }
            else
            {
                float cos = MathF.Cos(rotation);
                float sin = MathF.Sin(rotation);
                p0 = new Vector3(position.X + left * cos - top * sin, position.Y + left * sin + top * cos, depthZ);
                p1 = new Vector3(position.X + right * cos - top * sin, position.Y + right * sin + top * cos, depthZ);
                p2 = new Vector3(position.X + right * cos - bottom * sin, position.Y + right * sin + bottom * cos, depthZ);
                p3 = new Vector3(position.X + left * cos - bottom * sin, position.Y + left * sin + bottom * cos, depthZ);
            }

            mesh.AddQuadVertices(
                new UnifiedVertex(p0, DefaultNormal2D, color, new Vector2(u0, v0), custom),
                new UnifiedVertex(p1, DefaultNormal2D, color, new Vector2(u1, v0), custom),
                new UnifiedVertex(p2, DefaultNormal2D, color, new Vector2(u1, v1), custom),
                new UnifiedVertex(p3, DefaultNormal2D, color, new Vector2(u0, v1), custom));
        }

        /// <summary>追加一条带恒定像素宽度的 2D 线段。</summary>
        public static void AddLine(this DynamicMesh mesh, Vector2 start, Vector2 end, float thickness, Color startColor, Color endColor, float depthZ = 0f, Vector4 custom = default)
        {
            Vector2 delta = end - start;
            float lenSq = delta.LengthSquared();
            if (lenSq <= 1e-6f || thickness <= 0f)
                return;

            float invLen = 1f / MathF.Sqrt(lenSq);
            Vector2 perp = new Vector2(-delta.Y * invLen, delta.X * invLen) * (thickness * 0.5f);

            mesh.AddQuadVertices(
                new UnifiedVertex(new Vector3(start + perp, depthZ), DefaultNormal2D, startColor, new Vector2(0f, 0f), custom),
                new UnifiedVertex(new Vector3(end + perp, depthZ), DefaultNormal2D, endColor, new Vector2(1f, 0f), custom),
                new UnifiedVertex(new Vector3(end - perp, depthZ), DefaultNormal2D, endColor, new Vector2(1f, 1f), custom),
                new UnifiedVertex(new Vector3(start - perp, depthZ), DefaultNormal2D, startColor, new Vector2(0f, 1f), custom));
        }

        /// <summary>追加一个恒定线宽的圆环。</summary>
        public static void AddRing(this DynamicMesh mesh, Vector2 center, float radius, float thickness, int segments, Color color, float startAngle = 0f, float depthZ = 0f, Vector4 custom = default)
        {
            AddArc(mesh, center, radius, thickness, segments, startAngle, MathF.Tau, color, color, depthZ, custom);
        }

        /// <summary>追加一段恒定线宽的圆弧。</summary>
        public static void AddArc(this DynamicMesh mesh, Vector2 center, float radius, float thickness, int segments,
            float startAngle, float sweepAngle, Color startColor, Color endColor, float depthZ = 0f, Vector4 custom = default)
        {
            if (segments < 1 || thickness <= 0f || MathF.Abs(sweepAngle) <= 1e-5f)
                return;

            float halfThick = thickness * 0.5f;
            float innerR = MathF.Max(0f, radius - halfThick);
            float outerR = radius + halfThick;

            int ringPoints = segments + 1;
            int baseV = mesh.AllocateVertices(ringPoints * 2, out Span<UnifiedVertex> verts);
            Span<int> indices = mesh.AllocateIndices(segments * 6);

            for (int i = 0; i < ringPoints; i++)
            {
                float t = (float)i / segments;
                float angle = startAngle + sweepAngle * t;
                float cos = MathF.Cos(angle);
                float sin = MathF.Sin(angle);
                Color col = Color.Lerp(startColor, endColor, t);

                Vector3 outerPos = new(center.X + cos * outerR, center.Y + sin * outerR, depthZ);
                Vector3 innerPos = new(center.X + cos * innerR, center.Y + sin * innerR, depthZ);
                Vector4 c = new(t, thickness, custom.Z, custom.W);

                int vIdx = i * 2;
                verts[vIdx] = new UnifiedVertex(outerPos, DefaultNormal2D, col, new Vector2(t, 0f), c);
                verts[vIdx + 1] = new UnifiedVertex(innerPos, DefaultNormal2D, col, new Vector2(t, 1f), c);
            }

            for (int i = 0; i < segments; i++)
            {
                int v = baseV + i * 2;
                int dst = i * 6;
                indices[dst] = v;
                indices[dst + 1] = v + 2;
                indices[dst + 2] = v + 1;
                indices[dst + 3] = v + 1;
                indices[dst + 4] = v + 2;
                indices[dst + 5] = v + 3;
            }
        }

        /// <summary>追加一个恒定线宽的正多边形边框。</summary>
        public static void AddPolygon(this DynamicMesh mesh, Vector2 center, float radius, int sides, float thickness, float rotation, Color color, float depthZ = 0f, Vector4 custom = default)
        {
            if (sides < 3 || radius <= 0f || thickness <= 0f)
                return;

            float step = MathF.Tau / sides;
            Vector2 prev = new(
                center.X + MathF.Cos(rotation) * radius,
                center.Y + MathF.Sin(rotation) * radius);

            for (int i = 1; i <= sides; i++)
            {
                float angle = rotation + step * i;
                Vector2 next = new(
                    center.X + MathF.Cos(angle) * radius,
                    center.Y + MathF.Sin(angle) * radius);

                mesh.AddLine(prev, next, thickness, color, color, depthZ, custom);
                prev = next;
            }
        }

        /// <summary>追加一条带宽度与颜色渐变的 2D 丝带拖尾。</summary>
        public static void AddTrailRibbon(this DynamicMesh mesh, ReadOnlySpan<Vector2> points, float startWidth, float endWidth,
            Color startColor, Color endColor, Func<float, float>? widthCurve = null, Func<float, Color>? colorCurve = null, float depthZ = 0f)
        {
            int count = points.Length;
            if (count < 2)
                return;

            int baseV = mesh.AllocateVertices(count * 2, out Span<UnifiedVertex> verts);
            Span<int> indices = mesh.AllocateIndices((count - 1) * 6);
            float invSpan = 1f / (count - 1);

            Vector2 lastDir = Vector2.UnitX;

            for (int i = 0; i < count; i++)
            {
                float t = i * invSpan;
                Vector2 dir;
                if (i == 0)
                    dir = points[1] - points[0];
                else if (i == count - 1)
                    dir = points[count - 1] - points[count - 2];
                else
                    dir = points[i + 1] - points[i - 1];

                float lenSq = dir.LengthSquared();
                if (lenSq > 1e-6f)
                {
                    dir /= MathF.Sqrt(lenSq);
                    lastDir = dir;
                }
                else
                {
                    dir = lastDir;
                }

                float width = widthCurve is not null ? widthCurve(t) : MathHelper.Lerp(startWidth, endWidth, t);
                Color color = colorCurve is not null ? colorCurve(t) : Color.Lerp(startColor, endColor, t);
                Vector2 perp = new Vector2(-dir.Y, dir.X) * (width * 0.5f);

                Vector2 p = points[i];
                Vector4 custom = new(t, width, 0f, 0f);

                int vIdx = i * 2;
                verts[vIdx] = new UnifiedVertex(new Vector3(p + perp, depthZ), DefaultNormal2D, color, new Vector2(t, 0f), custom);
                verts[vIdx + 1] = new UnifiedVertex(new Vector3(p - perp, depthZ), DefaultNormal2D, color, new Vector2(t, 1f), custom);
            }

            for (int i = 0; i < count - 1; i++)
            {
                int v = baseV + i * 2;
                int dst = i * 6;
                indices[dst] = v;
                indices[dst + 1] = v + 2;
                indices[dst + 2] = v + 1;
                indices[dst + 3] = v + 1;
                indices[dst + 4] = v + 2;
                indices[dst + 5] = v + 3;
            }
        }

        /// <summary>创建内置中心对齐单位 2D 四边形（<c>[-0.5..+0.5]</c>，UV <c>[0..1]</c>），供硬件实例化与全屏/精灵复用。</summary>
        internal static StaticMesh CreateUnitQuad2D()
        {
            UnifiedVertex[] verts =
            {
                new(new Vector3(-0.5f, -0.5f, 0f), DefaultNormal2D, Color.White, new Vector2(0f, 0f), Vector4.Zero),
                new(new Vector3( 0.5f, -0.5f, 0f), DefaultNormal2D, Color.White, new Vector2(1f, 0f), Vector4.Zero),
                new(new Vector3( 0.5f,  0.5f, 0f), DefaultNormal2D, Color.White, new Vector2(1f, 1f), Vector4.Zero),
                new(new Vector3(-0.5f,  0.5f, 0f), DefaultNormal2D, Color.White, new Vector2(0f, 1f), Vector4.Zero),
            };
            int[] indices = { 0, 1, 2, 0, 2, 3 };
            return new StaticMesh(verts, indices, PrimitiveType.TriangleList);
        }

        /// <summary>创建内置中心对齐单位 3D 立方体（<c>[-0.5..+0.5]^3</c>，含每面独立法线与 UV），供 3D 测试与基础实例化复用。</summary>
        internal static StaticMesh CreateUnitCube3D()
        {
            var mesh = new DynamicMesh(24, 36);
            AddCubeFace(mesh, new Vector3(-0.5f, -0.5f, -0.5f), new Vector3(0.5f, -0.5f, -0.5f), new Vector3(0.5f, 0.5f, -0.5f), new Vector3(-0.5f, 0.5f, -0.5f), new Vector3(0f, 0f, -1f), Color.White);
            AddCubeFace(mesh, new Vector3(0.5f, -0.5f, 0.5f), new Vector3(-0.5f, -0.5f, 0.5f), new Vector3(-0.5f, 0.5f, 0.5f), new Vector3(0.5f, 0.5f, 0.5f), new Vector3(0f, 0f, 1f), Color.White);
            AddCubeFace(mesh, new Vector3(-0.5f, -0.5f, 0.5f), new Vector3(-0.5f, -0.5f, -0.5f), new Vector3(-0.5f, 0.5f, -0.5f), new Vector3(-0.5f, 0.5f, 0.5f), new Vector3(-1f, 0f, 0f), Color.White);
            AddCubeFace(mesh, new Vector3(0.5f, -0.5f, -0.5f), new Vector3(0.5f, -0.5f, 0.5f), new Vector3(0.5f, 0.5f, 0.5f), new Vector3(0.5f, 0.5f, -0.5f), new Vector3(1f, 0f, 0f), Color.White);
            AddCubeFace(mesh, new Vector3(-0.5f, -0.5f, 0.5f), new Vector3(0.5f, -0.5f, 0.5f), new Vector3(0.5f, -0.5f, -0.5f), new Vector3(-0.5f, -0.5f, -0.5f), new Vector3(0f, -1f, 0f), Color.White);
            AddCubeFace(mesh, new Vector3(-0.5f, 0.5f, -0.5f), new Vector3(0.5f, 0.5f, -0.5f), new Vector3(0.5f, 0.5f, 0.5f), new Vector3(-0.5f, 0.5f, 0.5f), new Vector3(0f, 1f, 0f), Color.White);
            return mesh.BakeToStatic();
        }

        private static void AddCubeFace(DynamicMesh mesh, Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, Vector3 normal, Color color)
        {
            mesh.AddQuadVertices(
                new UnifiedVertex(p0, normal, color, new Vector2(0f, 0f), Vector4.Zero),
                new UnifiedVertex(p1, normal, color, new Vector2(1f, 0f), Vector4.Zero),
                new UnifiedVertex(p2, normal, color, new Vector2(1f, 1f), Vector4.Zero),
                new UnifiedVertex(p3, normal, color, new Vector2(0f, 1f), Vector4.Zero));
        }
    }
}
