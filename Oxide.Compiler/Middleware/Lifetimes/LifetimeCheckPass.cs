using System;
using System.Collections.Generic;
using System.Linq;
using Oxide.Compiler.IR;
using Oxide.Compiler.IR.Instructions;
using Oxide.Compiler.IR.TypeRefs;
using Oxide.Compiler.IR.Types;

namespace Oxide.Compiler.Middleware.Lifetimes;

public class LifetimeCheckPass
{
    private MiddlewareManager Manager { get; }
    private LifetimePass Pass => Manager.Lifetime;

    private Dictionary<int, SlotDeclaration> _slots;

    private FunctionLifetime _functionLifetime;

    private Dictionary<int, Instruction> _valueCreations;

    public LifetimeCheckPass(MiddlewareManager manager)
    {
        Manager = manager;
    }

    public void Analyse(IrUnit unit)
    {
        Console.WriteLine("Checking lifetimes");

        foreach (var objects in unit.Objects.Values)
        {
            if (objects is Function func)
            {
                ProcessFunction(func);
            }
        }

        foreach (var imps in unit.Implementations.Values)
        {
            foreach (var imp in imps)
            {
                foreach (var func in imp.Functions)
                {
                    ProcessFunction(func);
                }
            }
        }
    }

    private void ProcessFunction(Function func)
    {
        if (func.IsExtern || !func.HasBody)
        {
            return;
        }

        Console.WriteLine($" - Checking function {func.Name}");

        _functionLifetime = Pass.FunctionLifetimes[func];

        // Extract slots
        _slots = new Dictionary<int, SlotDeclaration>();
        foreach (var scope in func.Scopes)
        {
            foreach (var slot in scope.Slots.Values)
            {
                _slots.Add(slot.Id, slot);
            }
        }

        // Check borrows are extended
        var returnInstructions = new List<ReturnInst>();
        _valueCreations = new Dictionary<int, Instruction>();
        foreach (var block in func.Blocks)
        {
            foreach (var inst in block.Instructions)
            {
                var lifetime = GetLifetime(inst);

                if (inst is ReturnInst returnInst)
                {
                    returnInstructions.Add(returnInst);
                }

                foreach (var id in lifetime.ProducedValues)
                {
                    _valueCreations.Add(id, inst);
                }

                var incomingRequirements = new Dictionary<int, HashSet<Requirement>>();

                foreach (var slotId in lifetime.ActiveSlots)
                {
                    var slot = lifetime.GetSlot(slotId);

                    if (slot.Status == SlotStatus.Error)
                    {
                        throw new Exception($"Error with slot {slotId}: {slot.ErrorMessage}");
                    }

                    // if (slot.Borrowed)
                    // {
                    //     var otherSlot = lifetime.GetSlot(slot.From);
                    //
                    //     if (otherSlot.Status != SlotStatus.Active)
                    //     {
                    //         throw new Exception(
                    //             $"Required slot {slot.From} for {slotId} not active: {otherSlot.Status}"
                    //         );
                    //     }
                    // }

                    foreach (var slotValue in slot.Values)
                    {
                        if (slot.Status != SlotStatus.Moved)
                        {
                            foreach (var required in _functionLifetime.DirectRequirements[slotValue])
                            {
                                if (!incomingRequirements.TryGetValue(required.Value, out var incReqs))
                                {
                                    incReqs = new HashSet<Requirement>();
                                    incomingRequirements.Add(required.Value, incReqs);
                                }

                                incReqs.Add(new Requirement(slotValue, required.Mutable, required.Field));
                            }
                        }

                        foreach (var required in _functionLifetime.ValueRequirements[slotValue])
                        {
                            var requiredValue = required.Value;
                            var otherSlotId = _functionLifetime.ValueMap[requiredValue];
                            if (lifetime.Overwritten.Contains(otherSlotId))
                            {
                                // TODO: More advanced check
                            }
                            else
                            {
                                var otherSlot = lifetime.GetSlot(otherSlotId);
                                if (!otherSlot.Values.Contains(requiredValue))
                                {
                                    throw new Exception(
                                        $"Slot {otherSlotId} did not contain expected value {requiredValue}"
                                    );
                                }
                            }
                        }
                    }
                }

                foreach (var (value, reqs) in incomingRequirements)
                {
                    var slot = _functionLifetime.ValueMap[value];
                    var borrowed = false;
                    var mutBorrowed = 0;
                    var fieldBorrows = new HashSet<string>();
                    var mutFieldBorrows = new Dictionary<string, int>();

                    foreach (var req in reqs)
                    {
                        var prefix = $"[{inst.Id}|Slot={slot}|Value={value}|Inc={req.Value}]";

                        if (req.Field != null)
                        {
                            if (req.Mutable)
                            {
                                if (borrowed)
                                {
                                    throw new Exception($"{prefix} Whole value is already non-mutably borrowed");
                                }

                                if (mutBorrowed != 0)
                                {
                                    throw new Exception($"{prefix} Whole value is already mutably borrowed");
                                }

                                if (fieldBorrows.Contains(req.Field))
                                {
                                    throw new Exception($"{prefix} Value field is already non-mutably borrowed");
                                }

                                if (mutFieldBorrows.ContainsKey(req.Field) && mutFieldBorrows[req.Field] != req.Value)
                                {
                                    throw new Exception($"{prefix} Value field is already mutably borrowed");
                                }

                                mutFieldBorrows[req.Field] = req.Value;
                            }
                            else
                            {
                                if (mutBorrowed != 0)
                                {
                                    throw new Exception($"{prefix} Whole value is already mutably borrowed");
                                }

                                if (mutFieldBorrows.ContainsKey(req.Field))
                                {
                                    throw new Exception($"{prefix} Value field is already mutably borrowed");
                                }

                                fieldBorrows.Add(req.Field);
                            }
                        }
                        else
                        {
                            if (req.Mutable)
                            {
                                if (borrowed)
                                {
                                    throw new Exception($"{prefix} Whole value is already non-mutably borrowed");
                                }

                                if (mutBorrowed != 0 && mutBorrowed != req.Value)
                                {
                                    throw new Exception($"{prefix} Whole value is already mutably borrowed");
                                }

                                if (fieldBorrows.Count > 0)
                                {
                                    throw new Exception($"{prefix} Value is already partially non-mutably borrowed");
                                }

                                if (mutFieldBorrows.Count > 0)
                                {
                                    throw new Exception($"{prefix} Value is already partially mutably borrowed");
                                }

                                mutBorrowed = req.Value;
                            }
                            else
                            {
                                if (mutBorrowed != 0)
                                {
                                    throw new Exception($"{prefix} Whole value is already mutably borrowed");
                                }

                                if (mutFieldBorrows.Count > 0)
                                {
                                    throw new Exception($"{prefix} Value is already partially mutably borrowed");
                                }

                                borrowed = true;
                            }
                        }
                    }
                }
            }
        }

        // Check return lifetimes
        foreach (var returnInst in returnInstructions)
        {
            var lifetime = GetLifetime(returnInst);
            if (!returnInst.ReturnSlot.HasValue)
            {
                continue;
            }

            var slotDef = _slots[returnInst.ReturnSlot.Value];
            if (slotDef.Type is not BorrowTypeRef)
            {
                continue;
            }

            var slot = lifetime.GetSlot(returnInst.ReturnSlot.Value);
            if (slot.Values.Count == 0)
            {
                throw new Exception("Return slot has no value");
            }

            var sourceParams = new HashSet<int>();
            var allDerived = true;
            foreach (var value in slot.Values)
            {
                var inprogress = new HashSet<int>();
                if (!CheckValueOrigin(value, sourceParams, inprogress))
                {
                    allDerived = true;
                }
            }

            if (!allDerived)
            {
                throw new Exception("Borrowed value return is not derived from source parameters");
            }

            if (sourceParams.Count != 1)
            {
                throw new Exception("Borrowed value return is derived from multiple parameters");
            }

            if (sourceParams.Single() != 0)
            {
                throw new Exception("Borrowed value return is not derived from this parameter");
            }
        }
    }

    private bool CheckValueOrigin(int value, HashSet<int> sourceParams, HashSet<int> inprogress)
    {
        var slot = _functionLifetime.ValueMap[value];
        var slotDef = _slots[slot];

        if (slotDef.Type is BaseTypeRef)
        {
            Console.WriteLine("Saw base type while tracing origin");
            return false;
        }

        if (_functionLifetime.ValueSourceParameters.TryGetValue(value, out var param))
        {
            sourceParams.Add(param);
            return true;
        }

        if (!_valueCreations.TryGetValue(value, out var inst))
        {
            throw new Exception("Unknown value origin");
        }

        var lifetime = GetLifetime(inst);
        var writeData = lifetime.Effects.Writes.Single(x => x.Slot == slot);

        int sourceSlot;
        if (writeData.MoveSource.HasValue)
        {
            sourceSlot = writeData.MoveSource.Value;
        }
        else if (writeData.ReferenceSource.HasValue)
        {
            sourceSlot = writeData.ReferenceSource.Value;
        }
        else
        {
            Console.WriteLine("Hit new value while tracing origin");
            return false;
        }

        var sourceState = lifetime.GetSlot(sourceSlot);
        var values = sourceState.Values.Where(x => !inprogress.Contains(x)).ToArray();
        if (values.Length == 0)
        {
            throw new Exception("Source slot has no checkable values");
        }

        inprogress.Add(value);

        var allDerived = true;
        foreach (var sourceValue in values)
        {
            if (!CheckValueOrigin(sourceValue, sourceParams, inprogress))
            {
                allDerived = false;
            }
        }

        inprogress.Remove(value);

        return allDerived;
    }

    private InstructionLifetime GetLifetime(Instruction inst)
    {
        return _functionLifetime.InstructionLifetimes[inst.Id];
    }
}