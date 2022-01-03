using System.Collections.Generic;
using System.Collections.Immutable;

namespace Oxide.Compiler.IR.Types;

public class Interface : OxType
{
    public List<Function> Functions { get; }

    public Interface(QualifiedName name, Visibility visibility, ImmutableList<string> genericParams)
    {
        Name = name;
        Visibility = visibility;
        GenericParams = genericParams;
        Functions = new List<Function>();
    }
}