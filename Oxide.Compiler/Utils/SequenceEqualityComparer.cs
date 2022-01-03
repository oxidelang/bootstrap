using System.Collections;
using System.Collections.Generic;

namespace Oxide.Compiler.Utils;

public class SequenceEqualityComparer<T> : IEqualityComparer<T> where T : IStructuralEquatable
{
    private static readonly IEqualityComparer Comparer = new PassthroughEqualityComparer<T>();

    public bool Equals(T? x, T? y)
    {
        if (x == null && y == null)
        {
            return true;
        }

        if (x == null || y == null)
        {
            return false;
        }

        return x.Equals(y, Comparer);
    }

    public int GetHashCode(T obj)
    {
        return obj.GetHashCode(Comparer);
    }
}