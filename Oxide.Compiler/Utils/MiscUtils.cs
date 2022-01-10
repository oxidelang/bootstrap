using System.Collections.Generic;

namespace Oxide.Compiler.Utils;

public static class MiscUtils
{
    public static bool AddRange<T>(this HashSet<T> source, IEnumerable<T> items)
    {
        var addedSome = false;
        foreach (var item in items)
        {
            addedSome |= source.Add(item);
        }

        return addedSome;
    }
}