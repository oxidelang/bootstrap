using System;
using Oxide.Compiler.IR;
using Oxide.Compiler.IR.Types;
using Oxide.Compiler.Middleware.Usage;

namespace Oxide.Compiler.Middleware
{
    public class MiddlewareManager
    {
        public IrStore Store { get; }

        public UsagePass Usage { get; }

        public MiddlewareManager(IrStore store)
        {
            Store = store;
            Usage = new UsagePass(this);
        }

        public void Process(IrUnit unit)
        {
            // TODO: Remove
            var mainFunc = unit.Lookup<Function>(QualifiedName.From("examples", "main"));
            mainFunc.IsExported = true;

            // TODO: Add pass management
            Usage.Analyse(unit);
        }
    }
}