using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using Oxide.Compiler.IR.TypeRefs;

namespace Oxide.Compiler.Middleware.Usage;

public class UsedTypeVersion
{
    public UsedType Type { get; }

    public ImmutableArray<TypeRef> Generics { get; }

    public Dictionary<ConcreteTypeRef, UsedImplementation> Implementations { get; }

    public UsedImplementation DefaultImplementation { get; private set; }

    public UsedTypeVersion(UsedType type, ImmutableArray<TypeRef> generics)
    {
        Type = type;
        Generics = generics;
        Implementations = new Dictionary<ConcreteTypeRef, UsedImplementation>();
    }

    public UsedImplementation MarkImplementation(ConcreteTypeRef iface)
    {
        if (iface == null)
        {
            DefaultImplementation ??= new UsedImplementation(null);
            return DefaultImplementation;
        }

        if (!Implementations.TryGetValue(iface, out var usedImp))
        {
            Console.WriteLine($" - New implementation usage {iface} on {Type.Name} {string.Join(", ", Generics)}");
            usedImp = new UsedImplementation(iface);
            Implementations.Add(iface, usedImp);
        }

        return usedImp;
    }
}