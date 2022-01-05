using Oxide.Compiler.IR;
using Oxide.Compiler.IR.Types;
using Oxide.Compiler.Middleware.Lifetimes;
using Oxide.Compiler.Middleware.Usage;

namespace Oxide.Compiler.Middleware;

public class MiddlewareManager
{
    public IrStore Store { get; }

    public UsagePass Usage { get; }

    public LifetimePass Lifetime { get; }

    public MiddlewareManager(IrStore store)
    {
        Store = store;
        Usage = new UsagePass(this);
        Lifetime = new LifetimePass(this);
    }

    public void Process(IrUnit unit)
    {
        // TODO: Remove
        var mainFunc = unit.Lookup<Function>(QualifiedName.From("examples", "main"));
        mainFunc.IsExported = true;

        Lifetime.Analyse(unit);

        // TODO: Add pass management
        Usage.Analyse(unit);
    }
}