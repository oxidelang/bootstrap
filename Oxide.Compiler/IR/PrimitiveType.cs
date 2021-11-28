using System.Collections.Immutable;

namespace Oxide.Compiler.IR
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

        public static TypeRef I32Ref = new()
        {
            Name = I32.Name,
            Category = TypeCategory.Direct,
            Source = TypeSource.Concrete,
            GenericParams = ImmutableArray<TypeRef>.Empty,
            MutableRef = false
        };

        public static PrimitiveType Bool = new()
        {
            Name = new QualifiedName(true, new[] { "std", "bool" }),
            Visibility = Visibility.Public,
            GenericParams = ImmutableList<string>.Empty,
            Kind = PrimitiveKind.Bool,
        };

        public static TypeRef BoolRef = new()
        {
            Name = Bool.Name,
            Category = TypeCategory.Direct,
            Source = TypeSource.Concrete,
            GenericParams = ImmutableArray<TypeRef>.Empty,
            MutableRef = false
        };

        public PrimitiveKind Kind { get; init; }
    }
}