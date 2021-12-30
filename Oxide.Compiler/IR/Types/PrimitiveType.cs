using System.Collections.Immutable;
using Oxide.Compiler.IR.TypeRefs;

namespace Oxide.Compiler.IR.Types
{
    public class PrimitiveType : OxType
    {
        public static PrimitiveType USize = new()
        {
            Name = new QualifiedName(true, new[] { "std", "usize" }),
            Visibility = Visibility.Public,
            GenericParams = ImmutableList<string>.Empty,
            Kind = PrimitiveKind.USize,
        };

        public static ConcreteTypeRef USizeRef = new(USize.Name, ImmutableArray<TypeRef>.Empty);

        public static PrimitiveType U8 = new()
        {
            Name = new QualifiedName(true, new[] { "std", "u8" }),
            Visibility = Visibility.Public,
            GenericParams = ImmutableList<string>.Empty,
            Kind = PrimitiveKind.U8,
        };

        public static ConcreteTypeRef U8Ref = new(U8.Name, ImmutableArray<TypeRef>.Empty);

        public static PrimitiveType I32 = new()
        {
            Name = new QualifiedName(true, new[] { "std", "i32" }),
            Visibility = Visibility.Public,
            GenericParams = ImmutableList<string>.Empty,
            Kind = PrimitiveKind.I32,
        };

        public static ConcreteTypeRef I32Ref = new(I32.Name, ImmutableArray<TypeRef>.Empty);

        public static PrimitiveType Bool = new()
        {
            Name = new QualifiedName(true, new[] { "std", "bool" }),
            Visibility = Visibility.Public,
            GenericParams = ImmutableList<string>.Empty,
            Kind = PrimitiveKind.Bool,
        };

        public static ConcreteTypeRef BoolRef = new(Bool.Name, ImmutableArray<TypeRef>.Empty);

        public PrimitiveKind Kind { get; init; }
    }
}