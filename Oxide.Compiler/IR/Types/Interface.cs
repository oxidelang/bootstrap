using System.Collections.Immutable;

namespace Oxide.Compiler.IR.Types
{
    public class Interface : OxType
    {
        public ImmutableList<Function> Functions { get; }

        public Interface(QualifiedName name, Visibility visibility, ImmutableList<string> genericParams,
            ImmutableList<Function> functions)
        {
            Name = name;
            Visibility = visibility;
            GenericParams = genericParams;
            Functions = functions;
        }
    }
}