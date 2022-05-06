using System;
using System.Collections.Generic;
using System.Linq;
using Oxide.Compiler.IR;
using Oxide.Compiler.IR.Instructions;
using Oxide.Compiler.IR.TypeRefs;
using Oxide.Compiler.IR.Types;
using Oxide.Compiler.Middleware;
using Oxide.Compiler.Middleware.Lifetimes;
using Oxide.Compiler.Middleware.Usage;

namespace Oxide.Compiler.Backend.Js;

public class JsBodyGenerator
{
    public JsBackend Backend { get; }

    public JsWriter Writer { get; private set; }

    public IrStore Store => Backend.Store;
    public FunctionLifetime FunctionLifetime { get; private set; }
    private Function _func;
    public Block CurrentBlock { get; private set; }
    public GenericContext FunctionContext { get; set; }

    public Dictionary<string, JsWriter> BlockWriters { get; private set; }

    private Dictionary<int, uint> _slotOffset;

    private Dictionary<int, uint> _slotSize;

    private Dictionary<int, TypeRef> _slotTypes;

    private Dictionary<int, string> _slotLivenessMap;

    private Dictionary<(int, int), string> _jumpTrampolines;

    private Dictionary<int, string> _scopeJumpBlocks;
    private Dictionary<int, string> _scopeJumpTargets;

    public JsBodyGenerator(JsBackend backend)
    {
        Backend = backend;
    }

    public void Compile(FunctionRef key, Function func, GenericContext context, FunctionLifetime functionLifetime)
    {
        _func = func;
        FunctionContext = context;
        FunctionLifetime = functionLifetime;
        Writer = Backend.Writer;

        // _slotNames = new Dictionary<int, string>();
        _slotTypes = new Dictionary<int, TypeRef>();
        _slotOffset = new Dictionary<int, uint>();
        _slotSize = new Dictionary<int, uint>();
        _slotLivenessMap = new Dictionary<int, string>();

        // Create storage slot for return value
        if (_func.ReturnType != null)
        {
            Writer.WriteLine($"var return_value;");
        }

        uint frameSize = 0;

        foreach (var scope in func.Scopes)
        {
            foreach (var slotDef in scope.Slots.Values)
            {
                var resolvedType = FunctionContext.ResolveRef(slotDef.Type);
                var varName = $"scope_{scope.Id}_slot_{slotDef.Id}_{slotDef.Name ?? "autogen"}";

                _slotTypes.Add(slotDef.Id, resolvedType);
                _slotOffset.Add(slotDef.Id, frameSize);

                var size = Backend.GetSize(resolvedType);
                _slotSize.Add(slotDef.Id, size);
                frameSize += size;

                if (Backend.GetDropFunctionRef(resolvedType) != null)
                {
                    var liveName = $"{varName}____live";
                    Writer.WriteLine($"var {liveName} = false;");
                    _slotLivenessMap.Add(slotDef.Id, liveName);
                }
            }
        }

        Writer.WriteLine($"var __sf = heap.alloc({frameSize});");

        foreach (var scope in func.Scopes)
        {
            foreach (var slotDef in scope.Slots.Values)
            {
                if (slotDef.ParameterSource is { } src)
                {
                    var paramName = func.Parameters[src].Name;
                    var type = _slotTypes[slotDef.Id];

                    StoreSlot(slotDef.Id, $"_{paramName}", type, true);
                    MarkActive(slotDef.Id);
                }
            }
        }

        BlockWriters = new Dictionary<string, JsWriter>();

        // Create scope jump targets
        _scopeJumpTargets = new Dictionary<int, string>();
        _scopeJumpBlocks = new Dictionary<int, string>();
        _jumpTrampolines = new Dictionary<(int, int), string>();
        foreach (var scope in func.Scopes)
        {
            Writer = Backend.Writer;
            var target = $"scope_{scope.Id}_jump_tgt";
            Writer.WriteLine($"var {target};");
            _scopeJumpTargets.Add(scope.Id, target);

            var block = $"scope_{scope.Id}_jump";
            _scopeJumpBlocks.Add(scope.Id, block);

            Writer = new JsWriter();
            BlockWriters[block] = Writer;

            CreateScopeJumpBlock(scope);
        }

        foreach (var block in func.Blocks)
        {
            CompileBlock(block);
        }

        Writer = Backend.Writer;

        Writer.WriteLine($"var __block = \"{func.EntryBlock}\";");
        Writer.WriteLine("__block_loop: while(true) {");
        Writer.Indent(1);

        var first = true;
        foreach (var (bkey, bwriter) in BlockWriters)
        {
            if (!first)
            {
                Writer.Write(" else ");
            }
            else
            {
                Writer.BeginLine();
            }

            first = false;

            Writer.Write($"if(__block === \"{bkey}\") {{");
            Writer.EndLine();
            Writer.Indent(1);

            foreach (var line in bwriter.Generate().TrimEnd().Split(Environment.NewLine))
            {
                Writer.WriteLine(line);
            }

            Writer.Indent(-1);
            Writer.BeginLine();
            Writer.Write("}");
        }

        Writer.Write(" else if(__block === \"return\") {");
        Writer.EndLine();
        Writer.Indent(1);
        Writer.WriteLine("heap.free(__sf);");
        if (_func.ReturnType != null)
        {
            Writer.WriteLine($"return return_value;");
        }
        else
        {
            Writer.WriteLine($"return;");
        }

        Writer.Indent(-1);
        Writer.WriteLine("}");

        Writer.WriteLine("console.log(\"Unexpected state\");");
        Writer.WriteLine("break;");

        Writer.Indent(-1);
        Writer.WriteLine("}");
    }

    private void CreateScopeJumpBlock(Scope scope)
    {
        (SlotDeclaration sd, string dropFunc)[] withDrops = scope.Slots.Values.Select(x =>
        {
            var resolvedType = FunctionContext.ResolveRef(x.Type);
            var dropFunc = Backend.GetDropFunctionRef(resolvedType);
            return (x, dropFunc);
        }).Where(x => x.dropFunc != null).ToArray();

        for (var i = 0; i < withDrops.Length; i++)
        {
            var sd = withDrops[i].sd;
            var dropFunc = withDrops[i].dropFunc;
            var livePtr = _slotLivenessMap[sd.Id];

            Writer.WriteLine($"if ({livePtr}) {{");
            Writer.Indent(1);

            var (_, value) = GetSlotRef(sd.Id, true);
            // var (_, value) = LoadSlot(sd.Id, $"drop_{sd.Id}_value", true);
            Writer.WriteLine($"{dropFunc}(heap, {value});");
            MarkMoved(sd.Id);

            Writer.Indent(-1);
            Writer.WriteLine("}");
        }

        var tgtSlot = _scopeJumpTargets[scope.Id];
        Writer.WriteLine($"__block = {tgtSlot};");
        Writer.WriteLine("continue __block_loop;");
    }

    private void PerformDrop(string value, TypeRef typeRef)
    {
        var dropFunc = Backend.GetDropFunctionRef(typeRef);
        if (dropFunc == null)
        {
            return;
        }

        Writer.WriteLine($"{dropFunc}(heap, {value});");
    }

    private void DropIfActive(int slotId, string name)
    {
        if (!_slotLivenessMap.TryGetValue(slotId, out var livePtr))
        {
            return;
        }

        Writer.WriteLine($"if ({livePtr}) {{");
        Writer.Indent(1);

        // var (valueType, value) = LoadSlot(slotId, $"{name}_value", true);
        var (valueType, value) = GetSlotRef(slotId, true);
        PerformDrop(value, valueType);
        MarkMoved(slotId);

        Writer.Indent(-1);
        Writer.WriteLine("}");
    }

    private void CompileBlock(Block block)
    {
        Writer = new JsWriter();
        BlockWriters[block.Id.ToString()] = Writer;
        CurrentBlock = block;

        foreach (var instruction in block.Instructions)
        {
            CompileInstruction(instruction);
        }

        CurrentBlock = null;
    }

    private void CompileInstruction(Instruction instruction)
    {
        var irWriter = new IrWriter();
        instruction.WriteIr(irWriter);
        Writer.Comment(irWriter.Generate());

        switch (instruction)
        {
            case MoveInst moveInst:
                CompileMoveInst(moveInst);
                break;
            case ConstInst constInst:
                CompileConstInst(constInst);
                break;
            case ArithmeticInst arithmeticInst:
                CompileArithmeticInstruction(arithmeticInst);
                break;
            case ComparisonInst comparisonInst:
                CompileComparisonInst(comparisonInst);
                break;
            case JumpInst jumpInst:
                CompileJumpInst(jumpInst);
                break;
            case StaticCallInst staticCallInst:
                CompileStaticCallInst(staticCallInst);
                break;
            case ReturnInst returnInst:
                CompileReturnInst(returnInst);
                break;
            case AllocStructInst allocStructInst:
                CompileAllocStructInst(allocStructInst);
                break;
            case UnaryInst unaryInst:
                CompileUnaryInstruction(unaryInst);
                break;
            case SlotBorrowInst slotBorrowInst:
                CompileSlotBorrowInst(slotBorrowInst);
                break;
            case FieldMoveInst fieldMoveInst:
                CompileFieldMoveInst(fieldMoveInst);
                break;
            case FieldBorrowInst fieldBorrowInst:
                CompileFieldBorrowInst(fieldBorrowInst);
                break;
            case StoreIndirectInst storeIndirectInst:
                CompileStoreIndirectInst(storeIndirectInst);
                break;
            case LoadIndirectInst loadIndirectInst:
                CompileLoadIndirectInst(loadIndirectInst);
                break;
            case CastInst castInst:
                CompileCastInst(castInst);
                break;
            case RefBorrowInst refBorrowInst:
                CompileRefBorrow(refBorrowInst);
                break;
            case AllocVariantInst allocVariantInst:
                CompileAllocVariantInst(allocVariantInst);
                break;
            case JumpVariantInst jumpVariantInst:
                CompileJumpVariantInst(jumpVariantInst);
                break;
            case RefDeriveInst refDeriveInst:
                CompileRefDeriveInst(refDeriveInst);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(instruction));
        }
    }

    private void CompileAllocVariantInst(AllocVariantInst inst)
    {
        var variant = Store.Lookup<Variant>(inst.VariantType.Name);
        var variantTypeRef = (ConcreteTypeRef) FunctionContext.ResolveRef(inst.VariantType);

        if (!CurrentBlock.Scope.CanAccessSlot(inst.SlotId))
        {
            throw new Exception($"Slot is not accessible from {CurrentBlock.Id}");
        }

        if (!Equals(variantTypeRef, _slotTypes[inst.SlotId]))
        {
            throw new Exception("Tried to store incompatible type");
        }

        var slotOffset = _slotOffset[inst.SlotId];
        Writer.WriteLine($"heap.clearBlob(__sf + {slotOffset}, {_slotSize[inst.SlotId]});");
        MarkActive(inst.SlotId);

        var (_, baseAddr) = GetSlotRef(inst.SlotId);

        // Create type value
        var index = variant.Items.FindIndex(x => x.Name == inst.ItemName);
        Writer.WriteLine($"heap.writeU8({baseAddr}, {index});");

        if (inst.ItemSlot is not { } slotId) return;

        var variantItemRef = new ConcreteTypeRef(
            new QualifiedName(true, variantTypeRef.Name.Parts.Add(inst.ItemName)),
            variantTypeRef.GenericParams
        );

        // Store variant value
        var (type, value) = LoadSlot(slotId, $"inst_{inst.Id}_load");
        if (!Equals(type, variantItemRef))
        {
            throw new Exception("Invalid variant item type");
        }

        var slotLifetime = GetLifetime(inst).GetSlot(slotId);
        if (slotLifetime.Status == SlotStatus.Moved)
        {
            MarkMoved(slotId);
        }
        else
        {
            throw new Exception("Unexpected");
        }

        Writer.WriteLine(Backend.BuildStore(variantItemRef, $"{baseAddr} + 1", value));
    }

    private void CompileAllocStructInst(AllocStructInst inst)
    {
        var structType = (ConcreteTypeRef) FunctionContext.ResolveRef(inst.StructType);
        var structDef = Store.Lookup<Struct>(structType.Name);
        var structContext = new GenericContext(null, structDef.GenericParams, structType.GenericParams, null);

        var targetValues = new Dictionary<string, string>();
        foreach (var (fname, fvalue) in inst.FieldValues)
        {
            var index = structDef.Fields.FindIndex(x => x.Name == fname);
            var fieldDef = structDef.Fields[index];
            var fieldType = structContext.ResolveRef(fieldDef.Type);
            var (valType, valPtr) = GetSlotRef(fvalue);
            var properties = Store.GetCopyProperties(valType);
            var slotLifetime = GetLifetime(inst).GetSlot(fvalue);

            if (!Equals(fieldType, valType))
            {
                throw new Exception("Value type does not match field type");
            }

            string val;
            if (slotLifetime.Status == SlotStatus.Moved)
            {
                (_, val) = LoadSlot(fvalue, $"inst_{inst.Id}_field_{fname}_value");
                MarkMoved(fvalue);
            }
            else if (properties.CanCopy)
            {
                val = GenerateCopy(valType, properties, valPtr, $"inst_{inst.Id}_field_{fname}_value");
            }
            else
            {
                throw new Exception("Value is not moveable");
            }

            targetValues.Add(fname, val);
        }

        if (!CurrentBlock.Scope.CanAccessSlot(inst.SlotId))
        {
            throw new Exception($"Slot is not accessible from {CurrentBlock.Id}");
        }

        if (!Equals(structType, _slotTypes[inst.SlotId]))
        {
            throw new Exception("Tried to store incompatible type");
        }

        var slotOffset = _slotOffset[inst.SlotId];
        Writer.WriteLine($"heap.clearBlob(__sf + {slotOffset}, {_slotSize[inst.SlotId]});");

        foreach (var fname in inst.FieldValues.Keys)
        {
            var ftype = structContext.ResolveRef(structDef.Fields.First(x => x.Name == fname).Type);
            var foffset = Backend.GetFieldOffset(structType, fname);
            Writer.WriteLine(Backend.BuildStore(ftype, $"__sf + {slotOffset} + {foffset}", targetValues[fname]));
        }

        MarkActive(inst.SlotId);
    }

    private void CompileSlotBorrowInst(SlotBorrowInst inst)
    {
        var (slotType, slotRef) = GetSlotRef(inst.BaseSlot);
        StoreSlot(inst.TargetSlot, slotRef, new BorrowTypeRef(slotType, inst.Mutable));
        MarkActive(inst.TargetSlot);
    }

    private void CompileFieldMoveInst(FieldMoveInst inst)
    {
        var slotType = _slotTypes[inst.BaseSlot];

        string baseAddr;
        ConcreteTypeRef structType;
        bool isDirect;
        switch (slotType)
        {
            case BorrowTypeRef borrowTypeRef:
            {
                (_, baseAddr) = LoadSlot(inst.BaseSlot, $"inst_{inst.Id}_base");
                isDirect = false;

                if (borrowTypeRef.InnerType is not ConcreteTypeRef concreteTypeRef)
                {
                    throw new Exception("Cannot borrow field from non borrowed direct type");
                }

                structType = concreteTypeRef;
                break;
            }
            case BaseTypeRef:
                (_, baseAddr) = GetSlotRef(inst.BaseSlot);
                isDirect = true;
                throw new NotImplementedException("direct field moves");
            case PointerTypeRef pointerTypeRef:
            {
                (_, baseAddr) = LoadSlot(inst.BaseSlot, $"inst_{inst.Id}_base");
                isDirect = false;

                if (pointerTypeRef.InnerType is not ConcreteTypeRef concreteTypeRef)
                {
                    throw new Exception("Cannot borrow field from non borrowed direct type");
                }

                structType = concreteTypeRef;
                break;
            }
            case ReferenceTypeRef:
                throw new NotImplementedException();
            default:
                throw new ArgumentOutOfRangeException(nameof(slotType));
        }

        var structDef = Store.Lookup<Struct>(structType.Name);
        var index = structDef.Fields.FindIndex(x => x.Name == inst.TargetField);
        var fieldDef = structDef.Fields[index];
        var structContext = new GenericContext(null, structDef.GenericParams, structType.GenericParams, null);
        var fieldType = structContext.ResolveRef(fieldDef.Type);

        var offset = Backend.GetFieldOffset(structType, inst.TargetField);
        var addr = $"{baseAddr} + {offset}";

        var properties = Store.GetCopyProperties(fieldType);

        string destValue;
        if (properties.CanCopy)
        {
            destValue = GenerateCopy(fieldType, properties, addr, $"inst_{inst.Id}");
        }
        else if (!isDirect)
        {
            throw new Exception("Cannot move non-copyable field from a reference");
        }
        else
        {
            throw new NotImplementedException("Field moves");
        }

        StoreSlot(inst.TargetSlot, destValue, fieldType);
        MarkActive(inst.TargetSlot);
    }

    private void CompileFieldBorrowInst(FieldBorrowInst inst)
    {
        var (slotType, slotVal) = LoadSlot(inst.BaseSlot, $"inst_{inst.Id}_base");

        ConcreteTypeRef structType;
        switch (slotType)
        {
            case BorrowTypeRef borrowTypeRef:
            {
                if (borrowTypeRef.InnerType is not ConcreteTypeRef concreteTypeRef)
                {
                    throw new Exception("Cannot borrow field from non borrowed direct type");
                }

                if (!borrowTypeRef.MutableRef && inst.Mutable)
                {
                    throw new Exception("Cannot mutably borrow from non-mutable borrow");
                }

                structType = concreteTypeRef;
                break;
            }
            case BaseTypeRef:
                throw new Exception("Cannot borrow field from base type");
            case PointerTypeRef pointerTypeRef:
            {
                if (pointerTypeRef.InnerType is not ConcreteTypeRef concreteTypeRef)
                {
                    throw new Exception("Cannot borrow field from non borrowed direct type");
                }

                if (!pointerTypeRef.MutableRef && inst.Mutable)
                {
                    throw new Exception("Cannot mutably borrow from non-mutable borrow");
                }

                structType = concreteTypeRef;
                break;
            }
            case ReferenceTypeRef:
                throw new NotImplementedException();
            default:
                throw new ArgumentOutOfRangeException(nameof(slotType));
        }

        var structDef = Store.Lookup<Struct>(structType.Name);
        var index = structDef.Fields.FindIndex(x => x.Name == inst.TargetField);
        var fieldDef = structDef.Fields[index];
        var structContext = new GenericContext(null, structDef.GenericParams, structType.GenericParams, null);

        TypeRef targetType = slotType switch
        {
            BorrowTypeRef => new BorrowTypeRef(
                structContext.ResolveRef(fieldDef.Type),
                inst.Mutable
            ),
            BaseTypeRef => throw new Exception("Cannot borrow field from base type"),
            PointerTypeRef => new PointerTypeRef(
                structContext.ResolveRef(fieldDef.Type),
                inst.Mutable
            ),
            ReferenceTypeRef => throw new NotImplementedException(),
            _ => throw new ArgumentOutOfRangeException(nameof(slotType))
        };

        var offset = Backend.GetFieldOffset(structType, inst.TargetField);
        StoreSlot(inst.TargetSlot, $"{slotVal} + {offset}", targetType);
        MarkActive(inst.TargetSlot);
    }

    private void CompileCastInst(CastInst inst)
    {
        var (type, value) = LoadSlot(inst.SourceSlot, $"inst_{inst.Id}_load");
        var slotLifetime = GetLifetime(inst).GetSlot(inst.SourceSlot);
        var targetType = FunctionContext.ResolveRef(inst.TargetType);

        var (castable, unsafeCast) = Store.CanCastTypes(type, targetType);
        if (!castable)
        {
            throw new Exception($"Cannot cast from {type} to {targetType}");
        }

        if (!CurrentBlock.Scope.Unsafe && unsafeCast)
        {
            throw new Exception($"Cast from {type} to {targetType} is unsafe");
        }

        string converted;
        var dropIfMoved = false;
        var ignoreMoved = false;

        switch (type)
        {
            case BaseTypeRef baseTypeRef:
                if (Equals(baseTypeRef, targetType))
                {
                    converted = value;
                }
                else if (PrimitiveType.IsPrimitiveInt(type) && PrimitiveType.IsPrimitiveInt(targetType))
                {
                    var fromKind = PrimitiveType.GetKind(type);
                    var fromWidth = PrimitiveType.GetWidth(fromKind, true);
                    var toKind = PrimitiveType.GetKind(targetType);
                    var toWidth = PrimitiveType.GetWidth(toKind, true);

                    if (toWidth == fromWidth)
                    {
                        converted = value;
                    }
                    else if (toWidth < fromWidth)
                    {
                        converted = $"OxideMath.trunc({value}, {toWidth})";
                    }
                    else if (PrimitiveType.IsSigned(fromKind))
                    {
                        converted = $"OxideMath.ext({value}, {toWidth})";
                    }
                    else
                    {
                        converted = $"OxideMath.uext({value}, {toWidth})";
                    }
                }
                else if (
                    baseTypeRef is ConcreteTypeRef concreteTypeRef &&
                    targetType is DerivedRefTypeRef derivedRefTypeRef &&
                    Equals(concreteTypeRef.Name, QualifiedName.From("std", "DerivedBox")) &&
                    derivedRefTypeRef.StrongRef &&
                    Equals(derivedRefTypeRef.InnerType, concreteTypeRef.GenericParams.Single())
                )
                {
                    converted = value;
                }
                else
                {
                    throw new NotImplementedException();
                }

                break;
            case BorrowTypeRef:
            {
                if (targetType is not BorrowTypeRef && targetType is not PointerTypeRef)
                {
                    throw new Exception("Incompatible conversion");
                }

                converted = value;
                break;
            }
            case PointerTypeRef:
            {
                if (targetType is not PointerTypeRef && !CurrentBlock.Scope.Unsafe)
                {
                    throw new Exception("Conversion is unsafe");
                }

                converted = value;
                break;
            }
            case ReferenceTypeRef fromRef:
            {
                switch (targetType)
                {
                    case ReferenceTypeRef toRef:
                    {
                        if (!fromRef.StrongRef || toRef.StrongRef)
                        {
                            throw new Exception("Unsupported");
                        }

                        var incFunc = Backend.GenerateKeyName(
                            new FunctionRef
                            {
                                TargetMethod = ConcreteTypeRef.From(
                                    QualifiedName.From("std", "box_inc_weak"),
                                    fromRef.InnerType
                                )
                            }
                        );
                        Writer.WriteLine($"{incFunc}(heap, {value});");

                        converted = value;
                        ignoreMoved = true;
                        break;
                    }
                    case BorrowTypeRef:
                    case PointerTypeRef:
                        converted = $"{value} + {Backend.GetBoxValueOffset((ConcreteTypeRef) fromRef.InnerType)}";
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(targetType));
                }

                break;
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(type));
        }

        if (slotLifetime.Status == SlotStatus.Moved && !ignoreMoved)
        {
            if (dropIfMoved)
            {
                throw new NotImplementedException();
                // PerformDrop(value, type);
            }

            MarkMoved(inst.SourceSlot);
        }

        StoreSlot(inst.ResultSlot, converted, targetType);
        MarkActive(inst.ResultSlot);
    }

    private void CompileRefDeriveInst(RefDeriveInst inst)
    {
        var (type, value) = GetSlotRef(inst.SourceSlot);
        var properties = Store.GetCopyProperties(type);
        var slotLifetime = GetLifetime(inst).GetSlot(inst.SourceSlot);

        string fromValue;
        if (slotLifetime.Status == SlotStatus.Moved)
        {
            (_, fromValue) = LoadSlot(inst.SourceSlot, $"inst_{inst.Id}_move");
            MarkMoved(inst.SourceSlot);
        }
        else if (properties.CanCopy)
        {
            fromValue = GenerateCopy(type, properties, value, $"inst_{inst.Id}_copy");
        }
        else
        {
            throw new Exception("Value is not moveable");
        }

        DropIfActive(inst.ResultSlot, $"inst_{inst.Id}_existing");

        string boxPtr;
        TypeRef ptrType;
        string ptrValue;

        if (type is DerivedRefTypeRef derivedRefTypeRef)
        {
            var ptrOffset = Backend.GetDerivedBoxPtrFieldOffset((ConcreteTypeRef) derivedRefTypeRef.InnerType);
            boxPtr = ExtractUSize(fromValue, ptrOffset, $"inst_{inst.Id}_box_ptr");

            var valueOffset = Backend.GetDerivedBoxValueFieldOffset((ConcreteTypeRef) derivedRefTypeRef.InnerType);
            ptrValue = ExtractUSize(fromValue, valueOffset, $"inst_{inst.Id}_value");

            ptrType = derivedRefTypeRef.InnerType;
        }
        else if (type is ReferenceTypeRef referenceTypeRef)
        {
            if (!referenceTypeRef.StrongRef)
            {
                throw new Exception("Cannot derive weak reference");
            }

            boxPtr = fromValue;
            var offset = Backend.GetBoxValueOffset((ConcreteTypeRef) referenceTypeRef.InnerType);
            ptrValue = $"inst_{inst.Id}_box";
            Writer.WriteLine($"var {ptrValue} = {boxPtr} + {offset};");
            ptrType = referenceTypeRef.InnerType;
        }
        else
        {
            throw new Exception("Source is not a reference");
        }

        if (inst.FieldName != null)
        {
            var structType = (ConcreteTypeRef) ptrType;
            var structDef = Store.Lookup<Struct>(structType.Name);
            var index = structDef.Fields.FindIndex(x => x.Name == inst.FieldName);
            var fieldDef = structDef.Fields[index];
            var structContext = new GenericContext(null, structDef.GenericParams, structType.GenericParams, null);
            ptrType = structContext.ResolveRef(fieldDef.Type);

            var fieldOffset = Backend.GetFieldOffset(structType, inst.FieldName);
            ptrValue = $"{ptrValue} + {fieldOffset}";
        }

        var targetMethod = ConcreteTypeRef.From(
            QualifiedName.From("std", "derived_create"),
            ptrType
        );
        var funcRef = Backend.GenerateKeyName(new FunctionRef
        {
            TargetMethod = targetMethod
        });

        var destValue = $"inst_{inst.Id}_derived";
        Writer.WriteLine($"var {destValue} = {funcRef}(heap, {boxPtr}, {ptrValue});");
        var returnType = new DerivedRefTypeRef(ptrType, true);

        StoreSlot(inst.ResultSlot, destValue, returnType);
        MarkActive(inst.ResultSlot);
    }

    private void CompileRefBorrow(RefBorrowInst inst)
    {
        var (type, value) = LoadSlot(inst.SourceSlot, $"inst_{inst.Id}_load");


        TypeRef innerType;
        string resultValue;

        if (type is ReferenceTypeRef referenceTypeRef)
        {
            if (!referenceTypeRef.StrongRef)
            {
                throw new Exception("Cannot borrow weak reference");
            }

            innerType = referenceTypeRef.InnerType;
            var offset = Backend.GetBoxValueOffset((ConcreteTypeRef) referenceTypeRef.InnerType);
            resultValue = $"{value} + {offset}";
        }
        else if (type is DerivedRefTypeRef derivedRefTypeRef)
        {
            if (!derivedRefTypeRef.StrongRef)
            {
                throw new Exception("Cannot borrow weak reference");
            }

            innerType = derivedRefTypeRef.InnerType;
            var offset = Backend.GetDerivedBoxValueFieldOffset((ConcreteTypeRef) derivedRefTypeRef.InnerType);
            resultValue = ExtractUSize(value, offset, $"inst_{inst.Id}_db");
        }
        else
        {
            throw new Exception("Source is not a reference");
        }

        var targetType = new BorrowTypeRef(innerType, false);

        StoreSlot(inst.ResultSlot, resultValue, targetType);
        MarkActive(inst.ResultSlot);
    }

    private string ExtractUSize(string source, uint offset, string name)
    {
        Writer.WriteLine($"var {name}_view = new DataView({source});");
        Writer.WriteLine($"var {name}_value = {name}_view.getUint32({offset}, heap.le);");
        return $"{name}_value";
    }

    private void CompileStoreIndirectInst(StoreIndirectInst inst)
    {
        var (tgtType, tgt) = LoadSlot(inst.TargetSlot, $"inst_{inst.Id}_tgt");
        var (valType, valPtr) = GetSlotRef(inst.ValueSlot);
        var properties = Store.GetCopyProperties(valType);
        var slotLifetime = GetLifetime(inst).GetSlot(inst.ValueSlot);

        var dropExisting = false;

        switch (tgtType)
        {
            case BaseTypeRef:
                throw new Exception("Base type is not valid ptr");
                break;
            case BorrowTypeRef borrowTypeRef:
                if (!borrowTypeRef.MutableRef)
                {
                    throw new Exception("Cannot store into a non-mutable borrow");
                }

                if (!Equals(borrowTypeRef.InnerType, valType))
                {
                    throw new Exception("Value type does not match borrowed type");
                }

                dropExisting = true;
                break;
            case PointerTypeRef pointerTypeRef:
                if (!pointerTypeRef.MutableRef)
                {
                    throw new Exception("Cannot store into a non-mutable pointer");
                }

                if (!Equals(pointerTypeRef.InnerType, valType))
                {
                    throw new Exception("Value type does not match pointer type");
                }

                break;
            case ReferenceTypeRef:
                throw new NotImplementedException();
            default:
                throw new ArgumentOutOfRangeException(nameof(tgtType));
        }

        string val;
        if (slotLifetime.Status == SlotStatus.Moved)
        {
            (_, val) = LoadSlot(inst.ValueSlot, $"inst_{inst.Id}_value");
            MarkMoved(inst.ValueSlot);
        }
        else if (properties.CanCopy)
        {
            val = GenerateCopy(valType, properties, valPtr, $"inst_{inst.Id}_value");
        }
        else
        {
            throw new Exception("Value is not moveable");
        }

        if (dropExisting)
        {
            // var value = $"inst_{inst.Id}_existing_value";
            // Writer.WriteLine($"var {value} = {Backend.BuildLoad(valType, tgt)};");
            PerformDrop(tgt, valType);
        }

        Writer.WriteLine(Backend.BuildStore(valType, tgt, val));
    }

    private void CompileLoadIndirectInst(LoadIndirectInst inst)
    {
        var (addrType, addr) = LoadSlot(inst.AddressSlot, $"inst_{inst.Id}_addr");

        TypeRef innerTypeRef;
        switch (addrType)
        {
            case BaseTypeRef:
                throw new Exception("Base type is not valid ptr");
                break;
            case BorrowTypeRef borrowTypeRef:
                innerTypeRef = borrowTypeRef.InnerType;
                break;
            case PointerTypeRef pointerTypeRef:
                innerTypeRef = pointerTypeRef.InnerType;
                break;
            case ReferenceTypeRef:
                throw new NotImplementedException();
            default:
                throw new ArgumentOutOfRangeException(nameof(addr));
        }

        var properties = Store.GetCopyProperties(innerTypeRef);

        string destValue;
        if (properties.CanCopy)
        {
            destValue = GenerateCopy(innerTypeRef, properties, addr, $"inst_{inst.Id}");
        }
        else
        {
            throw new NotImplementedException("Moves");
        }

        StoreSlot(inst.TargetSlot, destValue, innerTypeRef);
        MarkActive(inst.TargetSlot);
    }

    private void CompileReturnInst(ReturnInst inst)
    {
        if (inst.ReturnSlot.HasValue)
        {
            var (retType, retValue) = LoadSlot(inst.ReturnSlot.Value, $"inst_{inst.Id}_load");
            MarkMoved(inst.ReturnSlot.Value);
            if (!Equals(retType, FunctionContext.ResolveRef(_func.ReturnType)))
            {
                throw new Exception("Invalid return type");
            }

            Writer.WriteLine($"return_value = {retValue};");
        }

        var currentScope = CurrentBlock.Scope;

        while (currentScope.ParentScope != null)
        {
            var tgtSlot = _scopeJumpTargets[currentScope.Id];
            var parentBlock = _scopeJumpBlocks[currentScope.ParentScope.Id];
            Writer.WriteLine($"{tgtSlot} = \"{parentBlock}\";");
            currentScope = currentScope.ParentScope;
        }

        {
            var tgtSlot = _scopeJumpTargets[currentScope.Id];
            Writer.WriteLine($"{tgtSlot} = \"return\";");
        }

        Writer.WriteLine($"__block = \"{_scopeJumpBlocks[CurrentBlock.Scope.Id]}\";");
        Writer.WriteLine("continue __block_loop;");
    }

    private void CompileStaticCallInst(StaticCallInst inst)
    {
        Function funcDef;
        GenericContext funcContext;
        FunctionRef key;
        ConcreteTypeRef targetMethod;

        if (inst.TargetType != null)
        {
            var targetType = (ConcreteTypeRef) FunctionContext.ResolveRef(inst.TargetType);

            targetMethod = new ConcreteTypeRef(
                inst.TargetMethod.Name,
                FunctionContext.ResolveRefs(inst.TargetMethod.GenericParams)
            );

            var resolved = Store.LookupImplementation(
                targetType,
                inst.TargetImplementation,
                targetMethod.Name.Parts.Single()
            );
            funcDef = resolved.Function;

            var type = Store.Lookup(targetType.Name);
            funcContext = new GenericContext(
                null,
                type.GenericParams,
                targetType.GenericParams,
                targetType
            );

            key = new FunctionRef
            {
                TargetType = targetType,
                TargetImplementation = inst.TargetImplementation,
                TargetMethod = targetMethod
            };
        }
        else
        {
            targetMethod = (ConcreteTypeRef) FunctionContext.ResolveRef(inst.TargetMethod);
            key = new FunctionRef
            {
                TargetMethod = targetMethod
            };

            if (Backend.GetIntrinsic(targetMethod.Name, out var intrinsic))
            {
                intrinsic(this, inst, key);
                return;
            }

            funcDef = Store.Lookup<Function>(targetMethod.Name);
            funcContext = GenericContext.Default;
        }

        if (funcDef == null)
        {
            throw new Exception($"Failed to find unit for {targetMethod}");
        }

        if (targetMethod.GenericParams.Length > 0)
        {
            funcContext = new GenericContext(
                funcContext,
                funcDef.GenericParams,
                targetMethod.GenericParams,
                funcContext.ThisRef
            );
        }

        var name = $"inst_{inst.Id}";
        var funcRef = Backend.GenerateKeyName(key);

        if (funcDef.Parameters.Count != inst.Arguments.Count)
        {
            throw new Exception("Invalid number of arguments");
        }

        var args = new List<string>();

        for (var i = 0; i < funcDef.Parameters.Count; i++)
        {
            var param = funcDef.Parameters[i];
            var paramType = funcContext.ResolveRef(param.Type);

            var (argType, argPtr) = GetSlotRef(inst.Arguments[i]);
            var properties = Store.GetCopyProperties(argType);
            var slotLifetime = GetLifetime(inst).GetSlot(inst.Arguments[i]);

            string argVal;
            if (slotLifetime.Status == SlotStatus.Moved)
            {
                (_, argVal) = LoadSlot(inst.Arguments[i], $"{name}_param_{param.Name}");
                MarkMoved(inst.Arguments[i]);
            }
            else if (properties.CanCopy)
            {
                argVal = GenerateCopy(argType, properties, argPtr, $"{name}_param_{param.Name}");
            }
            else
            {
                throw new Exception("Value is not moveable");
            }

            bool matches;
            switch (argType)
            {
                case BaseTypeRef:
                case ReferenceTypeRef:
                case DerivedRefTypeRef:
                    matches = Equals(argType, paramType);
                    break;
                case BorrowTypeRef borrowTypeRef:
                    if (paramType is BorrowTypeRef paramBorrowType)
                    {
                        matches = Equals(borrowTypeRef.InnerType, paramBorrowType.InnerType) &&
                                  (!paramBorrowType.MutableRef || borrowTypeRef.MutableRef);
                    }
                    else
                    {
                        matches = false;
                    }

                    break;
                case PointerTypeRef pointerTypeRef:
                    if (paramType is PointerTypeRef paramPointerType)
                    {
                        matches = Equals(pointerTypeRef.InnerType, paramPointerType.InnerType) &&
                                  (!paramPointerType.MutableRef || pointerTypeRef.MutableRef);
                    }
                    else
                    {
                        matches = false;
                    }

                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(argType));
            }

            if (!matches)
            {
                throw new Exception($"Argument does not match parameter type for {param.Name}");
            }

            args.Add(argVal);
        }

        if (inst.ResultSlot != null)
        {
            if (funcDef.ReturnType == null)
            {
                throw new Exception("Function does not return a value");
            }

            var returnType = funcContext.ResolveRef(funcDef.ReturnType);
            Writer.WriteLine($"{name}_ret = {funcRef}(heap{string.Join("", args.Select(x => $", {x}"))});");
            StoreSlot(inst.ResultSlot.Value, $"{name}_ret", returnType);
            MarkActive(inst.ResultSlot.Value);
        }
        else
        {
            if (funcDef.ReturnType != null)
            {
                throw new Exception("Function returns a value which is unused");
            }

            Writer.WriteLine($"{funcRef}(heap{string.Join("", args.Select(x => $", {x}"))});");
        }
    }

    private void CompileJumpVariantInst(JumpVariantInst inst)
    {
        var variantItemTypeRef = (ConcreteTypeRef) FunctionContext.ResolveRef(inst.VariantItemType);
        var (varType, rawVarRef) = GetSlotRef(inst.VariantSlot);

        string varRef;
        switch (varType)
        {
            case BaseTypeRef:
                varRef = rawVarRef;
                break;
            case PointerTypeRef:
            case BorrowTypeRef:
                (_, varRef) = LoadSlot(inst.VariantSlot, $"inst_{inst.Id}_laddr");
                break;
            case ReferenceTypeRef referenceTypeRef:
                throw new NotImplementedException();
            default:
                throw new ArgumentOutOfRangeException();
        }

        var expectedVarType = new ConcreteTypeRef(
            new QualifiedName(
                true,
                variantItemTypeRef.Name.Parts.RemoveAt(variantItemTypeRef.Name.Parts.Length - 1)
            ),
            variantItemTypeRef.GenericParams
        );

        if (!Equals(varType.GetBaseType(), expectedVarType))
        {
            throw new Exception($"Variant type mismatch {varType} {expectedVarType}");
        }

        var variant = Store.Lookup<Variant>(expectedVarType.Name);
        var itemName = variantItemTypeRef.Name.Parts.Last();
        var index = variant.Items.FindIndex(x => x.Name == itemName);

        var typeVal = $"inst_{inst.Id}_type";
        Writer.WriteLine($"var {typeVal} = heap.readU8({varRef});");

        Writer.WriteLine($"if ({typeVal} == {index}) {{");
        Writer.Indent(1);

        var itemAddr = $"{varRef} + 1";
        switch (varType)
        {
            case BaseTypeRef:
            {
                var properties = Store.GetCopyProperties(variantItemTypeRef);

                string destValue;
                if (properties.CanCopy)
                {
                    destValue = GenerateCopy(variantItemTypeRef, properties, itemAddr, $"inst_{inst.Id}_copy");
                }
                else
                {
                    throw new NotImplementedException("Moves");
                }

                StoreSlot(inst.ItemSlot, destValue, variantItemTypeRef, true);
                MarkActive(inst.ItemSlot);
                break;
            }
            case BorrowTypeRef borrowTypeRef:
                StoreSlot(
                    inst.ItemSlot,
                    itemAddr,
                    new BorrowTypeRef(
                        variantItemTypeRef,
                        borrowTypeRef.MutableRef
                    ),
                    true
                );
                MarkActive(inst.ItemSlot);
                break;
            case PointerTypeRef pointerTypeRef:
                throw new NotImplementedException();
            case ReferenceTypeRef referenceTypeRef:
                throw new NotImplementedException();
            default:
                throw new ArgumentOutOfRangeException();
        }

        Writer.WriteLine($"__block = \"{GetJumpTrampoline(CurrentBlock.Id, inst.TargetBlock)}\";");
        Writer.WriteLine("continue __block_loop;");

        Writer.Indent(-1);
        Writer.WriteLine("} else {");
        Writer.Indent(1);
        Writer.WriteLine($"__block = \"{GetJumpTrampoline(CurrentBlock.Id, inst.ElseBlock)}\";");
        Writer.WriteLine("continue __block_loop;");
        Writer.Indent(-1);
        Writer.WriteLine("}");
    }

    private void CompileJumpInst(JumpInst inst)
    {
        if (inst.ConditionSlot.HasValue)
        {
            var (condType, cond) = LoadSlot(inst.ConditionSlot.Value, $"inst_{inst.Id}_cond");
            if (!Equals(condType, PrimitiveKind.Bool.GetRef()))
            {
                throw new Exception("Invalid condition type");
            }

            Writer.WriteLine(
                $"__block = OxideMath.toBool({cond}) ? \"{GetJumpTrampoline(CurrentBlock.Id, inst.TargetBlock)}\" :" +
                $" \"{GetJumpTrampoline(CurrentBlock.Id, inst.ElseBlock)}\";"
            );
            Writer.WriteLine("continue __block_loop;");
        }
        else
        {
            Writer.WriteLine($"__block = \"{GetJumpTrampoline(CurrentBlock.Id, inst.TargetBlock)}\";");
            Writer.WriteLine("continue __block_loop;");
        }
    }

    private Scope[] GetScopeHierarchy(Scope scope)
    {
        var scopes = new List<Scope>();

        var current = scope;
        while (current != null)
        {
            scopes.Insert(0, current);
            current = current.ParentScope;
        }

        return scopes.ToArray();
    }

    private string GetJumpTrampoline(int from, int to)
    {
        var key = (from, to);
        if (_jumpTrampolines.TryGetValue(key, out var blockRef))
        {
            return blockRef;
        }

        var fromBlock = _func.Blocks.Single(x => x.Id == from);
        var targetBlock = _func.Blocks.Single(x => x.Id == to);

        var fromScopes = GetScopeHierarchy(fromBlock.Scope);
        var targetScopes = GetScopeHierarchy(targetBlock.Scope);
        var matchesUntil = -1;
        for (var i = 0; i < Math.Min(fromScopes.Length, targetScopes.Length); i++)
        {
            if (fromScopes[i].Id != targetScopes[i].Id)
            {
                break;
            }

            matchesUntil = i;
        }

        if (matchesUntil == -1)
        {
            throw new Exception("No common scopes");
        }

        if (fromScopes.Length <= targetScopes.Length && matchesUntil == fromScopes.Length - 1)
        {
            _jumpTrampolines.Add(key, $"{to}");
            return $"{to}";
        }

        var original = Writer;

        blockRef = $"jump_f{from}_t{to}";
        _jumpTrampolines.Add(key, blockRef);

        Writer = new JsWriter();
        BlockWriters[blockRef] = Writer;

        var currentScope = fromBlock.Scope;

        while (currentScope.ParentScope != fromScopes[matchesUntil])
        {
            if (currentScope.ParentScope == null)
            {
                throw new Exception("Failed to find common parent");
            }

            var tgtSlot = _scopeJumpTargets[currentScope.Id];
            var parentBlock = _scopeJumpBlocks[currentScope.ParentScope.Id];
            Writer.WriteLine($"{tgtSlot} = \"{parentBlock}\";");
            currentScope = currentScope.ParentScope;
        }

        {
            var tgtSlot = _scopeJumpTargets[currentScope.Id];
            Writer.WriteLine($"{tgtSlot} = \"{to}\";");
        }

        Writer.WriteLine($"__block = \"{_scopeJumpBlocks[fromBlock.Scope.Id]}\";");
        Writer.WriteLine("continue __block_loop;");
        Writer = original;

        return blockRef;
    }

    private void CompileComparisonInst(ComparisonInst inst)
    {
        var name = $"inst_{inst.Id}";
        var (leftType, left) = LoadSlot(inst.LhsValue, $"{name}_left");
        var (rightType, right) = LoadSlot(inst.RhsValue, $"{name}_right");
        string value;

        if (!Equals(leftType, rightType))
        {
            throw new Exception("Lhs and rhs have different type");
        }

        var integer = IsIntegerBacked(leftType);
        if (!integer)
        {
            throw new NotImplementedException("Comparison of non-integers not implemented");
        }

        var signed = IsSignedInteger(leftType);

        switch (inst.Op)
        {
            case ComparisonInst.Operation.Eq:
                value = $"OxideMath.equal({left}, {right})";
                break;
            case ComparisonInst.Operation.NEq:
                value = $"OxideMath.not(OxideMath.equal({left}, {right}))";
                break;
            case ComparisonInst.Operation.GEq:
                value = $"OxideMath.fromBool(OxideMath.{(signed ? "cmp" : "ucmp")}({left}, {right}) >= 0)";
                break;
            case ComparisonInst.Operation.LEq:
                value = $"OxideMath.fromBool(OxideMath.{(signed ? "cmp" : "ucmp")}({left}, {right}) <= 0)";
                break;
            case ComparisonInst.Operation.Gt:
                value = $"OxideMath.fromBool(OxideMath.{(signed ? "cmp" : "ucmp")}({left}, {right}) == 1)";
                break;
            case ComparisonInst.Operation.Lt:
                value = $"OxideMath.fromBool(OxideMath.{(signed ? "cmp" : "ucmp")}({left}, {right}) == -1)";
                break;
            default:
                throw new ArgumentOutOfRangeException();
        }

        StoreSlot(inst.ResultSlot, value, PrimitiveKind.Bool.GetRef());
        MarkActive(inst.ResultSlot);
    }

    private void CompileArithmeticInstruction(ArithmeticInst inst)
    {
        var name = $"inst_{inst.Id}";
        var (leftType, left) = LoadSlot(inst.LhsValue, $"{name}_left");
        var (rightType, right) = LoadSlot(inst.RhsValue, $"{name}_right");
        string value;

        if (!Equals(leftType, rightType))
        {
            throw new Exception("Lhs and rhs have different type");
        }

        var integer = IsIntegerBacked(leftType);
        if (!integer)
        {
            throw new NotImplementedException("Arithmetic of non-integers not implemented");
        }

        var signed = IsSignedInteger(leftType);

        switch (inst.Op)
        {
            case ArithmeticInst.Operation.Add:
                value = $"OxideMath.add({left}, {right})";
                break;
            case ArithmeticInst.Operation.Minus:
                value = $"OxideMath.sub({left}, {right})";
                break;
            case ArithmeticInst.Operation.LogicalAnd:
                value = $"OxideMath.and({left}, {right})";
                break;
            case ArithmeticInst.Operation.LogicalOr:
                value = $"OxideMath.or({left}, {right})";
                break;
            case ArithmeticInst.Operation.Mod:
                value = $"OxideMath.{(signed ? "mod" : "umod")}({left}, {right})";
                break;
            case ArithmeticInst.Operation.Multiply:
                value = $"OxideMath.{(signed ? "mult" : "umult")}({left}, {right})";
                break;
            case ArithmeticInst.Operation.Divide:
                value = $"OxideMath.{(signed ? "div" : "udiv")}({left}, {right})";
                break;
            default:
                throw new ArgumentOutOfRangeException();
        }

        StoreSlot(inst.ResultSlot, value, leftType);
        MarkActive(inst.ResultSlot);
    }

    private void CompileUnaryInstruction(UnaryInst inst)
    {
        var name = $"inst_{inst.Id}";
        var (valueType, value) = LoadSlot(inst.Value, $"{name}_value");
        string result;

        var integer = IsIntegerBacked(valueType);
        if (!integer)
        {
            throw new NotImplementedException("Unary operations on non-integers not implemented");
        }

        switch (inst.Op)
        {
            case UnaryInst.Operation.Not:
                result = $"OxideMath.not({value})";
                break;
            default:
                throw new ArgumentOutOfRangeException();
        }

        StoreSlot(inst.ResultSlot, result, valueType);
        MarkActive(inst.ResultSlot);
    }

    private void CompileConstInst(ConstInst inst)
    {
        var (valType, value) = GetConstValue(inst.Value, inst.ConstType);
        StoreSlot(inst.TargetSlot, value, valType);
        MarkActive(inst.TargetSlot);
    }

    public (TypeRef t, string v) GetConstValue(object value, PrimitiveKind kind)
    {
        TypeRef valType;
        byte[] bytes;
        switch (kind)
        {
            case PrimitiveKind.I32:
                bytes = BitConverter.GetBytes((int) value);
                valType = PrimitiveKind.I32.GetRef();
                break;
            case PrimitiveKind.U32:
                bytes = BitConverter.GetBytes((uint) value);
                valType = PrimitiveKind.U32.GetRef();
                break;
            case PrimitiveKind.USize:
                bytes = BitConverter.GetBytes((uint) value);
                valType = PrimitiveKind.USize.GetRef();
                break;
            case PrimitiveKind.Bool:
                bytes = new[] {(byte) ((bool) value ? 255 : 0)};
                valType = PrimitiveKind.Bool.GetRef();
                break;
            default:
                throw new ArgumentOutOfRangeException();
        }

        if (!BitConverter.IsLittleEndian)
            Array.Reverse(bytes);

        return (valType, $"Uint8Array.from([{string.Join(", ", bytes.Select(x => x.ToString()))}]).buffer");
    }

    private void CompileMoveInst(MoveInst inst)
    {
        var (type, value) = GetSlotRef(inst.SrcSlot);
        var properties = Store.GetCopyProperties(type);
        var slotLifetime = GetLifetime(inst).GetSlot(inst.SrcSlot);

        string destValue;
        if (slotLifetime.Status == SlotStatus.Moved)
        {
            (_, destValue) = LoadSlot(inst.SrcSlot, $"inst_{inst.Id}");
            MarkMoved(inst.SrcSlot);
        }
        else if (properties.CanCopy)
        {
            destValue = GenerateCopy(type, properties, value, $"inst_{inst.Id}");
        }
        else
        {
            throw new Exception("Value is not moveable");
        }

        DropIfActive(inst.DestSlot, $"inst_{inst.Id}_existing");
        StoreSlot(inst.DestSlot, destValue, type);
        MarkActive(inst.DestSlot);
    }

    public (TypeRef type, string value) GetSlotRef(int slot, bool ignoreChecks = false)
    {
        if (!ignoreChecks && !CurrentBlock.Scope.CanAccessSlot(slot))
        {
            throw new Exception($"Slot is not accessible from {CurrentBlock.Id}");
        }

        return (_slotTypes[slot], $"(__sf + {_slotOffset[slot]})");
    }

    public (TypeRef type, string value) LoadSlot(int slot, string name, bool ignoreChecks = false)
    {
        if (!ignoreChecks && !CurrentBlock.Scope.CanAccessSlot(slot))
        {
            throw new Exception($"Slot is not accessible from {CurrentBlock.Id}");
        }

        var type = _slotTypes[slot];
        var offset = _slotOffset[slot];
        Writer.WriteLine($"var {name} = {Backend.BuildLoad(type, $"__sf + {offset}")};");
        return (type, name);
    }

    public void StoreSlot(int slot, string value, TypeRef type, bool ignoreChecks = false)
    {
        if (!ignoreChecks && !CurrentBlock.Scope.CanAccessSlot(slot))
        {
            throw new Exception($"Slot is not accessible from {CurrentBlock.Id}");
        }

        if (!Equals(type, _slotTypes[slot]))
        {
            throw new Exception("Tried to store incompatible type");
        }

        var offset = _slotOffset[slot];
        Writer.WriteLine(Backend.BuildStore(type, $"__sf + {offset}", value));
    }

    private string GenerateCopy(TypeRef type, CopyProperties properties, string pointer, string name)
    {
        if (!properties.CanCopy)
        {
            throw new ArgumentException("Invalid copy properties");
        }

        if (properties.BitwiseCopy)
        {
            var varName = $"{name}_copy";
            Writer.WriteLine($"var {varName} = {Backend.BuildLoad(type, pointer)};");
            return varName;
        }

        var targetMethod = properties.CopyMethod.TargetMethod;

        Function funcDef;
        GenericContext funcContext;

        if (properties.CopyMethod.TargetType != null)
        {
            var targetType = properties.CopyMethod.TargetType;
            var resolved = Store.LookupImplementation(
                targetType,
                properties.CopyMethod.TargetImplementation,
                properties.CopyMethod.TargetMethod.Name.Parts.Single()
            );
            funcDef = resolved.Function;

            var typeObj = Store.Lookup(targetType.Name);
            funcContext = new GenericContext(
                null,
                typeObj.GenericParams,
                targetType.GenericParams,
                targetType
            );
        }
        else
        {
            funcDef = Store.Lookup<Function>(targetMethod.Name);
            funcContext = GenericContext.Default;
        }

        if (funcDef == null)
        {
            throw new Exception($"Failed to find unit for {targetMethod}");
        }

        if (targetMethod.GenericParams.Length > 0)
        {
            funcContext = new GenericContext(
                funcContext,
                funcDef.GenericParams,
                targetMethod.GenericParams,
                funcContext.ThisRef
            );
        }

        var funcRef = Backend.GenerateKeyName(properties.CopyMethod);

        if (funcDef.Parameters.Count != 1)
        {
            throw new Exception("Invalid number of arguments");
        }

        var param = funcDef.Parameters[0];
        var paramType = funcContext.ResolveRef(param.Type);

        var matches = paramType switch
        {
            BorrowTypeRef borrowTypeRef => Equals(borrowTypeRef.InnerType, type) && !borrowTypeRef.MutableRef,
            PointerTypeRef pointerTypeRef => Equals(pointerTypeRef.InnerType, type) && !pointerTypeRef.MutableRef,
            _ => throw new ArgumentOutOfRangeException(nameof(paramType))
        };

        if (!matches)
        {
            throw new Exception($"Argument does not match parameter type for {param.Name}");
        }

        var returnType = funcContext.ResolveRef(funcDef.ReturnType);
        if (!Equals(returnType, type))
        {
            throw new Exception("Copy function does not return expected value");
        }


        var resultName = $"{name}_copyfunc";
        Writer.WriteLine($"var {resultName} = {funcRef}(heap, {pointer});");
        return resultName;
    }

    private InstructionLifetime GetLifetime(Instruction instruction)
    {
        return FunctionLifetime.InstructionLifetimes[instruction.Id];
    }

    public void MarkActive(int slot)
    {
        if (_slotLivenessMap.TryGetValue(slot, out var valRef))
        {
            Writer.WriteLine($"{valRef} = true;");
        }
    }

    private void MarkMoved(int slot)
    {
        if (_slotLivenessMap.TryGetValue(slot, out var valRef))
        {
            Writer.WriteLine($"{valRef} = false;");
        }
    }

    private bool IsIntegerBacked(TypeRef typeRef)
    {
        if (typeRef is not ConcreteTypeRef concreteTypeRef)
        {
            throw new NotImplementedException("Non direct types");
        }

        var kind = PrimitiveType.GetPossibleKind(concreteTypeRef);
        if (kind == null)
        {
            return false;
        }

        return PrimitiveType.IsInt(kind.Value) || kind.Value == PrimitiveKind.Bool;
    }

    private bool IsSignedInteger(TypeRef typeRef)
    {
        if (typeRef is not ConcreteTypeRef concreteTypeRef)
        {
            throw new NotImplementedException("Non direct types");
        }

        var kind = PrimitiveType.GetPossibleKind(concreteTypeRef);
        if (kind == null)
        {
            return false;
        }

        return PrimitiveType.IsSigned(kind.Value) || kind.Value == PrimitiveKind.Bool;
    }
}