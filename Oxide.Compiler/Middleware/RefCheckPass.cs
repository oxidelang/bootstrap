using System;
using System.Collections.Generic;
using System.Linq;
using Oxide.Compiler.IR;
using Oxide.Compiler.IR.TypeRefs;
using Oxide.Compiler.IR.Types;
using Oxide.Compiler.Middleware.Lifetimes;
using Oxide.Compiler.Middleware.Usage;

namespace Oxide.Compiler.Middleware;

public class RefCheckPass
{
    private MiddlewareManager Manager { get; }
    private UsagePass Usage => Manager.Usage;
    private IrStore Store => Manager.Store;

    public RefCheckPass(MiddlewareManager manager)
    {
        Manager = manager;
    }

    public void Analyse()
    {
        Console.WriteLine("Checking ref usages");

        var detected = false;

        foreach (var usedType in Usage.UsedTypes.Values)
        {
            var type = Store.Lookup<OxType>(usedType.Name);
            if (usedType.Versions.Count == 0)
            {
                throw new Exception("Type has no used versions");
            }

            foreach (var version in usedType.Versions.Values)
            {
                detected |= CheckType(type, version);
            }
        }

        if (detected)
        {
            throw new Exception("Loop detected.");
        }
    }

    private bool CheckType(OxType type, UsedTypeVersion version)
    {
        var concreteType = new ConcreteTypeRef(version.Type.Name, version.Generics);
        Console.WriteLine($" - Checking {concreteType}");
        var context = new GenericContext(null, type.GenericParams, concreteType.GenericParams, concreteType);

        switch (type)
        {
            case Interface:
            case OxEnum:
            case PrimitiveType:
                return false;
            case Struct @struct:
            {
                var detected = false;
                foreach (var field in @struct.Fields)
                {
                    if (field.Unsafe)
                    {
                        continue;
                    }

                    var fieldType = context.ResolveRef(field.Type);

                    var steps = new List<LoopStep>();
                    var seenTypes = new HashSet<ConcreteTypeRef>();
                    seenTypes.Add(concreteType);

                    if (CheckCycle(seenTypes, fieldType, steps))
                    {
                        Console.WriteLine($"   - Field {field.Name}: Loop detected");
                        Console.Write($"     - this:{field.Name}");

                        LoopStep lastStep = null;
                        foreach (var step in steps.AsEnumerable().Reverse())
                        {
                            Console.Write($"->{step.Type.Name}:{step.Field}");
                            lastStep = step;
                        }

                        Console.Write($"->{((ConcreteTypeRef)lastStep.FieldType.GetBaseType()).Name}");

                        Console.WriteLine();
                        detected = true;
                    }
                }

                return detected;
            }
            case Variant variant:
            {
                var detected = false;

                foreach (var item in variant.Items)
                {
                    if (item.Content == null)
                    {
                        continue;
                    }

                    var itemRef = new ConcreteTypeRef(
                        new QualifiedName(true, variant.Name.Parts.Add(item.Name)),
                        concreteType.GenericParams
                    );

                    foreach (var field in item.Content.Fields)
                    {
                        if (field.Unsafe)
                        {
                            continue;
                        }

                        var fieldType = context.ResolveRef(field.Type);

                        var steps = new List<LoopStep>();
                        var seenTypes = new HashSet<ConcreteTypeRef>();
                        seenTypes.Add(concreteType);
                        seenTypes.Add(itemRef);

                        if (CheckCycle(seenTypes, fieldType, steps))
                        {
                            Console.WriteLine($"   - {item.Name} field {field.Name}: Loop detected");
                            Console.Write($"     - {item.Name}:{field.Name}");


                            LoopStep lastStep = null;
                            foreach (var step in steps.AsEnumerable().Reverse())
                            {
                                Console.Write($"->{step.Type.Name}:{step.Field}");
                                lastStep = step;
                            }

                            Console.Write($"->{((ConcreteTypeRef)lastStep.FieldType.GetBaseType()).Name}");

                            Console.WriteLine();
                            detected = true;
                        }
                    }
                }

                return detected;
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(type));
        }
    }

    private bool CheckCycle(HashSet<ConcreteTypeRef> seenTypes, TypeRef currentType, List<LoopStep> steps)
    {
        switch (currentType)
        {
            case ConcreteTypeRef concreteTypeRef:
                return CheckCycleConcrete(seenTypes, concreteTypeRef, steps);
            case BaseTypeRef baseTypeRef:
                throw new Exception("Unresolved type");
            case BorrowTypeRef borrowTypeRef:
                throw new Exception($"Unexpected borrow {borrowTypeRef} inside {currentType}");
            case DerivedRefTypeRef:
            case PointerTypeRef:
                // Ignore pointers
                break;
            case ReferenceTypeRef referenceTypeRef:
                // Ignore weak cycles
                if (referenceTypeRef.StrongRef)
                {
                    if (referenceTypeRef.InnerType is ConcreteTypeRef concreteInner)
                    {
                        if (seenTypes.Contains(concreteInner))
                        {
                            return true;
                        }

                        seenTypes.Add(concreteInner);
                    }

                    return CheckCycle(seenTypes, referenceTypeRef.InnerType, steps);
                }

                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(currentType));
        }

        return false;
    }

    private bool CheckCycleConcrete(HashSet<ConcreteTypeRef> seenTypes, ConcreteTypeRef currentType,
        List<LoopStep> steps)
    {
        var type = Store.Lookup<OxType>(currentType.Name);
        var context = new GenericContext(null, type.GenericParams, currentType.GenericParams, currentType);

        switch (type)
        {
            case Interface:
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
                    if (CheckCycle(seenTypes, fieldType, steps))
                    {
                        steps.Add(new LoopStep
                        {
                            Type = currentType,
                            Field = field.Name,
                            FieldType = fieldType
                        });
                        return true;
                    }
                }

                break;
            }
            case Variant variant:
            {
                foreach (var item in variant.Items)
                {
                    if (item.Content == null)
                    {
                        continue;
                    }

                    var itemRef = new ConcreteTypeRef(
                        new QualifiedName(true, variant.Name.Parts.Add(item.Name)),
                        currentType.GenericParams
                    );

                    foreach (var field in item.Content.Fields)
                    {
                        if (field.Unsafe)
                        {
                            continue;
                        }

                        var fieldType = context.ResolveRef(field.Type);
                        if (CheckCycle(seenTypes, fieldType, steps))
                        {
                            steps.Add(new LoopStep
                            {
                                Type = itemRef,
                                Field = field.Name,
                                FieldType = fieldType
                            });
                            return true;
                        }
                    }
                }

                break;
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(type));
        }

        return false;
    }

    private class LoopStep
    {
        public ConcreteTypeRef Type { get; init; }

        public string Field { get; init; }

        public TypeRef FieldType { get; init; }
    }
}