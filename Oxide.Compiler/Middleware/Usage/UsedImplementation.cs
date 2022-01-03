using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Oxide.Compiler.IR.TypeRefs;
using Oxide.Compiler.IR.Types;

namespace Oxide.Compiler.Middleware.Usage;

public class UsedImplementation
{
    public ConcreteTypeRef Interface { get; }
    public Dictionary<string, UsedFunction> Functions { get; }

    public UsedImplementation(ConcreteTypeRef iface)
    {
        Interface = iface;
        Functions = new Dictionary<string, UsedFunction>();
    }

    public bool MarkFunction(Function func, ImmutableArray<TypeRef> generics)
    {
        var funcName = func.Name.Parts.Single();

        if (!Functions.TryGetValue(funcName, out var function))
        {
            function = new UsedFunction(func.Name);
            Functions.Add(funcName, function);
        }

        return function.MarkVersion(generics);
    }
}