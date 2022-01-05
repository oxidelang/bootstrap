using System.Collections.Generic;

namespace Oxide.Compiler.Middleware.Lifetimes;

public class BlockLifetime
{
    public int BlockId { get; }

    public HashSet<int> IncomingBlocks { get; }

    public BlockLifetime(int blockId)
    {
        BlockId = blockId;
        IncomingBlocks = new HashSet<int>();
    }
}