using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Oxide.Compiler.IR.TypeRefs;

namespace Oxide.Compiler.IR.Types
{
    public class PrimitiveType : OxType
    {
        public static ImmutableDictionary<PrimitiveKind, ConcreteTypeRef> TypeRefs;
        public static ImmutableDictionary<PrimitiveKind, PrimitiveType> Types;
        public static PrimitiveKind[] Integers = { PrimitiveKind.USize, PrimitiveKind.U8, PrimitiveKind.I32 };

        static PrimitiveType()
        {
            var types = new Dictionary<PrimitiveKind, PrimitiveType>();
            var typeRefs = new Dictionary<PrimitiveKind, ConcreteTypeRef>();

            foreach (var kind in Enum.GetValues<PrimitiveKind>())
            {
                var type = new PrimitiveType
                {
                    Name = new QualifiedName(true, new[] { "std", Enum.GetName(kind)!.ToLower() }),
                    Visibility = Visibility.Public,
                    GenericParams = ImmutableList<string>.Empty,
                    Kind = kind,
                };
                types.Add(kind, type);
                typeRefs.Add(kind, new ConcreteTypeRef(type.Name, ImmutableArray<TypeRef>.Empty));
            }

            TypeRefs = typeRefs.ToImmutableDictionary();
            Types = types.ToImmutableDictionary();
        }

        public static ConcreteTypeRef GetRef(PrimitiveKind kind)
        {
            return TypeRefs[kind];
        }

        public static bool IsPrimitiveInt(TypeRef tref)
        {
            return Integers.Any(kind => Equals(tref, TypeRefs[kind]));
        }

        public static PrimitiveKind GetKind(TypeRef typeRef)
        {
            foreach (var pair in TypeRefs)
            {
                if (Equals(pair.Value, typeRef))
                {
                    return pair.Key;
                }
            }

            throw new Exception($"Unable to find {typeRef}");
        }

        public static int GetWidth(PrimitiveKind kind)
        {
            return kind switch
            {
                PrimitiveKind.Bool => 1,
                PrimitiveKind.USize => 64,
                PrimitiveKind.U8 => 8,
                PrimitiveKind.I32 => 32,
                _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
            };
        }

        public static bool IsSigned(PrimitiveKind kind)
        {
            return kind switch
            {
                PrimitiveKind.USize or PrimitiveKind.U8 => false,
                PrimitiveKind.I32 => true,
                _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
            };
        }


        public PrimitiveKind Kind { get; init; }
    }
}