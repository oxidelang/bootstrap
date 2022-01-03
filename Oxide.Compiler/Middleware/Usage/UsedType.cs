using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using Oxide.Compiler.IR;
using Oxide.Compiler.IR.TypeRefs;
using Oxide.Compiler.Utils;

namespace Oxide.Compiler.Middleware.Usage;

public class UsedType
{
    public QualifiedName Name { get; }

    public Dictionary<ImmutableArray<TypeRef>, UsedTypeVersion> Versions { get; set; }

    public UsedType(QualifiedName name)
    {
        Name = name;
        Versions = new Dictionary<ImmutableArray<TypeRef>, UsedTypeVersion>(
            new SequenceEqualityComparer<ImmutableArray<TypeRef>>()
        );
    }

    public UsedTypeVersion MarkGenericVariant(ImmutableArray<TypeRef> types)
    {
        if (!Versions.TryGetValue(types, out var usedType))
        {
            Console.WriteLine($" - New variant of {Name}: {string.Join(",", types)}");
            usedType = new UsedTypeVersion(this, types);
            Versions.Add(types, usedType);
        }

        return usedType;
    }

    protected bool Equals(UsedType other)
    {
        return Equals(Name, other.Name);
    }

    public override bool Equals(object obj)
    {
        if (ReferenceEquals(null, obj)) return false;
        if (ReferenceEquals(this, obj)) return true;
        if (obj.GetType() != this.GetType()) return false;
        return Equals((UsedType)obj);
    }

    public override int GetHashCode()
    {
        return Name.GetHashCode();
    }
}