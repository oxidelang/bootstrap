using System.Collections.Generic;

namespace Oxide.Compiler.Middleware.Lifetimes;

public class FunctionLifetime
{
    public int Entry { get; set; }

    public Dictionary<int, HashSet<int>> IncomingBlocks { get; }

    public Dictionary<int, InstructionLifetime> InstructionLifetimes { get; }

    public Dictionary<int, HashSet<Requirement>> ValueRequirements { get; }

    public Dictionary<int, HashSet<Requirement>> DirectRequirements { get; }

    public Dictionary<int, int> ValueMap { get; }

    public Dictionary<int, int> ValueSourceParameters { get; }

    public FunctionLifetime()
    {
        IncomingBlocks = new Dictionary<int, HashSet<int>>();
        InstructionLifetimes = new Dictionary<int, InstructionLifetime>();
        ValueRequirements = new Dictionary<int, HashSet<Requirement>>();
        DirectRequirements = new Dictionary<int, HashSet<Requirement>>();
        ValueMap = new Dictionary<int, int>();
        ValueSourceParameters = new Dictionary<int, int>();
    }
}