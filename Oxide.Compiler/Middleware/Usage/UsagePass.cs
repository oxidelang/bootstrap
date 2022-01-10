using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Oxide.Compiler.IR;
using Oxide.Compiler.IR.Instructions;
using Oxide.Compiler.IR.TypeRefs;
using Oxide.Compiler.IR.Types;

namespace Oxide.Compiler.Middleware.Usage;

public class UsagePass
{
    private MiddlewareManager Manager { get; }
    private IrStore Store => Manager.Store;

    public Dictionary<QualifiedName, UsedType> UsedTypes { get; private set; }

    public Dictionary<QualifiedName, UsedFunction> UsedFunctions { get; private set; }

    public UsagePass(MiddlewareManager manager)
    {
        Manager = manager;
    }

    public void Analyse(IrUnit unit)
    {
        Console.WriteLine("Analysing usage");
        UsedFunctions = new Dictionary<QualifiedName, UsedFunction>();
        UsedTypes = new Dictionary<QualifiedName, UsedType>();

        foreach (var objects in unit.Objects.Values)
        {
            if (objects is Function { IsExported: true } func)
            {
                if (!func.GenericParams.IsEmpty)
                {
                    throw new Exception("Cannot export function with generic parameters");
                }

                if (MarkFunction(func, ImmutableArray<TypeRef>.Empty))
                {
                    ProcessFunction(func, ImmutableArray<TypeRef>.Empty, null);
                }
            }
        }
    }

    private bool MarkFunction(Function func, ImmutableArray<TypeRef> generics)
    {
        if (!UsedFunctions.TryGetValue(func.Name, out var function))
        {
            function = new UsedFunction(func.Name);
            UsedFunctions.Add(func.Name, function);
        }

        return function.MarkVersion(generics);
    }

    private void ProcessFunction(Function func, ImmutableArray<TypeRef> generics, GenericContext parentContext)
    {
        Console.WriteLine($" - Processing function: {func.Name}");

        var functionContext = new GenericContext(
            parentContext,
            func.GenericParams,
            generics,
            parentContext?.ThisRef
        );

        if (func.IsExtern || !func.HasBody)
        {
            return;
        }

        var slotTypes = new Dictionary<int, TypeRef>();
        foreach (var scope in func.Scopes)
        {
            foreach (var slot in scope.Slots.Values)
            {
                var slotType = functionContext.ResolveRef(slot.Type);
                slotTypes.Add(slot.Id, slotType);
                MarkConcreteType((ConcreteTypeRef)slotType.GetBaseType());

                var copyProperties = Store.GetCopyProperties(slotType);
                if (copyProperties.CopyMethod != null)
                {
                    ProcessFunctionRef(
                        copyProperties.CopyMethod.TargetType,
                        copyProperties.CopyMethod.TargetImplementation,
                        copyProperties.CopyMethod.TargetMethod,
                        functionContext
                    );
                }
            }
        }

        foreach (var block in func.Blocks)
        {
            foreach (var instruction in block.Instructions)
            {
                if (instruction is StaticCallInst staticCallInst)
                {
                    ProcessFunctionRef(
                        staticCallInst.TargetType,
                        staticCallInst.TargetImplementation,
                        staticCallInst.TargetMethod,
                        functionContext
                    );
                }
                else if (instruction is MoveInst moveInst)
                {
                    var slotType = slotTypes[moveInst.SrcSlot];
                    var copyProperties = Store.GetCopyProperties(slotType);
                    if (copyProperties.CopyMethod != null)
                    {
                        ProcessFunctionRef(
                            copyProperties.CopyMethod.TargetType,
                            copyProperties.CopyMethod.TargetImplementation,
                            copyProperties.CopyMethod.TargetMethod,
                            functionContext
                        );
                    }
                }
            }
        }
    }

    private void ProcessFunctionRef(BaseTypeRef targetType, ConcreteTypeRef targetImp, ConcreteTypeRef targetMethod,
        GenericContext functionContext)
    {
        var mappedGenerics = targetMethod
            .GenericParams
            .Select(x => functionContext.ResolveRef(x))
            .ToImmutableArray();

        if (targetType != null)
        {
            var targetConcrete = (ConcreteTypeRef)functionContext
                .ResolveRef(targetType)
                .GetBaseType();
            var resolved = Store.LookupImplementation(
                targetConcrete,
                targetImp,
                targetMethod.Name.Parts.Single()
            );

            var impContext = new GenericContext(null, resolved.ImplementationGenerics, targetConcrete);

            var usedVersion = MarkConcreteType(targetConcrete);
            var usedImp = usedVersion.MarkImplementation(resolved.Interface);

            if (usedImp.MarkFunction(resolved.Function, mappedGenerics))
            {
                ProcessFunction(resolved.Function, mappedGenerics, impContext);
            }
        }
        else
        {
            var calledFunc = Store.Lookup<Function>(targetMethod.Name);
            if (calledFunc == null)
            {
                throw new Exception($"Failed to resolve call {targetMethod}");
            }

            if (MarkFunction(calledFunc, mappedGenerics))
            {
                ProcessFunction(calledFunc, mappedGenerics, null);
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