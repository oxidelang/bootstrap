using Oxide.Compiler.Middleware.Usage;

namespace Oxide.Compiler.IR;

public class CopyProperties
{
    public bool CanCopy { get; init; }

    public bool BitwiseCopy { get; init; }

    public FunctionRef CopyMethod { get; init; }
}