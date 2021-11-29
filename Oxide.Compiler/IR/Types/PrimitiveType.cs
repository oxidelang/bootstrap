using System.Collections.Immutable;
using Oxide.Compiler.IR.TypeRefs;

namespace Oxide.Compiler.IR.Types
{
    public class PrimitiveType : OxType
    {
        public static PrimitiveType I32 = new()
        {
            Name = new QualifiedName(true, new[] { "std", "i32" }),
            Visibility = Visibility.Public,
            GenericParams = ImmutableList<string>.Empty,
            Kind = PrimitiveKind.I32,
        };

        public static DirectTypeRef I32Ref = new(I32.Name, TypeSource.Concrete, ImmutableArray<TypeRef>.Empty);

        public static PrimitiveType Bool = new()
        {
            Name = new QualifiedName(true, new[] { "std", "bool" }),
            Visibility = Visibility.Public,
            GenericParams = ImmutableList<string>.Empty,
            Kind = PrimitiveKind.Bool,
        };

        public static DirectTypeRef BoolRef = new(Bool.Name, TypeSource.Concrete, ImmutableArray<TypeRef>.Empty);

        public PrimitiveKind Kind { get; init; }
    }
}