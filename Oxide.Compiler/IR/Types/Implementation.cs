using System.Collections.Generic;

namespace Oxide.Compiler.IR.Types
{
    public class Implementation
    {
        public QualifiedName Target { get; }

        public QualifiedName Interface { get; }

        public List<Function> Functions { get; }

        public Implementation(QualifiedName target, QualifiedName @interface)
        {
            Target = target;
            Interface = @interface;
            Functions = new List<Function>();
        }
    }
}