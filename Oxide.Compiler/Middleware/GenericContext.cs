using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using Oxide.Compiler.IR.TypeRefs;

namespace Oxide.Compiler.Middleware
{
    public class GenericContext
    {
        public GenericContext Parent { get; }

        public ImmutableDictionary<string, TypeRef> Generics { get; }

        public GenericContext(GenericContext parent, ImmutableList<string> genericParams,
            ImmutableArray<TypeRef> genericValues)
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
        }

        public TypeRef ResolveGeneric(string name)
        {
            return Generics.ContainsKey(name) ? Generics[name] : Parent?.ResolveGeneric(name);
        }

        public TypeRef ResolveRef(TypeRef typeRef)
        {
            switch (typeRef)
            {
                case ConcreteTypeRef concreteTypeRef:
                    return concreteTypeRef;
                case GenericTypeRef genericTypeRef:
                    return ResolveGeneric(genericTypeRef.Name);
                case DerivedTypeRef derivedTypeRef:
                case ThisTypeRef thisTypeRef:
                    throw new NotImplementedException();
                case BorrowTypeRef borrowTypeRef:
                    return new BorrowTypeRef(ResolveRef(borrowTypeRef.InnerType), borrowTypeRef.MutableRef);
                case PointerTypeRef pointerTypeRef:
                    return new PointerTypeRef(ResolveRef(pointerTypeRef.InnerType), pointerTypeRef.MutableRef);
                case ReferenceTypeRef referenceTypeRef:
                    return new ReferenceTypeRef(ResolveRef(referenceTypeRef.InnerType), referenceTypeRef.StrongRef);
                default:
                    throw new ArgumentOutOfRangeException(nameof(typeRef));
            }
        }
    }
}