using System.Collections.Immutable;

namespace Oxide.Compiler.IR.Types;

public class OxEnum : OxType
{
    public PrimitiveKind UnderlyingType { get; }

    public ImmutableDictionary<string, object> Items { get; }

    public OxEnum(QualifiedName name, Visibility visibility, PrimitiveKind underlyingType,
        ImmutableDictionary<string, object> items)
    {
        Name = name;
        Visibility = visibility;
        GenericParams = ImmutableList<string>.Empty;
        UnderlyingType = underlyingType;
        Items = items;
    }
}