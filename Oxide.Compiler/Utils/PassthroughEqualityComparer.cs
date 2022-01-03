using System.Collections;
using System.Collections.Generic;

namespace Oxide.Compiler.Utils;

public class PassthroughEqualityComparer<T> : IEqualityComparer<T>, IEqualityComparer
{
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

        return x.Equals(y);
    }

    public int GetHashCode(T obj)
    {
        return obj.GetHashCode();
    }

    public bool Equals(object? x, object? y)
    {
        if (x == null && y == null)
        {
            return true;
        }

        if (x == null || y == null)
        {
            return false;
        }

        if (x.GetType() != y.GetType())
        {
            return false;
        }

        return x.Equals(y);
    }

    public int GetHashCode(object obj)
    {
        return obj.GetHashCode();
    }
}