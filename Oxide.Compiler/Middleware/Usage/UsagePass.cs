using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Oxide.Compiler.IR;
using Oxide.Compiler.IR.Instructions;
using Oxide.Compiler.IR.TypeRefs;
using Oxide.Compiler.IR.Types;

namespace Oxide.Compiler.Middleware.Usage
{
    public class UsagePass
    {
        private MiddlewareManager Manager { get; }
        private IrStore Store => Manager.Store;

        public Dictionary<QualifiedName, UsedType> UsedTypes { get; private set; }
        public HashSet<QualifiedName> UsedFunctions { get; private set; }

        public UsagePass(MiddlewareManager manager)
        {
            Manager = manager;
        }

        public void Analyse(IrUnit unit)
        {
            Console.WriteLine("Analysing usage");
            UsedFunctions = new HashSet<QualifiedName>();
            UsedTypes = new Dictionary<QualifiedName, UsedType>();

            foreach (var objects in unit.Objects.Values)
            {
                if (objects is Function { IsExported: true } func)
                {
                    ProcessFunction(func);
                }
            }
        }

        private void ProcessFunction(Function func)
        {
            if (!UsedFunctions.Add(func.Name))
            {
                return;
            }

            Console.WriteLine($" - New function: {func.Name}");

            var functionContext = GenericContext.Default;

            if (func.IsExtern || !func.HasBody)
            {
                return;
            }

            foreach (var scope in func.Scopes)
            {
                foreach (var slot in scope.Slots.Values)
                {
                    MarkConcreteType((ConcreteTypeRef)functionContext.ResolveRef(slot.Type.GetBaseType()));
                }
            }

            foreach (var block in func.Blocks)
            {
                foreach (var instruction in block.Instructions)
                {
                    if (instruction is StaticCallInst staticCallInst)
                    {
                        if (staticCallInst.TargetType != null)
                        {
                            var targetConcrete = (ConcreteTypeRef)functionContext.ResolveRef(staticCallInst.TargetType);
                            var (imp, targetFunc) = Store.LookupImplementation(
                                targetConcrete,
                                staticCallInst.TargetImplementation,
                                staticCallInst.TargetMethod.Parts.Single()
                            );

                            var usedVersion = MarkConcreteType(targetConcrete);
                            var usedImp = usedVersion.MarkImplementation(imp.Interface);
                            usedImp.MarkFunction(targetFunc, ImmutableArray<TypeRef>.Empty);
                        }
                        else
                        {
                            var calledFunc = Store.Lookup<Function>(staticCallInst.TargetMethod);
                            if (calledFunc == null)
                            {
                                throw new Exception($"Failed to resolve call {staticCallInst.TargetMethod}");
                            }

                            ProcessFunction(calledFunc);
                        }
                    }
                }
            }
        }

        private UsedTypeVersion MarkConcreteType(ConcreteTypeRef tref)
        {
            if (!UsedTypes.TryGetValue(tref.Name, out var used))
            {
                used = new UsedType(tref.Name);
                UsedTypes.Add(tref.Name, used);
                Console.WriteLine($" - New type {tref.Name}");
            }

            return used.MarkGenericVariant(tref.GenericParams);
        }
    }
}