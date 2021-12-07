using System.Collections.Generic;
using System.Collections.Immutable;

namespace Oxide.Compiler.IR.Types
{
    public class Implementation
    {
        public QualifiedName Target { get; }

        public QualifiedName Interface { get; }

        public ImmutableArray<Function> Functions { get; }

        public Implementation(QualifiedName target, QualifiedName @interface, ImmutableArray<Function> functions)
        {
            Target = target;
            Interface = @interface;
            Functions = functions;
        }
    }
}