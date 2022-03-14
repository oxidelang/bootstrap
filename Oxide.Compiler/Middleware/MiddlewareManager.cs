using System;
using Oxide.Compiler.IR;
using Oxide.Compiler.IR.Types;
using Oxide.Compiler.Middleware.Lifetimes;
using Oxide.Compiler.Middleware.Usage;

namespace Oxide.Compiler.Middleware;

/// <summary>
/// Manages running middleware passes on Oxide IR
/// </summary>
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

    public void Process(IrUnit unit, string outputDest)
    {
        // TODO: Remove
        var mainFunc = unit.Lookup<Function>(QualifiedName.From("examples", "main"));
        mainFunc.IsExported = true;

        Lifetime.Analyse(unit, outputDest);

        var lifetimeChecker = new LifetimeCheckPass(this);
        lifetimeChecker.Analyse(unit);

        // TODO: Add pass management
        Usage.Analyse(unit);

        var refChecker = new RefCheckPass(this);
        refChecker.Analyse();

        var derivedRefChecker = new DerivedRefCheckPass(this);
        derivedRefChecker.Analyse();
    }
}