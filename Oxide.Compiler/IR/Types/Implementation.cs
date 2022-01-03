using System.Collections.Generic;
using System.Collections.Immutable;
using Oxide.Compiler.IR.TypeRefs;

namespace Oxide.Compiler.IR.Types;

public class Implementation
{
    public ConcreteTypeRef Target { get; }

    public ConcreteTypeRef Interface { get; }

    public List<Function> Functions { get; }

    public ImmutableArray<string> GenericParams { get; }

    public Implementation(ConcreteTypeRef target, ConcreteTypeRef @interface, ImmutableArray<string> genericParams)
    {
        Target = target;
        Interface = @interface;
        Functions = new List<Function>();
        GenericParams = genericParams;
    }

        
}