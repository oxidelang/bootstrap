using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Oxide.Compiler.IR.TypeRefs;

namespace Oxide.Compiler.IR.Types;

public class PrimitiveType : OxType
{
    public static ImmutableDictionary<PrimitiveKind, ConcreteTypeRef> TypeRefs;
    public static ImmutableDictionary<PrimitiveKind, PrimitiveType> Types;

    public static PrimitiveKind[] Integers =
    {
        PrimitiveKind.USize, PrimitiveKind.U8, PrimitiveKind.U16, PrimitiveKind.U32, PrimitiveKind.U64,
        PrimitiveKind.ISize, PrimitiveKind.I8, PrimitiveKind.I16, PrimitiveKind.I32, PrimitiveKind.I64
    };

    public static PrimitiveKind[] SignedIntegers =
    {
        PrimitiveKind.ISize, PrimitiveKind.I8, PrimitiveKind.I16, PrimitiveKind.I32, PrimitiveKind.I64
    };

    static PrimitiveType()
    {
        var types = new Dictionary<PrimitiveKind, PrimitiveType>();
        var typeRefs = new Dictionary<PrimitiveKind, ConcreteTypeRef>();

        foreach (var kind in Enum.GetValues<PrimitiveKind>())
        {
            var type = new PrimitiveType
            {
                Name = QualifiedName.From("std", Enum.GetName(kind)!.ToLower()),
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

    public static PrimitiveKind? GetPossibleKind(TypeRef typeRef)
    {
        foreach (var pair in TypeRefs)
        {
            if (Equals(pair.Value, typeRef))
            {
                return pair.Key;
            }
        }

        return null;
    }

    public static int GetWidth(PrimitiveKind kind, bool is32bit = false)
    {
        return kind switch
        {
            PrimitiveKind.Bool => 1,
            PrimitiveKind.USize => is32bit ? 32 : 64,
            PrimitiveKind.U8 => 8,
            PrimitiveKind.U16 => 16,
            PrimitiveKind.U32 => 32,
            PrimitiveKind.U64 => 64,
            PrimitiveKind.ISize => is32bit ? 32 : 64,
            PrimitiveKind.I8 => 8,
            PrimitiveKind.I16 => 16,
            PrimitiveKind.I32 => 32,
            PrimitiveKind.I64 => 64,
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
        };
    }

    public static bool IsSigned(PrimitiveKind kind)
    {
        return SignedIntegers.Contains(kind);
    }

    public static bool IsInt(PrimitiveKind kind)
    {
        return Integers.Contains(kind);
    }

    public PrimitiveKind Kind { get; init; }
}