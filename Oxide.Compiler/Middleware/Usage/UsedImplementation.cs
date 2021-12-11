using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Oxide.Compiler.IR;
using Oxide.Compiler.IR.TypeRefs;
using Oxide.Compiler.IR.Types;

namespace Oxide.Compiler.Middleware.Usage
{
    public class UsedImplementation
    {
        public QualifiedName Interface { get; }
        public Dictionary<string, UsedFunction> Functions { get; }

        public UsedImplementation(QualifiedName iface)
        {
            Interface = iface;
            Functions = new Dictionary<string, UsedFunction>();
        }

        public void MarkFunction(Function func, ImmutableArray<TypeRef> generics)
        {
            var funcName = func.Name.Parts.Single();

            if (!Functions.TryGetValue(funcName, out var function))
            {
                function = new UsedFunction(func.Name);
                Functions.Add(funcName, function);
            }

            function.MarkVersion(generics);
        }
    }
}