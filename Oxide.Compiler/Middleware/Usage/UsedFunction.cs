using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using Oxide.Compiler.IR;
using Oxide.Compiler.IR.TypeRefs;
using Oxide.Compiler.Utils;

namespace Oxide.Compiler.Middleware.Usage;

public class UsedFunction
{
    public QualifiedName Name { get; }

    public HashSet<ImmutableArray<TypeRef>> Versions { get; init; }

    public UsedFunction(QualifiedName name)
    {
        Name = name;
        Versions = new HashSet<ImmutableArray<TypeRef>>(
            new SequenceEqualityComparer<ImmutableArray<TypeRef>>()
        );
    }

    public bool MarkVersion(ImmutableArray<TypeRef> version)
    {
        if (Versions.Add(version))
        {
            Console.WriteLine($" - New func version {Name}");
            return true;
        }

        return false;
    }
}