#nullable enable

namespace Apotheosis.Runtime.Foundation
{
    /// <summary>
    /// 运行时基础公共常量定义。
    /// </summary>
    public static class RuntimeConstants
    {
        /// <summary>
        /// 通用无效索引 / 无效句柄 / 无效代际哨兵值（<c>0</c>）。
        /// <para>
        /// 以 <see cref="byte"/> 常量定义，在 C# 中可无损隐式宽化至 <see cref="byte"/>、<see cref="ushort"/>、
        /// <see cref="int"/>、<see cref="uint"/>、<see cref="ulong"/> 等常用整数位宽，调用方无需关心底层存储类型。
        /// </para>
        /// </summary>
        public const byte IndexNone = 0;

        /// <summary>
        /// 通用代际句柄的起始有效版本号（<c>1</c>）。
        /// <para>
        /// 将 <c>0</c> 保留给未初始化/默认构造的无效句柄，确保即使未来存在 0-based 槽位句柄，
        /// 任意已分配的有效句柄打包值也永不为 <c>0</c>。
        /// </para>
        /// </summary>
        public const byte InitialGeneration = 1;

        /// <summary>
        /// 有符号 0-based 数组的通用无效索引（<c>-1</c>），例如泰拉瑞亚实体 <c>whoAmI</c>。
        /// </summary>
        public const int InvalidIndex = -1;
    }
}
