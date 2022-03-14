using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using ClosedXML.Excel;
using GiGraph.Dot.Entities.Graphs;
using GiGraph.Dot.Entities.Nodes;
using GiGraph.Dot.Extensions;
using Oxide.Compiler.IR;
using Oxide.Compiler.IR.Instructions;
using Oxide.Compiler.IR.TypeRefs;
using Oxide.Compiler.IR.Types;
using Oxide.Compiler.Utils;

namespace Oxide.Compiler.Middleware.Lifetimes;

/// <summary>
/// Finds the lifetimes of each slot
/// </summary>
public class LifetimePass
{
    private MiddlewareManager Manager { get; }
    private IrStore Store => Manager.Store;

    public Dictionary<Function, FunctionLifetime> FunctionLifetimes { get; private set; }

    private FunctionLifetime _functionLifetime;
    private Dictionary<int, Block> _blocks;
    private Dictionary<int, SlotDeclaration> _slots;

    private int _lastValueId;

    private ConcreteTypeRef _thisRef;

    public LifetimePass(MiddlewareManager manager)
    {
        Manager = manager;
    }

    public void Analyse(IrUnit unit, string outputDest)
    {
        Console.WriteLine("Analysing lifetimes");

        FunctionLifetimes = new Dictionary<Function, FunctionLifetime>();

        foreach (var obj in unit.Objects.Values)
        {
            if (obj is Function func)
            {
                _thisRef = null;
                ProcessFunction(func);
            }
        }

        foreach (var imps in unit.Implementations.Values)
        {
            foreach (var imp in imps)
            {
                _thisRef = imp.Target;
                foreach (var func in imp.Functions)
                {
                    ProcessFunction(func);
                }
            }
        }

        GenerateDebug(outputDest);
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
                var effects = inst.GetEffects(Store);
                var lifetime = new InstructionLifetime
                {
                    Id = inst.Id,
                    FunctionLifetime = _functionLifetime,
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
        while (updated)
        {
            Console.WriteLine("   - Processing blocks");
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
                    foreach (var nestedRequires in _functionLifetime.ValueRequirements[requires.Value])
                    {
                        if (pair.Value.Add(nestedRequires))
                        {
                            updated = true;
                        }
                    }
                }
            }

            foreach (var block in func.Blocks)
            {
                foreach (var inst in block.Instructions)
                {
                    var lifetime = GetLifetime(inst);

                    foreach (var write in lifetime.Effects.Writes)
                    {
                        var targetLifetime = write.TargetBlock.HasValue
                            ? GetLifetime(_blocks[write.TargetBlock.Value].FirstInstruction)
                            : lifetime;

                        var slotState = targetLifetime.GetSlot(write.Slot);
                        if (slotState.Status != SlotStatus.Active && slotState.Status != SlotStatus.Moved)
                        {
                            continue;
                        }

                        if (!write.MoveSource.HasValue)
                        {
                            continue;
                        }

                        var moveSource = write.MoveSource.Value;
                        var moveSlot = lifetime.GetSlot(moveSource);
                        if (moveSlot.Status != SlotStatus.Active && moveSlot.Status != SlotStatus.Moved)
                        {
                            continue;
                        }

                        var (_, persistReqs) = IsSlotCopy(write.MoveSource.Value, write.MoveField);
                        if (!persistReqs)
                        {
                            continue;
                        }

                        foreach (var currentValue in slotState.Values)
                        {
                            var currentRequires = _functionLifetime.ValueRequirements[currentValue];

                            foreach (var moveValue in moveSlot.Values)
                            {
                                updated |= currentRequires.AddRange(_functionLifetime.ValueRequirements[moveValue]);
                            }
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

        foreach (var block in func.Blocks)
        {
            AddBlockStates(block);
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

        // Perform transitive reduction on value requirements
        foreach (var (fromId, xReq) in _functionLifetime.ValueRequirements)
        {
            foreach (var edge in xReq)
            {
                var targetId = edge.Value;

                var otherRoute = false;

                foreach (var otherEdge in xReq)
                {
                    // Ignore same edge
                    if (Equals(edge, otherEdge))
                    {
                        continue;
                    }

                    var otherId = otherEdge.Value;
                    var otherReqs = _functionLifetime.ValueRequirements[otherId];

                    if (otherReqs.Any(x => x.Value == targetId))
                    {
                        otherRoute = true;
                        break;
                    }
                }

                if (!otherRoute)
                {
                    _functionLifetime.DirectRequirements[fromId].Add(edge);
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
                    var writeId = AllocateValue(slot.Id);
                    slotState.AddValues(new HashSet<int> { writeId });
                    _functionLifetime.ValueSourceParameters.Add(writeId, slot.ParameterSource.Value);
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
                var writeId = AllocateValue(write.Slot);
                slotState.AddValues(new HashSet<int> { writeId });
                lifetime.ProducedValues.Add(writeId);

                // if (write.ReferenceSource.HasValue)
                // {
                //     slotState.MarkBorrowed(write.ReferenceSource.Value, write.ReferenceField, write.ReferenceMutable);
                // }

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

        return updated;
    }

    private bool TraceWriteReference(InstructionLifetime lifetime, InstructionEffects.WriteData write)
    {
        var targetLifetime = write.TargetBlock.HasValue
            ? GetLifetime(_blocks[write.TargetBlock.Value].FirstInstruction)
            : lifetime;

        var slot = targetLifetime.GetSlot(write.Slot);
        if (
            (slot.Status != SlotStatus.Active && slot.Status != SlotStatus.Moved)
            || !write.ReferenceSource.HasValue) return false;

        var referenceSource = write.ReferenceSource.Value;

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
            return reqs.AddRange(
                refSlot
                    .Values
                    .Select(x => new Requirement(x, write.ReferenceMutable, write.ReferenceField))
            );
        }
        else
        {
            // throw new Exception("Missing value");
        }

        return false;
    }

    private bool TraceRead(InstructionLifetime lifetime, InstructionEffects.ReadData read)
    {
        var (isCopy, _) = IsSlotCopy(read.Slot, read.Field);
        var currentState = lifetime.GetSlot(read.Slot);

        var visited = new HashSet<(int, int)>();
        var updated = TraceReadInner(lifetime, read.Slot, true, visited, "");

        if (currentState.Status != SlotStatus.Active)
        {
            return updated;
        }

        if (read.Moved && !isCopy)
        {
            if (read.Field != null)
            {
                throw new NotImplementedException("Field moves");
            }

            currentState.Move();
        }

        return updated;
    }

    private bool TraceReadInner(InstructionLifetime lifetime, int slot, bool first, HashSet<(int, int)> visited,
        string order)
    {
        var updated = false;
        var currentState = lifetime.GetSlot(slot);

        // Check if this slot has already been processed
        if (currentState.Status == SlotStatus.Error)
        {
            return false;
        }

        if (lifetime.Set.Contains(slot) && (!first || !lifetime.Overwritten.Contains(slot)))
        {
            return false;
        }

        if (lifetime.Previous != null)
        {
            updated |= TraceReadInner(lifetime.Previous, slot, false, visited, order);

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
                updated |= TraceReadInner(otherLifetime, slot, false, visited,
                    $"{order} {pathKey.Id}-{pathKey.incBlock}");
                visited.Remove(pathKey);

                var otherSlot = otherLifetime.GetSlot(slot);
                if (otherSlot.Status == SlotStatus.Unprocessed)
                {
                    skipped = $"{skipped} {incBlock}:unprocessed";
                    continue;
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
                foreach (var state in incomingStates)
                {
                    updated |= currentState.Propagate(state);
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
                if (slot.Status == SlotStatus.Active || slot.Status == SlotStatus.Moved)
                {
                    foreach (var slotValue in slot.Values)
                    {
                        foreach (var required in _functionLifetime.ValueRequirements[slotValue])
                        {
                            var requiredSlot = _functionLifetime.ValueMap[required.Value];
                            var visited = new HashSet<(int, int)>();
                            TraceReadInner(lifetime, requiredSlot, true, visited, "");
                        }
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

    private void AddBlockStates(Block block)
    {
        // Add move markers to optional moves
        foreach (var inst in block.Instructions)
        {
            var lifetime = GetLifetime(inst);

            foreach (var read in lifetime.Effects.Reads)
            {
                if (!read.Moved)
                {
                    continue;
                }

                var slot = lifetime.GetSlot(read.Slot);
                if (slot.Status != SlotStatus.Active)
                {
                    continue;
                }

                var next = lifetime.Next;
                if (next == null)
                {
                    continue;
                }

                var nextSlot = next.GetSlot(read.Slot);
                if (nextSlot.Status == SlotStatus.NoValue)
                {
                    slot.Move();
                }
            }

            foreach (var write in lifetime.Effects.Writes)
            {
                if (write.MoveSource is not { } slotId)
                {
                    continue;
                }

                var slot = lifetime.GetSlot(slotId);
                if (slot.Status != SlotStatus.Active)
                {
                    continue;
                }

                var next = lifetime.Next;
                if (next == null)
                {
                    continue;
                }

                var nextSlot = next.GetSlot(slotId);
                if (nextSlot.Status == SlotStatus.NoValue)
                {
                    slot.Move();
                }
            }
        }
    }

    private int AllocateValue(int slot)
    {
        var newId = _lastValueId++;
        _functionLifetime.ValueMap.Add(newId, slot);
        _functionLifetime.ValueRequirements.Add(newId, new HashSet<Requirement>());
        _functionLifetime.DirectRequirements.Add(newId, new HashSet<Requirement>());
        return newId;
    }

    private InstructionLifetime GetLifetime(Instruction inst)
    {
        return _functionLifetime.InstructionLifetimes[inst.Id];
    }

    private (bool copy, bool reqs) IsSlotCopy(int slot, string field)
    {
        var slotDec = _slots[slot];
        return IsTypeCopy(slotDec.Type, field);
    }

    private (bool copy, bool reqs) IsTypeCopy(TypeRef typeRef, string field)
    {
        var checkType = typeRef;
        if (field != null)
        {
            switch (typeRef.GetBaseType())
            {
                case ConcreteTypeRef concreteTypeRef:
                {
                    var resolved = Store.Lookup(concreteTypeRef.Name);
                    if (resolved is not Struct structDef)
                    {
                        throw new Exception($"Unexpected type {resolved}");
                    }

                    var structContext = new GenericContext(
                        null,
                        structDef.GenericParams,
                        concreteTypeRef.GenericParams,
                        _thisRef
                    );
                    var fieldDef = structDef.Fields.Single(x => x.Name == field);
                    checkType = structContext.ResolveRef(fieldDef.Type, true);
                    break;
                }
                case GenericTypeRef genericTypeRef:
                {
                    checkType = null;
                    break;
                }
                case ThisTypeRef:
                {
                    var resolved = Store.Lookup(_thisRef.Name);
                    if (resolved is not Struct structDef)
                    {
                        throw new Exception($"Unexpected type {resolved}");
                    }

                    var structContext = new GenericContext(
                        null,
                        structDef.GenericParams,
                        _thisRef.GenericParams,
                        _thisRef
                    );
                    var fieldDef = structDef.Fields.Single(x => x.Name == field);
                    checkType = structContext.ResolveRef(fieldDef.Type, true);
                    break;
                }
                case DerivedTypeRef derivedTypeRef:
                    throw new NotImplementedException();
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }

        switch (checkType)
        {
            case null:
            case ReferenceTypeRef:
            case DerivedRefTypeRef:
                return (true, false);
            case PointerTypeRef:
            case BorrowTypeRef:
                return (true, true);
            case BaseTypeRef:
            {
                var copyProperties = Store.GetCopyProperties(
                    checkType,
                    new WhereConstraints(ImmutableDictionary<string, ImmutableArray<TypeRef>>.Empty)
                );
                return (copyProperties.CanCopy, false);
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(typeRef));
        }
    }

    public void GenerateDebug(string outputDest)
    {
        using var workbook = new XLWorkbook();

        var counter = 0;
        foreach (var pair in FunctionLifetimes)
        {
            var func = pair.Key;
            var funcLifetime = pair.Value;

            var worksheet =
                workbook.Worksheets.Add($"{counter++} Func {func.Name.ToString().Replace(':', '_')}".MaxLength(31));

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

                    worksheet.Cell(1, 6 + slot.Id).SetText($"Slot {slot.Id}");
                    worksheet.Cell(2, 6 + slot.Id).SetText(slot.Name ?? "internal");
                }
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

                    worksheet.Cell(row, 3).SetText(string.Join(",", effects.Reads.Select(x => x.ToDebugString())));
                    worksheet.Cell(row, 4).SetText(string.Join(",", effects.Writes.Select(x => x.ToDebugString())));
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

        workbook.SaveAs($"{outputDest}/lifetimes.xlsx");

        var graph = new DotGraph(directed: true);

        counter = 0;
        foreach (var (func, lifetime) in FunctionLifetimes)
        {
            var name = ($"{counter++} Func {func.Name.ToString().Replace(':', '_')}");

            graph.Clusters.Add(name, cluster =>
            {
                cluster.Label = name;

                foreach (var id in lifetime.DirectRequirements.Keys)
                {
                    cluster.Nodes.Add($"{counter}-{id}", node => { node.Label = $"{id}"; });
                }

                var used = new HashSet<string>();

                foreach (var (from, reqs) in lifetime.DirectRequirements)
                {
                    foreach (var req in reqs)
                    {
                        used.Add($"{counter}-{from}");
                        used.Add($"{counter}-{req.Value}");
                        cluster.Edges.Add($"{counter}-{from}", $"{counter}-{req.Value}");
                    }
                }

                cluster.Nodes.RemoveAll(x => !used.Contains(((DotNode)x).Id));
            });

            counter++;
        }

        graph.SaveToFile($"{outputDest}\\lifetimes.gv");
    }
}