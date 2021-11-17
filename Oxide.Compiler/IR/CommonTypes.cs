using System.Collections.Immutable;

namespace Oxide.Compiler.IR
{
    public static class CommonTypes
    {
        public static TypeDef I32 = new()
        {
            Name = new QualifiedName(true, new[] { "std", "i32" }),
            Category = TypeCategory.Direct,
            MutableRef = false,
            Source = TypeSource.Concrete,
            GenericParams = ImmutableArray<TypeDef>.Empty
        };

        public static TypeDef Bool = new()
        {
            Name = new QualifiedName(true, new[] { "std", "bool" }),
            Category = TypeCategory.Direct,
            MutableRef = false,
            Source = TypeSource.Concrete,
            GenericParams = ImmutableArray<TypeDef>.Empty
        };
    }
}