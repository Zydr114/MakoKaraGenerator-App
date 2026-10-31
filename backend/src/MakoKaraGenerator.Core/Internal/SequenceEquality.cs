using System.Collections.Immutable;

namespace MakoKaraGenerator.Core.Internal;

/// <summary>
/// <see cref="ImmutableArray{T}"/> 的结构比较与内容哈希辅助。
/// </summary>
/// <remarks>
/// <see cref="ImmutableArray{T}"/> 自带的相等性比较的是内部数组引用，不是内容，
/// 因此领域模型中持有集合的记录必须使用本类。
/// 本类是内部实现细节，不作为公共 API 暴露。
/// </remarks>
internal static class SequenceEquality
{
    /// <summary>按内容比较两个序列，并把 <c>default</c> 视为空序列。</summary>
    internal static bool ContentEquals<T>(ImmutableArray<T> left, ImmutableArray<T> right)
        where T : IEquatable<T>
    {
        int leftLength = left.IsDefault ? 0 : left.Length;
        int rightLength = right.IsDefault ? 0 : right.Length;

        if (leftLength != rightLength)
        {
            return false;
        }

        for (int i = 0; i < leftLength; i++)
        {
            if (!left[i].Equals(right[i]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>把序列内容混入哈希值，并把 <c>default</c> 视为空序列。</summary>
    internal static void AddContentHash<T>(ref HashCode hash, ImmutableArray<T> values)
        where T : IEquatable<T>
    {
        if (values.IsDefaultOrEmpty)
        {
            return;
        }

        foreach (T value in values)
        {
            hash.Add(value);
        }
    }
}
