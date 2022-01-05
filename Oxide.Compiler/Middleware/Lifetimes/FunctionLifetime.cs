using System.Collections.Generic;

namespace Oxide.Compiler.Middleware.Lifetimes;

public class FunctionLifetime
{
    public int Entry { get; set; }

    public Dictionary<int, HashSet<int>> IncomingBlocks { get; }

    public Dictionary<int, InstructionLifetime> InstructionLifetimes { get; }

    public FunctionLifetime()
    {
        IncomingBlocks = new Dictionary<int, HashSet<int>>();
        InstructionLifetimes = new Dictionary<int, InstructionLifetime>();
    }
}