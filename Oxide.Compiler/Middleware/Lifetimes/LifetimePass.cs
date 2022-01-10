using System;
using System.Collections.Generic;
using System.Linq;
using ClosedXML.Excel;
using Microsoft.VisualBasic;
using Oxide.Compiler.IR;
using Oxide.Compiler.IR.Instructions;
using Oxide.Compiler.IR.TypeRefs;
using Oxide.Compiler.IR.Types;
using Oxide.Compiler.Utils;

namespace Oxide.Compiler.Middleware.Lifetimes;

public class LifetimePass
{
    private MiddlewareManager Manager { get; }
    private IrStore Store => Manager.Store;

    public Dictionary<Function, FunctionLifetime> FunctionLifetimes { get; private set; }

    private FunctionLifetime _functionLifetime;
    private Dictionary<int, Block> _blocks;
    private Dictionary<int, SlotDeclaration> _slots;

    private int _lastValueId;

    public LifetimePass(MiddlewareManager manager)
    {
        Manager = manager;
    }

    public void Analyse(IrUnit unit, string outputDest)
    {
        Console.WriteLine("Analysing lifetimes");

        FunctionLifetimes = new Dictionary<Function, FunctionLifetime>();

        foreach (var objects in unit.Objects.Values)
        {
            if (objects is Function func)
            {
                ProcessFunction(func);
            }
        }

        GenerateDebug($"{outputDest}\\lifetimes.xlsx");
    }

    private void ProcessFunction(Function func)
    {
        if (func.IsExtern || !func.HasBody)
        {
            return;
        }

        Console.WriteLine($" - Processing function {func.Name}");

        _functionLifetime = new FunctionLifetime
        {
            Entry = func.EntryBlock
        };
        FunctionLifetimes.Add(func, _functionLifetime);

        // Extract slots
        _slots = new Dictionary<int, SlotDeclaration>();
        foreach (var scope in func.Scopes)
        {
            foreach (var slot in scope.Slots.Values)
            {
                _slots.Add(slot.Id, slot);
            }
        }

        _blocks = new Dictionary<int, Block>();
        _lastValueId = 1;

        // Get effects
        foreach (var block in func.Blocks)
        {
            _blocks.Add(block.Id, block);
            _functionLifetime.IncomingBlocks.Add(block.Id, new HashSet<int>());

            InstructionLifetime last = null;
            foreach (var inst in block.Instructions)
            {
                var effects = inst.GetEffects();
                var lifetime = new InstructionLifetime
                {
                    Id = inst.Id,
                    Block = block,
                    Instruction = inst,
                    Previous = last,
                    Effects = effects,
                };

                // Ensure all slots exist
                foreach (var slotDef in _slots.Values)
                {
                    var slot = lifetime.GetSlot(slotDef.Id);
                    if (!block.Scope.CanAccessSlot(slotDef.Id))
                    {
                        slot.NoValue();
                    }
                }

                _functionLifetime.InstructionLifetimes.Add(inst.Id, lifetime);

                if (last != null)
                {
                    last.Next = lifetime;
                }

                last = lifetime;
            }
        }

        // Init
        foreach (var block in func.Blocks)
        {
            var effects = GetLifetime(block.LastInstruction).Effects;

            foreach (var target in effects.Jumps)
            {
                _functionLifetime.IncomingBlocks[target].Add(block.Id);
            }

            InitBlock(block, block.Id == func.EntryBlock);
        }

        // Process
        var updated = true;
        // var count 
        while (updated)
        // for (var i = 0; i < 50 && updated; i++)
        {
            Console.WriteLine("------- ");
            updated = false;

            foreach (var block in func.Blocks)
            {
                updated |= ProcessBlock(block);
            }
        }

        // Process requirements
        updated = true;
        while (updated)
        {
            updated = false;

            foreach (var pair in _functionLifetime.ValueRequirements)
            {
                // var value = pair.Key;
                var currentRequires = pair.Value.ToArray();
                foreach (var requires in currentRequires)
                {
                    foreach (var nestedRequires in _functionLifetime.ValueRequirements[requires])
                    {
                        if (pair.Value.Add(nestedRequires))
                        {
                            updated = true;
                        }
                    }
                }
            }
        }

        // Process borrows
        foreach (var block in func.Blocks)
        {
            ProcessBlockBorrows(block);
        }

        // Fill gaps
        foreach (var block in func.Blocks)
        {
            ProcessBlockMissing(block);
        }

        // Store list of active slots
        foreach (var block in func.Blocks)
        {
            foreach (var inst in block.Instructions)
            {
                var lifetime = GetLifetime(inst);

                foreach (var slotId in _slots.Keys)
                {
                    var slot = lifetime.GetSlot(slotId);
                    if (slot.Status != SlotStatus.NoValue)
                    {
                        lifetime.ActiveSlots.Add(slotId);
                    }
                }
            }
        }
    }

    private void InitBlock(Block block, bool entry)
    {
        // At entry, start with parameter values
        if (entry)
        {
            var lifetime = GetLifetime(block.FirstInstruction);

            foreach (var slot in _slots.Values)
            {
                var slotState = lifetime.GetSlot(slot.Id);
                if (slot.ParameterSource.HasValue)
                {
                    slotState.NewValue(new HashSet<int> { AllocateValue(slot.Id) });
                    lifetime.Set.Add(slot.Id);
                }
                else
                {
                    slotState.NoValue();
                }
            }
        }

        foreach (var inst in block.Instructions)
        {
            var lifetime = GetLifetime(inst);

            foreach (var write in lifetime.Effects.Writes)
            {
                var targetLifetime = write.TargetBlock.HasValue
                    ? GetLifetime(_blocks[write.TargetBlock.Value].FirstInstruction)
                    : lifetime;

                var slotState = targetLifetime.GetSlot(write.Slot);
                slotState.NewValue(new HashSet<int> { AllocateValue(write.Slot) });

                if (write.ReferenceSource.HasValue)
                {
                    slotState.MarkBorrowed(write.ReferenceSource.Value, write.ReferenceField, write.ReferenceMutable);
                }

                if (!write.TargetBlock.HasValue)
                {
                    lifetime.Overwritten.Add(write.Slot);
                }

                targetLifetime.Set.Add(write.Slot);
            }
        }
    }

    private bool ProcessBlock(Block block)
    {
        var updated = false;

        foreach (var inst in block.Instructions)
        {
            var lifetime = GetLifetime(inst);

            foreach (var read in lifetime.Effects.Reads)
            {
                updated |= TraceRead(lifetime, read);
            }

            foreach (var write in lifetime.Effects.Writes)
            {
                updated |= TraceWriteReference(lifetime, write);
            }
        }

        if (updated)
        {
            Console.WriteLine(block.Id + " was updated");
        }

        return updated;
    }

    private bool TraceWriteReference(InstructionLifetime lifetime, InstructionEffects.WriteData write)
    {
        var targetLifetime = write.TargetBlock.HasValue
            ? GetLifetime(_blocks[write.TargetBlock.Value].FirstInstruction)
            : lifetime;

        var slot = targetLifetime.GetSlot(write.Slot);
        if (slot.Status != SlotStatus.Active || !write.ReferenceSource.HasValue) return false;

        var referenceSource = write.ReferenceSource.Value;

        if (!slot.Borrowed)
        {
            return false;
        }

        var slotValue = slot.Values.Single();
        var reqs = _functionLifetime.ValueRequirements[slotValue];

        SlotState refSlot;
        if (lifetime.Overwritten.Contains(referenceSource))
        {
            if (lifetime.Previous == null)
            {
                throw new Exception("No previous instruction to trace write reference from");
            }

            refSlot = lifetime.Previous.GetSlot(referenceSource);
        }
        else
        {
            refSlot = lifetime.GetSlot(referenceSource);
        }

        if (refSlot.Status == SlotStatus.Active && refSlot.Values.Count > 0)
        {
            return reqs.AddRange(refSlot.Values);
        }
        else
        {
            // throw new Exception("Missing value");
        }

        return false;
    }

    private bool TraceRead(InstructionLifetime lifetime, InstructionEffects.ReadData read)
    {
        var isCopy = IsSlotCopy(read.Slot);
        var currentState = lifetime.GetSlot(read.Slot);

        var visited = new HashSet<(int, int)>();
        var updated = TraceReadInner(lifetime, read.Slot, true, visited, "");

        if (updated)
        {
            Console.WriteLine("  > " + lifetime.Id + " was updated");
        }

        if (currentState.Status != SlotStatus.Active)
        {
            return updated;
        }

        if (read.Moved && !isCopy)
        {
            currentState.Move();
        }

        return updated;
    }

    private bool TraceReadInner(InstructionLifetime lifetime, int slot, bool first, HashSet<(int, int)> visited,
        string order)
    {
        var updated = false;
        var currentState = lifetime.GetSlot(slot);
        // var checkProcessed = !first || !lifetime.Overwritten.Contains(slot);

        // Check if this slot has already been processed

        if (currentState.Status == SlotStatus.Error)
        {
            return false;
        }

        if (lifetime.Set.Contains(slot) && (!first || !lifetime.Overwritten.Contains(slot)))
        {
            return false;
        }

        // if (currentState.Status != SlotStatus.Unprocessed && checkProcessed)
        // {
        //     return;
        // }

        if (lifetime.Previous != null)
        {
            // updated |= 
                var prev = TraceReadInner(lifetime.Previous, slot, false, visited, order);

                if (prev)
                {
                    Console.WriteLine("  >>>> " + lifetime.Previous.Id);
                    updated = true;
                }
                
            // if (currentState.Status != SlotStatus.Unprocessed && checkProcessed)
            // {
            //     return;
            // }

            var previousSlot = lifetime.Previous.GetSlot(slot);

            if (previousSlot.Status == SlotStatus.Active)
            {
                updated |= currentState.Propagate(previousSlot);
            }
            else if (previousSlot.Status != SlotStatus.Unprocessed)
            {
                updated |= currentState.Error("previous");
            }
        }
        else
        {
            var incomingStates = new List<SlotState>();

            var skipped = "";

            foreach (var incBlock in _functionLifetime.IncomingBlocks[lifetime.Block.Id])
            {
                var pathKey = (lifetime.Block.Id, incBlock);

                // Don't revisit blocks
                if (visited.Contains(pathKey))
                {
                    skipped = $"{skipped} {incBlock}:visited ";
                    continue;
                }

                var otherBlock = _blocks[incBlock];
                var otherLifetime = GetLifetime(otherBlock.LastInstruction);

                visited.Add(pathKey);
                var prev = TraceReadInner(otherLifetime, slot, false, visited,
                    $"{order} {pathKey.Id}-{pathKey.incBlock}");
                if (prev)
                {
                    Console.WriteLine("  >>>> " + otherLifetime.Id);
                    updated = true;
                }
                visited.Remove(pathKey);

                // if (currentState.Status != SlotStatus.Unprocessed && checkProcessed)
                // {
                //     return;
                // }

                var otherSlot = otherLifetime.GetSlot(slot);
                if (otherSlot.Status == SlotStatus.Unprocessed)
                {
                    skipped = $"{skipped} {incBlock}:unprocessed";
                    continue;
                    // throw new Exception("Unexpected state");
                }

                incomingStates.Add(otherSlot);
            }

            if (incomingStates.Count == 0)
            {
                if (visited.Count == 0)
                {
                    updated |= currentState.Error($"No Incoming {skipped} {order}");
                }
            }
            else if (incomingStates.Any(x => x.Status != SlotStatus.Active))
            {
                updated |= currentState.Error(
                    $"Invalid incoming: {string.Join(", ", incomingStates.Select(x => $"{x.Instruction.Block.Id}:{x.Status}").ToArray())}"
                );
            }
            else
            {
                var firstState = incomingStates[0];
                var values = new HashSet<int>();
                values.UnionWith(firstState.Values);

                for (var i = 1; i < incomingStates.Count; i++)
                {
                    var otherState = incomingStates[i];

                    if (firstState.Borrowed)
                    {
                        if (!firstState.Matches(otherState, true))
                        {
                            return updated || currentState.Error("Mismatch");
                        }
                    }
                    else
                    {
                        values.UnionWith(otherState.Values);
                    }
                }

                if (firstState.Borrowed)
                {
                    updated |= currentState.Propagate(firstState);
                }
                else
                {
                    updated |= currentState.NewValue(values);
                }
            }
        }

        return updated;
    }

    private void ProcessBlockBorrows(Block block)
    {
        foreach (var inst in block.Instructions)
        {
            var lifetime = GetLifetime(inst);

            foreach (var slotId in _slots.Keys)
            {
                var slot = lifetime.GetSlot(slotId);
                if (slot.Status == SlotStatus.Active)
                {
                    if (!slot.Borrowed)
                    {
                        continue;
                    }

                    var slotValue = slot.Values.Single();

                    foreach (var required in _functionLifetime.ValueRequirements[slotValue])
                    {
                        var requiredSlot = _functionLifetime.ValueMap[required];
                        var visited = new HashSet<(int, int)>();
                        TraceReadInner(lifetime, requiredSlot, true, visited, "");
                    }
                }
            }
        }
    }

    private void ProcessBlockMissing(Block block)
    {
        foreach (var inst in block.Instructions)
        {
            var lifetime = GetLifetime(inst);

            foreach (var slotId in _slots.Keys)
            {
                var slot = lifetime.GetSlot(slotId);
                if (slot.Status == SlotStatus.Unprocessed)
                {
                    slot.NoValue();
                }
            }
        }
    }

    private int AllocateValue(int slot)
    {
        var newId = _lastValueId++;
        _functionLifetime.ValueMap.Add(newId, slot);
        _functionLifetime.ValueRequirements.Add(newId, new HashSet<int>());
        return newId;
    }

    private InstructionLifetime GetLifetime(Instruction inst)
    {
        return _functionLifetime.InstructionLifetimes[inst.Id];
    }

    private bool IsSlotCopy(int slot)
    {
        var slotDec = _slots[slot];
        return IsTypeCopy(slotDec.Type);
    }

    private bool IsTypeCopy(TypeRef typeRef)
    {
        switch (typeRef)
        {
            case ConcreteTypeRef concreteTypeRef:
            {
                var type = Store.Lookup(concreteTypeRef.Name);

                switch (type)
                {
                    case Function function:
                    case Interface @interface:
                        throw new NotImplementedException();
                    case PrimitiveType primitiveType:
                        return true;
                    case Struct @struct:
                        return false;
                    case Variant variant:
                        // throw new NotImplementedException();
                        return false;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(type));
                }
            }
            case DerivedTypeRef:
                throw new NotImplementedException();
            case GenericTypeRef:
                return false;
            case ThisTypeRef:
                return false;
            case BorrowTypeRef:
            case PointerTypeRef:
            case ReferenceTypeRef:
                return true;
            default:
                throw new ArgumentOutOfRangeException(nameof(typeRef));
        }
    }

    public void GenerateDebug(string fileName)
    {
        using var workbook = new XLWorkbook();

        foreach (var pair in FunctionLifetimes)
        {
            var func = pair.Key;
            var funcLifetime = pair.Value;

            var worksheet = workbook.Worksheets.Add($"Func {func.Name.ToString().Replace(':', '_')}");

            worksheet.Cell(1, 1).SetText("Id");
            worksheet.Cell(1, 2).SetText("Inst");
            worksheet.Cell(1, 3).SetText("Reads");
            worksheet.Cell(1, 4).SetText("Writes");
            worksheet.Cell(1, 5).SetText("Jumps");

            var slotIds = new HashSet<int>();
            foreach (var scope in func.Scopes)
            {
                foreach (var slot in scope.Slots.Values)
                {
                    slotIds.Add(slot.Id);
                }
            }

            foreach (var slot in slotIds)
            {
                worksheet.Cell(1, 6 + slot).SetText($"Slot {slot}");
            }

            var row = 2;

            foreach (var block in func.Blocks)
            {
                worksheet.Cell(row, 1).SetText($"Block {block.Id}");
                worksheet.Cell(row, 2)
                    .SetText("Incoming=" + string.Join(",", funcLifetime.IncomingBlocks[block.Id].ToArray()));
                row++;

                foreach (var inst in block.Instructions)
                {
                    var lifetime = funcLifetime.InstructionLifetimes[inst.Id];
                    var effects = lifetime.Effects;

                    worksheet.Cell(row, 1).SetText($"{inst.Id}");

                    var writer = new IrWriter();
                    inst.WriteIr(writer);
                    worksheet.Cell(row, 2).SetText(writer.Generate());

                    worksheet.Cell(row, 3).SetText(string.Join(",", effects.Reads.Select(x => x.Slot)));
                    worksheet.Cell(row, 4).SetText(string.Join(",", effects.Writes.Select(x => x.Slot)));
                    worksheet.Cell(row, 5).SetText(string.Join(',', effects.Jumps));

                    foreach (var slot in slotIds)
                    {
                        var slotState = lifetime.GetSlot(slot);
                        worksheet.Cell(row, 6 + slot).SetText(slotState.ToDebugString());
                    }

                    row++;
                }
            }
        }

        workbook.SaveAs(fileName);
    }
}