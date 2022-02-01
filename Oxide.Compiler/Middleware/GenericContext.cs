using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Oxide.Compiler.IR.TypeRefs;

namespace Oxide.Compiler.Middleware;

public class GenericContext
{
    public static GenericContext Default = new(null, ImmutableDictionary<string, TypeRef>.Empty, null);

    public GenericContext Parent { get; }

    public ImmutableDictionary<string, TypeRef> Generics { get; }

    public ConcreteTypeRef ThisRef { get; }

    public GenericContext(GenericContext parent, ImmutableDictionary<string, TypeRef> generics,
        ConcreteTypeRef thisRef)
    {
        Parent = parent;
        Generics = generics;
        ThisRef = thisRef;
    }

    public GenericContext(GenericContext parent, ImmutableList<string> genericParams,
        ImmutableArray<TypeRef> genericValues, ConcreteTypeRef thisRef)
    {
        Parent = parent;
        var generics = new Dictionary<string, TypeRef>();

        if (genericParams.Count != genericValues.Length)
        {
            throw new ArgumentException("Mismatch of generic params and values");
        }

        for (var i = 0; i < genericParams.Count; i++)
        {
            generics.Add(genericParams[i], genericValues[i]);
        }

        Generics = generics.ToImmutableDictionary();
        ThisRef = thisRef;
    }

    public TypeRef ResolveGeneric(string name)
    {
        return Generics.ContainsKey(name) ? Generics[name] : Parent?.ResolveGeneric(name);
    }

    public ImmutableArray<TypeRef> ResolveRefs(ImmutableArray<TypeRef> refs)
    {
        return refs.Select(ResolveRef).ToImmutableArray();
    }

    public TypeRef ResolveRef(TypeRef typeRef)
    {
        switch (typeRef)
        {
            case null:
                return null;
            case ConcreteTypeRef concreteTypeRef:
                return new ConcreteTypeRef(
                    concreteTypeRef.Name,
                    concreteTypeRef.GenericParams.Select(ResolveRef).ToImmutableArray()
                );
            case GenericTypeRef genericTypeRef:
                return ResolveGeneric(genericTypeRef.Name) ?? throw new Exception("Unable to resolve generic type");
            case DerivedTypeRef derivedTypeRef:
                throw new NotImplementedException();
            case ThisTypeRef thisTypeRef:
                return ThisRef;
            case BorrowTypeRef borrowTypeRef:
                return new BorrowTypeRef(ResolveRef(borrowTypeRef.InnerType), borrowTypeRef.MutableRef);
            case PointerTypeRef pointerTypeRef:
                return new PointerTypeRef(ResolveRef(pointerTypeRef.InnerType), pointerTypeRef.MutableRef);
            case ReferenceTypeRef referenceTypeRef:
                return new ReferenceTypeRef(ResolveRef(referenceTypeRef.InnerType), referenceTypeRef.StrongRef);
            case DerivedRefTypeRef derivedRefTypeRef:
                return new DerivedRefTypeRef(ResolveRef(derivedRefTypeRef.InnerType), derivedRefTypeRef.StrongRef);
            default:
                throw new ArgumentOutOfRangeException(nameof(typeRef));
        }
    }
}