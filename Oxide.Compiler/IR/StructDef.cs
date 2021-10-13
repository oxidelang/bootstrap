using System.Collections.Immutable;

namespace Oxide.Compiler.IR
{
    public class StructDef
    {
        public QualifiedName Name { get; }

        public Visibility Visibility { get; }

        public ImmutableList<string> GenericParams { get; }

        public ImmutableList<FieldDef> Fields { get; }

        public StructDef(QualifiedName name, Visibility visibility, ImmutableList<string> genericParams,
            ImmutableList<FieldDef> fields)
        {
            Name = name;
            Visibility = visibility;
            GenericParams = genericParams;
            Fields = fields;
        }
    }
}