using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Oxide.Compiler.IR.TypeRefs;
using Oxide.Compiler.IR.Types;
using Oxide.Compiler.Middleware.Lifetimes;

namespace Oxide.Compiler.IR.Instructions;

public class StaticCallInst : Instruction
{
    public BaseTypeRef TargetType { get; init; }

    public ConcreteTypeRef TargetImplementation { get; init; }

    public ConcreteTypeRef TargetMethod { get; init; }

    public ImmutableList<int> Arguments { get; init; }

    public int? ResultSlot { get; init; }

    public override void WriteIr(IrWriter writer)
    {
        writer.Write("staticcall ");
        if (ResultSlot.HasValue)
        {
            writer.Write($"${ResultSlot} ");
        }

        if (TargetType != null)
        {
            writer.WriteType(TargetType);
            writer.Write(" ");

            if (TargetImplementation != null)
            {
                writer.WriteType(TargetImplementation);
                writer.Write(" ");
            }
        }

        writer.Write($"{TargetMethod} ({string.Join(", ", Arguments.Select(x => $"${x}"))})");
    }

    public override InstructionEffects GetEffects(IrStore store)
    {
        // TODO: Detect borrowed returns

        var hasThis = false;
        var resultReferenceThis = false;
        var resultMutable = false;

        if (TargetType == null)
        {
            var func = store.Lookup<Function>(TargetMethod.Name);
        }
        else if (TargetType is ConcreteTypeRef concreteTypeRef)
        {
            hasThis = true;
            var func = store.LookupImplementation(
                concreteTypeRef,
                TargetImplementation,
                TargetMethod.Name.Parts.Single()
            );
            if (func.Function.ReturnType != null)
            {
                switch (func.Function.ReturnType)
                {
                    case BaseTypeRef baseTypeRef:
                        break;
                    case BorrowTypeRef borrowTypeRef:
                        resultReferenceThis = true;
                        resultMutable = borrowTypeRef.MutableRef;
                        break;
                    case PointerTypeRef pointerTypeRef:
                        break;
                    case ReferenceTypeRef referenceTypeRef:
                        break;
                    default:
                        throw new ArgumentOutOfRangeException();
                }
            }
        }
        else
        {
            throw new NotImplementedException("Finding effects of non-concrete types");
        }

        var reads = new List<InstructionEffects.ReadData>();
        var writes = new List<InstructionEffects.WriteData>();

        var first = true;
        foreach (var arg in Arguments)
        {
            reads.Add(InstructionEffects.ReadData.Access(arg, first && hasThis));
            first = false;
        }

        if (ResultSlot.HasValue)
        {
            if (resultReferenceThis)
            {
                writes.Add(InstructionEffects.WriteData.Borrow(ResultSlot.Value, Arguments.First(), resultMutable));
            }
            else
            {
                writes.Add(InstructionEffects.WriteData.New(ResultSlot.Value));
            }
        }

        return new InstructionEffects(
            reads.ToImmutableArray(),
            writes.ToImmutableArray()
        );
    }
}