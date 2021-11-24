using System.Collections.Immutable;

namespace Oxide.Compiler.IR
{
    public class Struct : OxType
    {
        public ImmutableList<Field> Fields { get; }

        public Struct(QualifiedName name, Visibility visibility, ImmutableList<string> genericParams,
            ImmutableList<Field> fields)
        {
            Name = name;
            Visibility = visibility;
            GenericParams = genericParams;
            Fields = fields;
        }
    }
}