using System.Collections.Immutable;
using Oxide.Compiler.IR.TypeRefs;

namespace Oxide.Compiler.IR;

public class WhereConstraints
{
    public static WhereConstraints Default = new(ImmutableDictionary<string, ImmutableArray<TypeRef>>.Empty);

    private ImmutableDictionary<string, ImmutableArray<TypeRef>> _constraints;

    public WhereConstraints(ImmutableDictionary<string, ImmutableArray<TypeRef>> constraints)
    {
        _constraints = constraints;
    }

    public ImmutableArray<TypeRef> GetConstraints(string param)
    {
        if (_constraints.TryGetValue(param, out var constraints))
        {
            return constraints;
        }

        return ImmutableArray<TypeRef>.Empty;
    }
}