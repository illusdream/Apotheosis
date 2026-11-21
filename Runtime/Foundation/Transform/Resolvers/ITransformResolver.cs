#nullable enable

namespace Apotheosis.Runtime.Foundation.Transform.Resolvers
{
    /// <summary>
    /// 显式变换解析器接口：仅负责在 <see cref="TransformNode"/> 与具体目标实体的原有空间字段之间进行双向读写映射。
    /// </summary>
    /// <typeparam name="TTarget">目标实体类型。</typeparam>
    public interface ITransformResolver<in TTarget>
    {
        /// <summary>从目标实体的空间字段读取变换写入 <paramref name="node"/>。</summary>
        void ReadFrom(TransformNode node, TTarget target, TransformSyncFlags flags = TransformSyncFlags.All);

        /// <summary>显式将 <paramref name="node"/> 的世界变换写回目标实体的原有空间字段。</summary>
        void WriteTo(TransformNode node, TTarget target, TransformSyncFlags flags = TransformSyncFlags.All);
    }

    /// <summary>
    /// 显式变换解析器抽象基类，提供可重写的读写模板。
    /// </summary>
    public abstract class TransformResolver<TTarget> : ITransformResolver<TTarget>
    {
        public abstract void ReadFrom(TransformNode node, TTarget target, TransformSyncFlags flags = TransformSyncFlags.All);

        public abstract void WriteTo(TransformNode node, TTarget target, TransformSyncFlags flags = TransformSyncFlags.All);
    }
}
