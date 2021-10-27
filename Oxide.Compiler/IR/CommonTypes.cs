namespace Oxide.Compiler.IR
{
    public static class CommonTypes
    {
        public static TypeDef I32 = new TypeDef
        {
            Name = new QualifiedName(true, new[] { "std", "i32" }),
            Category = TypeCategory.Direct,
            MutableRef = false,
            Source = TypeSource.Concrete,
            GenericParams = null
        };
    }
}