using System.Collections.Generic;

namespace Oxide.Compiler.Middleware.Lifetimes;

public class FunctionLifetime
{
    public Dictionary<int, BlockLifetime> BlockLifetimes { get; }

    public int Entry { get; set; }

    public FunctionLifetime()
    {
        BlockLifetimes = new Dictionary<int, BlockLifetime>();
    }

    public BlockLifetime GetBlock(int id)
    {
        if (BlockLifetimes.TryGetValue(id, out var lifetime))
        {
            return lifetime;
        }

        lifetime = new BlockLifetime(id);
        BlockLifetimes.Add(lifetime.BlockId, lifetime);
        return lifetime;
    }
}