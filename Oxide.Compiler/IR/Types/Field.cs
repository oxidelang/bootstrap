using Oxide.Compiler.IR.TypeRefs;

namespace Oxide.Compiler.IR.Types;

public class Field
{
    public string Name { get; init; }

    public Visibility Visibility { get; init; }

    public TypeRef Type { get; init; }

    public bool Mutable { get; set; }
}