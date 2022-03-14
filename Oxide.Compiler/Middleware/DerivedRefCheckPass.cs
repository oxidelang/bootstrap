using System;
using System.Collections.Generic;
using System.Linq;
using Oxide.Compiler.IR;
using Oxide.Compiler.IR.Instructions;
using Oxide.Compiler.IR.TypeRefs;
using Oxide.Compiler.IR.Types;
using Oxide.Compiler.Middleware.Usage;

namespace Oxide.Compiler.Middleware;

/// <summary>
/// Checks that derived references are not created from types that contain derived references
/// </summary>
public class DerivedRefCheckPass
{
    private MiddlewareManager Manager { get; }
    private UsagePass Usage => Manager.Usage;
    private IrStore Store => Manager.Store;

    private Dictionary<TypeRef, bool> _checkedTypes;

    public DerivedRefCheckPass(MiddlewareManager manager)
    {
        Manager = manager;
    }

    public void Analyse()
    {
        Console.WriteLine("Checking derived ref usages");
        _checkedTypes = new Dictionary<TypeRef, bool>();

        foreach (var usedType in Usage.UsedTypes.Values)
        {
            CreateType(usedType);
        }

        foreach (var usedFunc in Usage.UsedFunctions.Values)
        {
            var func = Store.Lookup<Function>(usedFunc.Name);

            foreach (var version in usedFunc.Versions)
            {
                var key = new FunctionRef
                {
                    TargetMethod = new ConcreteTypeRef(usedFunc.Name, version)
                };

                var context = new GenericContext(null, func.GenericParams, version, null);

                CheckFunction(key, func, context);
            }
        }
    }

    private void CreateType(UsedType usedType)
    {
        var type = Store.Lookup<OxType>(usedType.Name);
        if (usedType.Versions.Count == 0)
        {
            throw new Exception("Type has no used versions");
        }

        foreach (var version in usedType.Versions.Values)
        {
            var concreteType = new ConcreteTypeRef(usedType.Name, version.Generics);
            var context = new GenericContext(null, type.GenericParams, version.Generics, concreteType);

            if (version.DefaultImplementation != null)
            {
                CheckImplementation(context, version.DefaultImplementation);
            }

            foreach (var imp in version.Implementations.Values)
            {
                CheckImplementation(context, imp);
            }
        }
    }

    private void CheckImplementation(GenericContext context, UsedImplementation usedImp)
    {
        foreach (var usedFunc in usedImp.Functions.Values)
        {
            var resolved = Store.LookupImplementation(
                context.ThisRef,
                usedImp.Interface,
                usedFunc.Name.Parts.Single()
            );

            var impContext = new GenericContext(context, resolved.ImplementationGenerics, context.ThisRef);

            foreach (var version in usedFunc.Versions)
            {
                var key = new FunctionRef
                {
                    TargetType = context.ThisRef,
                    TargetImplementation = resolved.Interface,
                    TargetMethod = new ConcreteTypeRef(usedFunc.Name, version),
                };

                var funcContext = new GenericContext(
                    impContext,
                    resolved.Function.GenericParams,
                    version,
                    impContext.ThisRef
                );

                CheckFunction(key, resolved.Function, funcContext);
            }
        }
    }


    private void CheckFunction(FunctionRef key, Function func, GenericContext context)
    {
        if (func.IsExtern)
        {
            return;
        }

        Console.WriteLine($" - Checking function {key.ToPrettyString()}");

        // Extract slots
        var slots = new Dictionary<int, SlotDeclaration>();
        foreach (var scope in func.Scopes)
        {
            foreach (var slot in scope.Slots.Values)
            {
                slots.Add(slot.Id, slot);
            }
        }

        foreach (var block in func.Blocks)
        {
            // Don't check unsafe blocks
            if (block.Scope.Unsafe)
            {
                continue;
            }

            foreach (var instruction in block.Instructions)
            {
                if (instruction is not RefDeriveInst refDeriveInst)
                {
                    continue;
                }

                var sourceSlot = slots[refDeriveInst.SourceSlot];
                var sourceType = context.ResolveRef(sourceSlot.Type);

                // Only check start of derive chain
                if (sourceType is not ReferenceTypeRef referenceTypeRef)
                {
                    continue;
                }

                if (ContainsDerivedRef(referenceTypeRef))
                {
                    throw new Exception(
                        $"It is unsafe to derive a reference from slot \"{sourceSlot.Name}\" of type \"{referenceTypeRef.ToPrettyString()}\" as it contains derived references which may cause a loop"
                    );
                }
            }
        }
    }

    private bool ContainsDerivedRef(TypeRef typeRef)
    {
        if (_checkedTypes.TryGetValue(typeRef, out var hasDref))
        {
            return hasDref;
        }

        hasDref = false;
        switch (typeRef)
        {
            case ConcreteTypeRef concreteTypeRef:
                hasDref = ContainsDerivedRefConcrete(concreteTypeRef);
                break;
            case BaseTypeRef:
                throw new Exception("Unresolved type");
            case BorrowTypeRef borrowTypeRef:
                hasDref = ContainsDerivedRef(borrowTypeRef.InnerType);
                break;
            case DerivedRefTypeRef derivedRefTypeRef:
                hasDref = derivedRefTypeRef.StrongRef;
                break;
            case PointerTypeRef pointerTypeRef:
                hasDref = ContainsDerivedRef(pointerTypeRef.InnerType);
                break;
            case ReferenceTypeRef referenceTypeRef:
                if (referenceTypeRef.StrongRef)
                {
                    hasDref = ContainsDerivedRef(referenceTypeRef.InnerType);
                }

                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(typeRef));
        }

        _checkedTypes.Add(typeRef, hasDref);
        return hasDref;
    }

    private bool ContainsDerivedRefConcrete(ConcreteTypeRef concreteType)
    {
        var type = Store.Lookup(concreteType.Name);
        var context = new GenericContext(null, type.GenericParams, concreteType.GenericParams, concreteType);

        switch (type)
        {
            case Interface:
            case OxEnum:
            case PrimitiveType:
                return false;
            case Struct @struct:
            {
                foreach (var field in @struct.Fields)
                {
                    if (field.Unsafe)
                    {
                        continue;
                    }

                    var fieldType = context.ResolveRef(field.Type);

                    if (ContainsDerivedRef(fieldType))
                    {
                        return true;
                    }
                }

                return false;
            }
            case Variant variant:
            {
                foreach (var item in variant.Items)
                {
                    if (item.Content == null)
                    {
                        continue;
                    }

                    // var itemRef = new ConcreteTypeRef(
                    //     new QualifiedName(true, variant.Name.Parts.Add(item.Name)),
                    //     concreteType.GenericParams
                    // );

                    foreach (var field in item.Content.Fields)
                    {
                        if (field.Unsafe)
                        {
                            continue;
                        }

                        var fieldType = context.ResolveRef(field.Type);

                        if (ContainsDerivedRef(fieldType))
                        {
                            return true;
                        }
                    }
                }

                return false;
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(type));
        }
    }
}