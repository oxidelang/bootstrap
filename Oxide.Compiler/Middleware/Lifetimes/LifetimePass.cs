using System;
using System.Collections.Generic;
using System.Linq;
using ClosedXML.Excel;
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

    private FunctionLifetime _functionLifetime;
    private Dictionary<int, InstructionLifetime> _instructionLifetimes;

    private Function _currentFunc;

    private Dictionary<int, int> _valueMap;
    private Dictionary<int, HashSet<int>> _valueRequirements;
    private Dictionary<int, Block> _blocks;
    private Dictionary<int, SlotDeclaration> _slots;

    private int _lastValueId;

    public LifetimePass(MiddlewareManager manager)
    {
        Manager = manager;
    }

    public void Analyse(IrUnit unit)
    {
        Console.WriteLine("Analysing usage");

        foreach (var objects in unit.Objects.Values)
        {
            if (objects is Function func)
            {
                ProcessFunction(func);
            }
        }
    }

    private void ProcessFunction(Function func)
    {
        if (func.IsExtern || !func.HasBody || func.Name.Parts[0] != "examples")
        {
            return;
        }

        Console.WriteLine($" - Processing function: {func.Name}");
        _currentFunc = func;

        _functionLifetime = new FunctionLifetime
        {
            Entry = func.EntryBlock
        };

        _valueMap = new Dictionary<int, int>();
        _valueRequirements = new Dictionary<int, HashSet<int>>();

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
        _instructionLifetimes = new Dictionary<int, InstructionLifetime>();
        _lastValueId = 1;

        // Get effects
        foreach (var block in func.Blocks)
        {
            _blocks.Add(block.Id, block);

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

                _instructionLifetimes.Add(inst.Id, lifetime);

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
                var lifetime = GetBlockLifetime(target);
                lifetime.IncomingBlocks.Add(block.Id);
            }

            InitBlock(block, block.Id == func.EntryBlock);
        }

        // Process
        foreach (var block in func.Blocks)
        {
            ProcessBlock(block);
        }

        // Process requirements
        var updated = true;
        while (updated)
        {
            Console.WriteLine("Requirement round");
            updated = false;

            foreach (var pair in _valueRequirements)
            {
                // var value = pair.Key;
                var currentRequires = pair.Value.ToArray();
                foreach (var requires in currentRequires)
                {
                    foreach (var nestedRequires in _valueRequirements[requires])
                    {
                        if (pair.Value.Add(nestedRequires))
                        {
                            updated = true;
                        }
                    }
                }
            }
        }

        foreach (var pair in _valueRequirements)
        {
            if (pair.Value.Count > 0)
            {
                Console.WriteLine($" {pair.Key}: {(string.Join(", ", pair.Value))}");
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


        GenerateDebug(
            "C:\\Users\\chand\\Projects\\Oxide\\bootstrap\\Oxide.Compiler\\examples\\lists\\lifetimes\\p1.xlsx");
    }

    private void InitBlock(Block block, bool entry)
    {
        Console.WriteLine($"Block {block.Id}");

        // At entry, start with parameter values
        if (entry)
        {
            var lifetime = GetLifetime(block.FirstInstruction);

            foreach (var slot in _slots.Values)
            {
                var slotState = lifetime.GetSlot(slot.Id);
                if (slot.ParameterSource.HasValue)
                {
                    slotState.NewValue(AllocateValue(slot.Id));
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
                slotState.NewValue(AllocateValue(write.Slot));

                if (write.ReferenceSource.HasValue)
                {
                    slotState.MarkBorrowed(write.ReferenceSource.Value, write.ReferenceField, write.ReferenceMutable);
                }

                if (!write.TargetBlock.HasValue)
                {
                    lifetime.Overwritten.Add(write.Slot);
                }
            }
        }
    }

    private void ProcessBlock(Block block)
    {
        Console.WriteLine($"Process Block {block.Id}");

        foreach (var inst in block.Instructions)
        {
            var lifetime = GetLifetime(inst);

            foreach (var read in lifetime.Effects.Reads)
            {
                TraceRead(lifetime, read);
            }

            foreach (var write in lifetime.Effects.Writes)
            {
                TraceWriteReference(lifetime, write);
            }
        }
    }

    private void TraceWriteReference(InstructionLifetime lifetime, InstructionEffects.WriteData write)
    {
        var targetLifetime = write.TargetBlock.HasValue
            ? GetLifetime(_blocks[write.TargetBlock.Value].FirstInstruction)
            : lifetime;

        var slot = targetLifetime.GetSlot(write.Slot);
        if (slot.Status != SlotStatus.Active || !write.ReferenceSource.HasValue) return;

        var referenceSource = write.ReferenceSource.Value;

        var reqs = _valueRequirements[slot.Value];

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

        if (refSlot.Status == SlotStatus.Active && refSlot.Value != 0)
        {
            reqs.Add(refSlot.Value);
        }
        else
        {
            throw new Exception("Missing value");
        }
    }

    private void TraceRead(InstructionLifetime lifetime, InstructionEffects.ReadData read)
    {
        var isCopy = IsSlotCopy(read.Slot);
        var currentState = lifetime.GetSlot(read.Slot);

        var visited = new HashSet<int>();
        TraceReadInner(lifetime, read.Slot, true, visited);

        if (currentState.Status == SlotStatus.Error)
        {
            return;
        }

        if (currentState.Status != SlotStatus.Active)
        {
            return;
        }

        if (read.Moved && !isCopy)
        {
            currentState.Move();
        }
    }

    private void TraceReadInner(InstructionLifetime lifetime, int slot, bool first, HashSet<int> visited)
    {
        var currentState = lifetime.GetSlot(slot);
        if (currentState.Status == SlotStatus.Unused)
        {
            currentState.MarkUsed();
            return;
        }

        // Check if this slot has already been processed
        if (currentState.Status != SlotStatus.Unprocessed && (!first || !lifetime.Overwritten.Contains(slot)))
        {
            return;
        }

        if (lifetime.Previous != null)
        {
            TraceReadInner(lifetime.Previous, slot, false, visited);

            if (currentState.Status != SlotStatus.Active)
            {
                var previousSlot = lifetime.Previous.GetSlot(slot);

                switch (previousSlot.Status)
                {
                    case SlotStatus.Active:
                        currentState.Propagate(previousSlot);
                        break;
                    case SlotStatus.Error:
                    case SlotStatus.Moved:
                    case SlotStatus.NoValue:
                        currentState.Error();
                        break;
                    case SlotStatus.Unused:
                    case SlotStatus.Unprocessed:
                        // throw new Exception("Unexpected state");
                        currentState.Error();
                        break;
                    default:
                        throw new ArgumentOutOfRangeException();
                }
            }
        }
        else
        {
            var blockLifetime = GetBlockLifetime(lifetime.Block.Id);
            var incomingStates = new List<SlotState>();

            foreach (var incBlock in blockLifetime.IncomingBlocks)
            {
                if (visited.Contains(incBlock))
                {
                    Console.WriteLine($"Skipping {incBlock} due to already visiting");
                    continue;
                }

                var otherBlock = _blocks[incBlock];
                var otherLifetime = GetLifetime(otherBlock.LastInstruction);

                visited.Add(incBlock);
                TraceReadInner(otherLifetime, slot, false, visited);
                visited.Remove(incBlock);

                var otherSlot = otherLifetime.GetSlot(slot);
                switch (otherSlot.Status)
                {
                    case SlotStatus.Active:
                    case SlotStatus.Error:
                    case SlotStatus.Moved:
                    case SlotStatus.NoValue:
                        incomingStates.Add(otherSlot);
                        break;
                    case SlotStatus.Unused:
                    case SlotStatus.Unprocessed:
                        throw new Exception("Unexpected state");
                    // currentState.Error();
                    // break;
                    default:
                        throw new ArgumentOutOfRangeException();
                }
            }

            if (incomingStates.Count == 0)
            {
                Console.WriteLine("No incoming");
                currentState.Error();
            }
            else if (incomingStates.Any(x => x.Status != SlotStatus.Active))
            {
                currentState.Error();
            }
            else
            {
                var firstState = incomingStates[0];
                var matches = true;
                var newValue = false;

                for (var i = 1; i < incomingStates.Count; i++)
                {
                    var otherState = incomingStates[i];

                    if (firstState.Borrowed)
                    {
                        if (!firstState.Matches(otherState, true))
                        {
                            matches = false;
                        }
                    }
                    else
                    {
                        if (otherState.Value != firstState.Value)
                        {
                            newValue = true;
                        }
                    }
                }

                if (newValue)
                {
                    if (currentState.Status != SlotStatus.Active)
                    {
                        currentState.NewValue(AllocateValue(slot));
                    }
                }
                else if (matches)
                {
                    if (currentState.Status != SlotStatus.Active)
                    {
                        currentState.Propagate(firstState);
                    }
                }
                else
                {
                    currentState.Error();
                }
            }
        }
    }

    private void ProcessBlockBorrows(Block block)
    {
        Console.WriteLine($"Block Borrows {block.Id}");

        foreach (var inst in block.Instructions)
        {
            var lifetime = GetLifetime(inst);

            foreach (var slotId in _slots.Keys)
            {
                var slot = lifetime.GetSlot(slotId);
                if (slot.Status == SlotStatus.Active)
                {
                    foreach (var required in _valueRequirements[slot.Value])
                    {
                        var requiredSlot = _valueMap[required];
                        var visited = new HashSet<int>();
                        TraceReadInner(lifetime, requiredSlot, true, visited);
                    }
                }
            }
        }
    }

    private void ProcessBlockMissing(Block block)
    {
        Console.WriteLine($"Block Missing {block.Id}");

        foreach (var inst in block.Instructions)
        {
            var lifetime = GetLifetime(inst);

            foreach (var slotId in _slots.Keys)
            {
                var slot = lifetime.GetSlot(slotId);
                if (slot.Status == SlotStatus.Unprocessed)
                {
                    var visited = new HashSet<int>();
                    FillMissingInner(lifetime, slotId, visited);
                }
            }
        }
    }

    private void FillMissingInner(InstructionLifetime lifetime, int slot, HashSet<int> visited)
    {
        var currentState = lifetime.GetSlot(slot);
        if (currentState.Status != SlotStatus.Unprocessed)
        {
            return;
        }

        if (lifetime.Previous != null)
        {
            var previousSlot = lifetime.Previous.GetSlot(slot);
            if (previousSlot.Status == SlotStatus.Unprocessed)
            {
                FillMissingInner(lifetime.Previous, slot, visited);
                
                if (currentState.Status != SlotStatus.Unprocessed)
                {
                    return;
                }
            }

            switch (previousSlot.Status)
            {
                case SlotStatus.Unprocessed:
                    // throw new Exception("Unexpected state");
                    return;
                case SlotStatus.Moved:
                case SlotStatus.NoValue:
                    currentState.NoValue();
                    break;
                case SlotStatus.Active:
                    if (previousSlot.Borrowed)
                    {
                        currentState.NoValue();
                    }
                    else
                    {
                        currentState.Propagate(previousSlot, true);
                        currentState.Status = SlotStatus.Unused;
                    }

                    break;
                case SlotStatus.Unused:
                    currentState.Propagate(previousSlot, true);
                    currentState.Status = SlotStatus.Unused;
                    break;
                case SlotStatus.Error:
                    currentState.Error();
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }
        else
        {
            var blockLifetime = GetBlockLifetime(lifetime.Block.Id);
            var incomingStates = new List<SlotState>();

            foreach (var incBlock in blockLifetime.IncomingBlocks)
            {
                if (visited.Contains(incBlock))
                {
                    Console.WriteLine($"Skipping {incBlock} due to already visiting");
                    continue;
                }

                var otherBlock = _blocks[incBlock];
                var otherLifetime = GetLifetime(otherBlock.LastInstruction);

                var otherSlot = otherLifetime.GetSlot(slot);
                if (otherSlot.Status == SlotStatus.Unprocessed)
                {
                    visited.Add(incBlock);
                    FillMissingInner(otherLifetime, slot, visited);
                    visited.Remove(incBlock);
                    
                    if (currentState.Status != SlotStatus.Unprocessed)
                    {
                        return;
                    }
                }

                // if (otherSlot.Status == SlotStatus.Unprocessed)
                // {
                //     // throw new Exception("Unexpected state");
                // }

                if (otherSlot.Status != SlotStatus.Unprocessed)
                {
                    incomingStates.Add(otherSlot);
                }
            }

            if (incomingStates.Count == 0)
            {
                // Console.WriteLine("No incoming");
                // currentState.Error();
                return;
            }
            else if (incomingStates.Any(x => x.Borrowed))
            {
                currentState.NoValue();
            }
            else
            {
                var firstState = incomingStates[0];
                var matches = true;

                for (var i = 1; i < incomingStates.Count; i++)
                {
                    var otherState = incomingStates[i];
                    if (otherState.Value != firstState.Value || otherState.Status != firstState.Status)
                    {
                        matches = false;
                    }
                }

                if (matches)
                {
                    switch (firstState.Status)
                    {
                        case SlotStatus.Unprocessed:
                            throw new Exception("Unexpected state");
                        case SlotStatus.Moved:
                        case SlotStatus.NoValue:
                            currentState.NoValue();
                            break;
                        case SlotStatus.Active:
                            if (firstState.Borrowed)
                            {
                                currentState.NoValue();
                            }
                            else
                            {
                                currentState.Propagate(firstState, true);
                                currentState.Status = SlotStatus.Unused;
                            }

                            break;
                        case SlotStatus.Unused:
                            currentState.Propagate(firstState, true);
                            currentState.Status = SlotStatus.Unused;
                            break;
                        case SlotStatus.Error:
                            currentState.Error();
                            break;
                        default:
                            throw new ArgumentOutOfRangeException();
                    }
                }
                else
                {
                    currentState.NoValue();
                }
            }
        }
    }

    private int AllocateValue(int slot)
    {
        var newId = _lastValueId++;
        _valueMap.Add(newId, slot);
        _valueRequirements.Add(newId, new HashSet<int>());
        return newId;
    }

    private BlockLifetime GetBlockLifetime(int id)
    {
        return _functionLifetime.GetBlock(id);
    }

    private InstructionLifetime GetLifetime(Instruction inst)
    {
        return _instructionLifetimes[inst.Id];
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
        var worksheet = workbook.Worksheets.Add("Sample Sheet");

        worksheet.Cell(1, 1).SetText("Id");
        worksheet.Cell(1, 2).SetText("Inst");
        worksheet.Cell(1, 3).SetText("Reads");
        worksheet.Cell(1, 4).SetText("Writes");
        worksheet.Cell(1, 5).SetText("Jumps");

        foreach (var slot in _slots.Values)
        {
            worksheet.Cell(1, 6 + slot.Id).SetText($"Slot {slot.Id}");
        }

        var row = 2;

        foreach (var block in _currentFunc.Blocks)
        {
            var blockLifetime = GetBlockLifetime(block.Id);
            worksheet.Cell(row, 1).SetText($"Block {block.Id}");
            worksheet.Cell(row, 2).SetText("Incoming=" + string.Join(",", blockLifetime.IncomingBlocks.ToArray()));
            row++;

            foreach (var inst in block.Instructions)
            {
                var lifetime = GetLifetime(inst);
                var effects = lifetime.Effects;

                worksheet.Cell(row, 1).SetText($"{inst.Id}");

                var writer = new IrWriter();
                inst.WriteIr(writer);
                worksheet.Cell(row, 2).SetText(writer.Generate());

                worksheet.Cell(row, 3).SetText(string.Join(",", effects.Reads.Select(x => x.Slot)));
                worksheet.Cell(row, 4).SetText(string.Join(",", effects.Writes.Select(x => x.Slot)));
                worksheet.Cell(row, 5).SetText(string.Join(',', effects.Jumps));

                foreach (var slot in _slots.Values)
                {
                    var slotState = lifetime.GetSlot(slot.Id);
                    worksheet.Cell(row, 6 + slot.Id).SetText(slotState.ToDebugString());
                }

                row++;
            }
        }

        workbook.SaveAs(fileName);
    }
}