using System;
using System.Collections.Generic;
using System.Linq;
using LLVMSharp.Interop;
using Oxide.Compiler.IR;
using Oxide.Compiler.IR.Instructions;
using Oxide.Compiler.IR.TypeRefs;
using Oxide.Compiler.IR.Types;
using Oxide.Compiler.Middleware;
using Oxide.Compiler.Middleware.Lifetimes;
using Oxide.Compiler.Middleware.Usage;

namespace Oxide.Compiler.Backend.Llvm;

public class FunctionGenerator
{
    public LlvmBackend Backend { get; }

    public LLVMModuleRef Module => Backend.Module;

    public IrStore Store => Backend.Store;

    public LLVMBuilderRef Builder { get; set; }

    public FunctionLifetime FunctionLifetime { get; private set; }

    private Function _func;

    private LLVMValueRef _funcRef;

    private Dictionary<int, LLVMValueRef> _slotMap;
    private Dictionary<int, LLVMValueRef> _slotLivenessMap;
    private Dictionary<int, SlotDeclaration> _slotDefs;
    private Dictionary<int, LLVMBasicBlockRef> _blockMap;
    private Dictionary<int, LLVMBasicBlockRef> _scopeJumpBlocks;
    private Dictionary<int, LLVMValueRef> _scopeJumpTargets;
    private Dictionary<(int, int), LLVMBasicBlockRef> _jumpTrampolines;
    private LLVMBasicBlockRef _returnBlock;
    private int? _returnSlot;

    public Block CurrentBlock { get; set; }

    public GenericContext FunctionContext { get; set; }

    public FunctionGenerator(LlvmBackend backend)
    {
        Backend = backend;
    }

    public void Compile(FunctionRef key, Function func, GenericContext context, FunctionLifetime functionLifetime)
    {
        _func = func;
        FunctionContext = context;
        FunctionLifetime = functionLifetime;

        Builder = Backend.Context.CreateBuilder();

        _funcRef = Backend.GetFunctionRef(key);
        if (func.IsExtern)
        {
            // _funcRef.Linkage = LLVMLinkage.LLVMExternalLinkage;
            return;
        }

        var entryBlock = _funcRef.AppendBasicBlock("entry");

        _blockMap = new Dictionary<int, LLVMBasicBlockRef>();
        foreach (var block in func.Blocks)
        {
            _blockMap.Add(block.Id, _funcRef.AppendBasicBlock($"scope_{block.Scope.Id}_block_{block.Id}"));
        }

        // Generate entry block
        Builder.PositionAtEnd(entryBlock);

        // Create slots
        _slotMap = new Dictionary<int, LLVMValueRef>();
        _slotLivenessMap = new Dictionary<int, LLVMValueRef>();
        _slotDefs = new Dictionary<int, SlotDeclaration>();

        // Create storage slot for return value
        if (_func.ReturnType != null)
        {
            var varName = $"return_value";
            var returnType = FunctionContext.ResolveRef(func.ReturnType);
            var varType = Backend.ConvertType(returnType);
            _returnSlot = -1;
            _slotMap.Add(_returnSlot.Value, Builder.BuildAlloca(varType, varName));
            _slotDefs.Add(_returnSlot.Value, new SlotDeclaration
            {
                Id = _returnSlot.Value,
                Mutable = true,
                Type = returnType
            });
        }

        // TODO: Reuse variable slots
        foreach (var scope in func.Scopes)
        {
            foreach (var slotDef in scope.Slots.Values)
            {
                var varName = $"scope_{scope.Id}_slot_{slotDef.Id}_{slotDef.Name ?? "autogen"}";
                var resolvedType = FunctionContext.ResolveRef(slotDef.Type);
                var varType = Backend.ConvertType(resolvedType);

                _slotMap.Add(slotDef.Id, Builder.BuildAlloca(varType, varName));

                if (Backend.GetDropFunctionRef(resolvedType) != null)
                {
                    _slotLivenessMap.Add(slotDef.Id, Builder.BuildAlloca(LLVMTypeRef.Int1, $"{varName}:live"));
                }

                _slotDefs.Add(slotDef.Id, new SlotDeclaration
                {
                    Id = slotDef.Id,
                    Name = slotDef.Name,
                    Mutable = slotDef.Mutable,
                    ParameterSource = slotDef.ParameterSource,
                    Type = resolvedType
                });
            }
        }

        // Create scope jump targets
        _scopeJumpTargets = new Dictionary<int, LLVMValueRef>();
        _scopeJumpBlocks = new Dictionary<int, LLVMBasicBlockRef>();
        _jumpTrampolines = new Dictionary<(int, int), LLVMBasicBlockRef>();
        foreach (var scope in func.Scopes)
        {
            var target = Builder.BuildAlloca(
                LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0),
                $"scope_{scope.Id}_jump_tgt"
            );
            _scopeJumpTargets.Add(scope.Id, target);

            var block = _funcRef.AppendBasicBlock($"scope_{scope.Id}_jump");
            _scopeJumpBlocks.Add(scope.Id, block);
        }

        // Default to all slots inactive
        foreach (var scope in func.Scopes)
        {
            foreach (var slotDef in scope.Slots.Values)
            {
                MarkMoved(slotDef.Id);
            }
        }

        // Load parameters
        foreach (var scope in func.Scopes)
        {
            if (scope.ParentScope != null)
            {
                continue;
            }

            foreach (var varDef in scope.Slots.Values)
            {
                if (!varDef.ParameterSource.HasValue)
                {
                    continue;
                }

                Builder.BuildStore(_funcRef.Params[varDef.ParameterSource.Value], _slotMap[varDef.Id]);
                MarkActive(varDef.Id);
            }
        }

        // Jump to first block
        Builder.BuildBr(_blockMap[func.EntryBlock]);

        _returnBlock = _funcRef.AppendBasicBlock("return");
        Builder.PositionAtEnd(_returnBlock);

        if (_returnSlot.HasValue)
        {
            var (retType, retVal) = LoadSlot(_returnSlot.Value, "loaded_return_value", true);
            if (!Equals(retType, FunctionContext.ResolveRef(_func.ReturnType)))
            {
                throw new Exception("Incompatible return type");
            }

            Builder.BuildRet(retVal);
        }
        else
        {
            Builder.BuildRetVoid();
        }

        // Create paths for scopes
        foreach (var scope in func.Scopes)
        {
            CreateScopeJumpBlock(scope);
        }

        // Compile bodies
        foreach (var block in func.Blocks)
        {
            CompileBlock(block);
        }

        Builder.Dispose();
    }

    private void CreateScopeJumpBlock(Scope scope)
    {
        var name = $"scope_{scope.Id}_jump";
        var block = _scopeJumpBlocks[scope.Id];

        var finalBlock = _funcRef.AppendBasicBlock($"{name}_final");

        (SlotDeclaration sd, LLVMValueRef dropFunc)[] withDrops = scope.Slots.Values.Select(x =>
        {
            var resolvedType = FunctionContext.ResolveRef(x.Type);
            var dropFunc = Backend.GetDropFunctionRef(resolvedType);
            return (x, dropFunc);
        }).Where(x => x.dropFunc != null).Reverse().ToArray();

        var condBlocks = new LLVMBasicBlockRef[withDrops.Length];
        for (var i = 0; i < withDrops.Length; i++)
        {
            var sd = withDrops[i].sd;
            condBlocks[i] = _funcRef.AppendBasicBlock($"{name}_drop_{sd.Id}_check");
        }

        for (var i = 0; i < withDrops.Length; i++)
        {
            var sd = withDrops[i].sd;
            var dropFunc = withDrops[i].dropFunc;
            var nextBlock = i == withDrops.Length - 1 ? finalBlock : condBlocks[i + 1];

            var dropBlock = _funcRef.AppendBasicBlock($"{name}_drop_{sd.Id}");
            Builder.PositionAtEnd(dropBlock);
            var (_, valuePtr) = GetSlotRef(sd.Id, true);
            var value = Builder.BuildLoad(valuePtr, $"{name}_drop_{sd.Id}_value");
            Builder.BuildCall(dropFunc, new[] { value });
            MarkMoved(sd.Id);
            Builder.BuildBr(nextBlock);

            // Create condition block
            Builder.PositionAtEnd(condBlocks[i]);
            var livePtr = _slotLivenessMap[sd.Id];
            var liveValue = Builder.BuildLoad(livePtr, $"{name}_drop_{sd.Id}_liveness");
            Builder.BuildCondBr(liveValue, dropBlock, nextBlock);
        }

        Builder.PositionAtEnd(block);
        Builder.BuildBr(condBlocks.Length > 0 ? condBlocks[0] : finalBlock);

        Builder.PositionAtEnd(finalBlock);
        var tgtSlot = _scopeJumpTargets[scope.Id];
        var tgt = Builder.BuildLoad(tgtSlot, $"{name}_jump_tgt");

        var dests = new List<LLVMBasicBlockRef>();

        if (scope.ParentScope != null)
        {
            dests.Add(_scopeJumpBlocks[scope.ParentScope.Id]);

            var todo = new Stack<Scope>();
            todo.Push(scope.ParentScope);

            while (todo.TryPop(out var current))
            {
                foreach (var otherBlock in _func.Blocks)
                {
                    if (otherBlock.Scope != current)
                    {
                        continue;
                    }

                    dests.Add(_blockMap[otherBlock.Id]);
                }

                foreach (var otherScope in _func.Scopes)
                {
                    if (otherScope.ParentScope != current || otherScope == scope)
                    {
                        continue;
                    }

                    todo.Push(otherScope);
                }
            }
        }
        else
        {
            dests.Add(_returnBlock);
        }

        var indirectBr = Builder.BuildIndirectBr(tgt, (uint)dests.Count);
        foreach (var dest in dests)
        {
            indirectBr.AddDestination(dest);
        }
    }

    private void CompileBlock(Block block)
    {
        CurrentBlock = block;

        var mainBlock = _blockMap[block.Id];
        Builder.PositionAtEnd(mainBlock);

        foreach (var instruction in block.Instructions)
        {
            CompileInstruction(instruction);
        }

        CurrentBlock = null;
    }

    private void CompileInstruction(Instruction instruction)
    {
        switch (instruction)
        {
            case MoveInst moveInst:
                CompileMoveInst(moveInst);
                break;
            case ConstInst constInst:
                CompileConstInst(constInst);
                break;
            case LoadEnumInst loadEnumInst:
                CompileLoadEnumInst(loadEnumInst);
                break;
            case ArithmeticInst arithmeticInst:
                CompileArithmeticInstruction(arithmeticInst);
                break;
            case UnaryInst unaryInst:
                CompileUnaryInstruction(unaryInst);
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

    private SlotDeclaration GetSlot(int slot)
    {
        if (!CurrentBlock.Scope.CanAccessSlot(slot))
        {
            throw new Exception($"Slot is not accessible from {CurrentBlock.Id}");
        }

        return _slotDefs[slot];
    }

    public void StoreSlot(int slot, LLVMValueRef value, TypeRef type, bool ignoreChecks = false)
    {
        if (!ignoreChecks && !CurrentBlock.Scope.CanAccessSlot(slot))
        {
            throw new Exception($"Slot is not accessible from {CurrentBlock.Id}");
        }

        if (!Equals(type, _slotDefs[slot].Type))
        {
            throw new Exception("Tried to store incompatible type");
        }

        Builder.BuildStore(value, _slotMap[slot]);
    }

    public (TypeRef type, LLVMValueRef value) LoadSlot(int slot, string name, bool ignoreChecks = false)
    {
        if (!ignoreChecks && !CurrentBlock.Scope.CanAccessSlot(slot))
        {
            throw new Exception($"Slot is not accessible from {CurrentBlock.Id}");
        }

        return (_slotDefs[slot].Type, Builder.BuildLoad(_slotMap[slot], name));
    }

    public (TypeRef type, LLVMValueRef value) GetSlotRef(int slot, bool ignoreChecks = false)
    {
        if (!ignoreChecks && !CurrentBlock.Scope.CanAccessSlot(slot))
        {
            throw new Exception($"Slot is not accessible from {CurrentBlock.Id}");
        }

        return (_slotDefs[slot].Type, _slotMap[slot]);
    }

    private InstructionLifetime GetLifetime(Instruction instruction)
    {
        return FunctionLifetime.InstructionLifetimes[instruction.Id];
    }

    public void MarkActive(int slot)
    {
        if (_slotLivenessMap.TryGetValue(slot, out var valRef))
        {
            Builder.BuildStore(LLVMValueRef.CreateConstInt(LLVMTypeRef.Int1, 1), valRef);
        }
    }

    private void MarkMoved(int slot)
    {
        if (_slotLivenessMap.TryGetValue(slot, out var valRef))
        {
            Builder.BuildStore(LLVMValueRef.CreateConstInt(LLVMTypeRef.Int1, 0), valRef);
        }
    }

    private void PerformDrop(LLVMValueRef value, TypeRef typeRef)
    {
        var dropFunc = Backend.GetDropFunctionRef(typeRef);
        if (dropFunc == null)
        {
            return;
        }

        Builder.BuildCall(dropFunc, new[] { value });
    }

    private void DropIfActive(int slotId, string name)
    {
        if (!_slotLivenessMap.TryGetValue(slotId, out var livePtr))
        {
            return;
        }

        var finalBlock = _funcRef.AppendBasicBlock($"{name}_resume");
        var dropBlock = _funcRef.AppendBasicBlock($"{name}_drop");

        var liveValue = Builder.BuildLoad(livePtr, $"{name}_liveness");
        Builder.BuildCondBr(liveValue, dropBlock, finalBlock);

        Builder.PositionAtEnd(dropBlock);
        var (valueType, valuePtr) = GetSlotRef(slotId, true);
        var value = Builder.BuildLoad(valuePtr, $"{name}_drop_value");
        PerformDrop(value, valueType);
        MarkMoved(slotId);
        Builder.BuildBr(finalBlock);

        Builder.PositionAtEnd(finalBlock);
    }

    private void CompileMoveInst(MoveInst inst)
    {
        var (type, value) = GetSlotRef(inst.SrcSlot);
        var properties = Store.GetCopyProperties(type);
        var slotLifetime = GetLifetime(inst).GetSlot(inst.SrcSlot);

        LLVMValueRef destValue;
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

    private LLVMValueRef GenerateCopy(TypeRef type, CopyProperties properties, LLVMValueRef pointer, string name)
    {
        if (!properties.CanCopy)
        {
            throw new ArgumentException("Invalid copy properties");
        }

        if (properties.BitwiseCopy)
        {
            return Builder.BuildLoad(pointer, $"{name}_copy");
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

        var funcRef = Backend.GetFunctionRef(properties.CopyMethod);

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

        return Builder.BuildCall(funcRef, new[] { pointer }, $"{name}_copyfunc");
    }

    private void CompileConstInst(ConstInst inst)
    {
        var constValue = ConvertConstant(inst.ConstType, inst.Value);
        StoreSlot(inst.TargetSlot, constValue.value, constValue.ty);
        MarkActive(inst.TargetSlot);
    }

    private void CompileLoadEnumInst(LoadEnumInst inst)
    {
        var oxEnum = Store.Lookup<OxEnum>(inst.EnumName);
        if (!oxEnum.Items.TryGetValue(inst.ItemName, out var enumValue))
        {
            throw new Exception($"Invalid enum name {inst.EnumName}");
        }

        var constValue = ConvertConstant(oxEnum.UnderlyingType, enumValue);

        StoreSlot(inst.TargetSlot, constValue.value, ConcreteTypeRef.From(inst.EnumName));
        MarkActive(inst.TargetSlot);
    }

    private (LLVMValueRef value, TypeRef ty) ConvertConstant(PrimitiveKind kind, object sourceValue)
    {
        LLVMValueRef value;
        TypeRef valType;
        switch (kind)
        {
            case PrimitiveKind.I32:
                value = LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, (ulong)(int)sourceValue, true);
                valType = PrimitiveKind.I32.GetRef();
                break;
            case PrimitiveKind.Bool:
                value = LLVMValueRef.CreateConstInt(LLVMTypeRef.Int1, (ulong)((bool)sourceValue ? 1 : 0), true);
                valType = PrimitiveKind.Bool.GetRef();
                break;
            default:
                throw new ArgumentOutOfRangeException();
        }

        return (value, valType);
    }

    private void CompileArithmeticInstruction(ArithmeticInst inst)
    {
        var name = $"inst_{inst.Id}";
        var (leftType, left) = LoadSlot(inst.LhsValue, $"{name}_left");
        var (rightType, right) = LoadSlot(inst.RhsValue, $"{name}_right");
        LLVMValueRef value;

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
                value = Builder.BuildAdd(left, right, name);
                break;
            case ArithmeticInst.Operation.Minus:
                value = Builder.BuildSub(left, right, name);
                break;
            case ArithmeticInst.Operation.LogicalAnd:
                value = Builder.BuildAnd(left, right, name);
                break;
            case ArithmeticInst.Operation.LogicalOr:
                value = Builder.BuildOr(left, right, name);
                break;
            case ArithmeticInst.Operation.Mod:
                value = signed ? Builder.BuildSRem(left, right, name) : Builder.BuildURem(left, right, name);
                break;
            case ArithmeticInst.Operation.Multiply:
                value = Builder.BuildMul(left, right, name);
                break;
            case ArithmeticInst.Operation.Divide:
                value = signed ? Builder.BuildSDiv(left, right, name) : Builder.BuildUDiv(left, right, name);
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
        LLVMValueRef result;

        var integer = IsIntegerBacked(valueType);
        if (!integer)
        {
            throw new NotImplementedException("Unary operations on non-integers not implemented");
        }

        switch (inst.Op)
        {
            case UnaryInst.Operation.Not:
                result = Builder.BuildNot(value, name);
                break;
            default:
                throw new ArgumentOutOfRangeException();
        }

        StoreSlot(inst.ResultSlot, result, valueType);
        MarkActive(inst.ResultSlot);
    }

    private void CompileComparisonInst(ComparisonInst inst)
    {
        var name = $"inst_{inst.Id}";
        var (leftType, left) = LoadSlot(inst.LhsValue, $"{name}_left");
        var (rightType, right) = LoadSlot(inst.RhsValue, $"{name}_right");
        LLVMValueRef value;

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
                value = Builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, left, right, name);
                break;
            case ComparisonInst.Operation.NEq:
                value = Builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, left, right, name);
                break;
            case ComparisonInst.Operation.GEq:
                value = Builder.BuildICmp(signed ? LLVMIntPredicate.LLVMIntSGE : LLVMIntPredicate.LLVMIntUGE,
                    left,
                    right, name);
                break;
            case ComparisonInst.Operation.LEq:
                value = Builder.BuildICmp(signed ? LLVMIntPredicate.LLVMIntSLE : LLVMIntPredicate.LLVMIntULE,
                    left,
                    right, name);
                break;
            case ComparisonInst.Operation.Gt:
                value = Builder.BuildICmp(signed ? LLVMIntPredicate.LLVMIntSGT : LLVMIntPredicate.LLVMIntUGT,
                    left,
                    right, name);
                break;
            case ComparisonInst.Operation.Lt:
                value = Builder.BuildICmp(signed ? LLVMIntPredicate.LLVMIntSLT : LLVMIntPredicate.LLVMIntULT,
                    left,
                    right, name);
                break;
            default:
                throw new ArgumentOutOfRangeException();
        }

        StoreSlot(inst.ResultSlot, value, PrimitiveKind.Bool.GetRef());
        MarkActive(inst.ResultSlot);
    }

    private void CompileAllocStructInst(AllocStructInst inst)
    {
        var structType = (ConcreteTypeRef)FunctionContext.ResolveRef(inst.StructType);
        var structDef = Store.Lookup<Struct>(structType.Name);
        var structContext = new GenericContext(null, structDef.GenericParams, structType.GenericParams, null);

        var targetValues = new Dictionary<string, LLVMValueRef>();
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

            LLVMValueRef val;
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

        var finalValue = ZeroInit(structType);

        foreach (var (fname, fvalue) in inst.FieldValues)
        {
            var index = structDef.Fields.FindIndex(x => x.Name == fname);
            finalValue = Builder.BuildInsertValue(
                finalValue,
                targetValues[fname],
                (uint)index,
                $"inst_{inst.Id}_field_{fname}_insert"
            );
        }

        StoreSlot(inst.SlotId, finalValue, structType);
        MarkActive(inst.SlotId);
    }

    private LLVMValueRef ZeroInit(TypeRef tref)
    {
        switch (tref)
        {
            case ConcreteTypeRef concreteTypeRef:
                return ZeroInitConcreteType(concreteTypeRef);
            case DerivedRefTypeRef derivedRefTypeRef:
                return ZeroInitConcreteType(ConcreteTypeRef.From(
                    QualifiedName.From("std", "DerivedBox"),
                    derivedRefTypeRef.InnerType
                ));
            case BorrowTypeRef:
            case PointerTypeRef:
            case ReferenceTypeRef:
                return LLVMValueRef.CreateConstPointerNull(Backend.ConvertType(tref));
            default:
                throw new ArgumentOutOfRangeException(nameof(tref));
        }
    }

    private LLVMValueRef ZeroInitConcreteType(ConcreteTypeRef concreteTypeRef)
    {
        var val = Store.Lookup(concreteTypeRef.Name);
        switch (val)
        {
            case null:
                throw new Exception($"Unable to resolve {concreteTypeRef}");
            case PrimitiveType primitiveType:
                switch (primitiveType.Kind)
                {
                    case PrimitiveKind.Bool:
                    case PrimitiveKind.U8:
                    case PrimitiveKind.I8:
                    case PrimitiveKind.U16:
                    case PrimitiveKind.I16:
                    case PrimitiveKind.U32:
                    case PrimitiveKind.I32:
                    case PrimitiveKind.U64:
                    case PrimitiveKind.I64:
                    case PrimitiveKind.USize:
                    case PrimitiveKind.ISize:
                        return LLVMValueRef.CreateConstInt(Backend.ConvertType(concreteTypeRef), 0);
                    default:
                        throw new ArgumentOutOfRangeException();
                }
            case Struct @struct:
            {
                var baseType = Backend.ConvertType(concreteTypeRef);
                var structContext =
                    new GenericContext(null, @struct.GenericParams, concreteTypeRef.GenericParams, null);

                var consts = new List<LLVMValueRef>();
                foreach (var fieldDef in @struct.Fields)
                {
                    consts.Add(ZeroInit(structContext.ResolveRef(fieldDef.Type)));
                }

                return LLVMValueRef.CreateConstNamedStruct(baseType, consts.ToArray());
            }
            case Interface @interface:
                throw new NotImplementedException($"Zero init not implemented for {val}");
            case Variant variant:
            {
                var baseType = Backend.ConvertType(concreteTypeRef);
                var bodySize = Backend.GetVariantBodySize(variant, concreteTypeRef.GenericParams);

                var emptyBody = new LLVMValueRef[bodySize];
                for (var i = 0; i < bodySize; i++)
                {
                    emptyBody[i] = LLVMValueRef.CreateConstInt(LLVMTypeRef.Int8, 0);
                }

                return LLVMValueRef.CreateConstNamedStruct(baseType, new[]
                {
                    LLVMValueRef.CreateConstInt(LLVMTypeRef.Int8, 0),
                    LLVMValueRef.CreateConstArray(
                        LLVMTypeRef.Int8,
                        emptyBody
                    )
                });
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(val));
        }
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

            Builder.BuildCondBr(
                cond,
                GetJumpTrampoline(CurrentBlock.Id, inst.TargetBlock),
                GetJumpTrampoline(CurrentBlock.Id, inst.ElseBlock)
            );
        }
        else
        {
            Builder.BuildBr(GetJumpTrampoline(CurrentBlock.Id, inst.TargetBlock));
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

    private LLVMBasicBlockRef GetJumpTrampoline(int from, int to)
    {
        var key = (from, to);
        if (_jumpTrampolines.TryGetValue(key, out var blockRef))
        {
            return blockRef;
        }

        var fromBlock = _func.Blocks.Single(x => x.Id == from);
        var targetBlock = _func.Blocks.Single(x => x.Id == to);
        var targetBlockRef = _blockMap[to];

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
            _jumpTrampolines.Add(key, targetBlockRef);
            return targetBlockRef;
        }

        var original = Builder.InsertBlock;

        blockRef = _funcRef.AppendBasicBlock($"jump_f{from}_t{to}");
        _jumpTrampolines.Add(key, blockRef);
        Builder.PositionAtEnd(blockRef);

        var currentScope = fromBlock.Scope;

        while (currentScope.ParentScope != fromScopes[matchesUntil])
        {
            if (currentScope.ParentScope == null)
            {
                throw new Exception("Failed to find common parent");
            }

            var tgtSlot = _scopeJumpTargets[currentScope.Id];
            var parentBlock = _scopeJumpBlocks[currentScope.ParentScope.Id];
            var parentBlockAddress = _funcRef.GetBlockAddress(parentBlock);
            Builder.BuildStore(parentBlockAddress, tgtSlot);
            currentScope = currentScope.ParentScope;
        }

        {
            var tgtSlot = _scopeJumpTargets[currentScope.Id];
            var tgtBlockAddress = _funcRef.GetBlockAddress(targetBlockRef);
            Builder.BuildStore(tgtBlockAddress, tgtSlot);
        }

        Builder.BuildBr(_scopeJumpBlocks[fromBlock.Scope.Id]);
        Builder.PositionAtEnd(original);

        return blockRef;
    }

    private void CompileJumpVariantInst(JumpVariantInst inst)
    {
        var variantItemTypeRef = (ConcreteTypeRef)FunctionContext.ResolveRef(inst.VariantItemType);
        var (varType, rawVarRef) = GetSlotRef(inst.VariantSlot);

        LLVMValueRef varRef;
        switch (varType)
        {
            case BaseTypeRef:
                varRef = rawVarRef;
                break;
            case PointerTypeRef:
            case BorrowTypeRef:
                varRef = Builder.BuildLoad(rawVarRef, $"inst_{inst.Id}_laddr");
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

        var typeAddr = Builder.BuildInBoundsGEP(
            varRef,
            new[]
            {
                LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0),
                LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0)
            },
            $"inst_{inst.Id}_taddr"
        );
        var typeVal = Builder.BuildLoad(typeAddr, $"inst_{inst.Id}_type");
        var condVal = Builder.BuildICmp(
            LLVMIntPredicate.LLVMIntEQ,
            typeVal,
            LLVMValueRef.CreateConstInt(LLVMTypeRef.Int8, (ulong)index),
            $"inst_{inst.Id}_cond"
        );

        var trueBlock = _funcRef.AppendBasicBlock(
            $"scope_{CurrentBlock.Scope.Id}_block_{CurrentBlock.Id}_inst_{inst.Id}"
        );
        Builder.BuildCondBr(condVal, trueBlock, GetJumpTrampoline(CurrentBlock.Id, inst.ElseBlock));
        Builder.PositionAtEnd(trueBlock);

        switch (varType)
        {
            case BaseTypeRef:
                varRef = rawVarRef;
                break;
            case PointerTypeRef:
            case BorrowTypeRef:
                varRef = Builder.BuildLoad(rawVarRef, $"inst_{inst.Id}_laddr");
                break;
            case ReferenceTypeRef referenceTypeRef:
                throw new NotImplementedException();
            default:
                throw new ArgumentOutOfRangeException();
        }

        var itemAddr = Builder.BuildInBoundsGEP(
            varRef,
            new[]
            {
                LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0),
                LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 1)
            },
            $"inst_{inst.Id}_iaddr"
        );
        var variantItemType = Backend.ConvertType(variantItemTypeRef);
        var castedAddr = Builder.BuildBitCast(
            itemAddr,
            LLVMTypeRef.CreatePointer(variantItemType, 0),
            $"inst_{inst.Id}_iaddr_cast"
        );

        switch (varType)
        {
            case BaseTypeRef:
            {
                var properties = Store.GetCopyProperties(variantItemTypeRef);

                LLVMValueRef destValue;
                if (properties.CanCopy)
                {
                    destValue = GenerateCopy(variantItemTypeRef, properties, castedAddr, $"inst_{inst.Id}_copy");
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
                    castedAddr,
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

        Builder.BuildBr(GetJumpTrampoline(CurrentBlock.Id, inst.TargetBlock));
        Builder.PositionAtEnd(_blockMap[CurrentBlock.Id]);
    }

    private void CompileStaticCallInst(StaticCallInst inst)
    {
        Function funcDef;
        GenericContext funcContext;
        FunctionRef key;
        ConcreteTypeRef targetMethod;

        if (inst.TargetType != null)
        {
            var targetType = (ConcreteTypeRef)FunctionContext.ResolveRef(inst.TargetType);

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
            targetMethod = (ConcreteTypeRef)FunctionContext.ResolveRef(inst.TargetMethod);
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
        var funcRef = Backend.GetFunctionRef(key);

        if (funcDef.Parameters.Count != inst.Arguments.Count)
        {
            throw new Exception("Invalid number of arguments");
        }

        var args = new List<LLVMValueRef>();

        for (var i = 0; i < funcDef.Parameters.Count; i++)
        {
            var param = funcDef.Parameters[i];
            var paramType = funcContext.ResolveRef(param.Type);

            var (argType, argPtr) = GetSlotRef(inst.Arguments[i]);
            var properties = Store.GetCopyProperties(argType);
            var slotLifetime = GetLifetime(inst).GetSlot(inst.Arguments[i]);

            LLVMValueRef argVal;
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
            var value = Builder.BuildCall(funcRef, args.ToArray(), $"{name}_ret");
            StoreSlot(inst.ResultSlot.Value, value, returnType);
            MarkActive(inst.ResultSlot.Value);
        }
        else
        {
            if (funcDef.ReturnType != null)
            {
                throw new Exception("Function returns a value which is unused");
            }

            Builder.BuildCall(funcRef, args.ToArray());
        }
    }

    private void CompileReturnInst(ReturnInst inst)
    {
        if (inst.ReturnSlot.HasValue != _returnSlot.HasValue)
        {
            throw new Exception("Invalid return expression");
        }

        if (inst.ReturnSlot.HasValue)
        {
            var (retType, retValue) = LoadSlot(inst.ReturnSlot.Value, $"inst_{inst.Id}_load");
            MarkMoved(inst.ReturnSlot.Value);
            if (!Equals(retType, FunctionContext.ResolveRef(_func.ReturnType)))
            {
                throw new Exception("Invalid return type");
            }

            StoreSlot(_returnSlot.Value, retValue, retType, true);
        }

        var currentScope = CurrentBlock.Scope;

        while (currentScope.ParentScope != null)
        {
            var tgtSlot = _scopeJumpTargets[currentScope.Id];
            var parentBlock = _scopeJumpBlocks[currentScope.ParentScope.Id];
            var parentBlockAddress = _funcRef.GetBlockAddress(parentBlock);
            Builder.BuildStore(parentBlockAddress, tgtSlot);
            currentScope = currentScope.ParentScope;
        }

        {
            var tgtSlot = _scopeJumpTargets[currentScope.Id];
            var returnBlockAddress = _funcRef.GetBlockAddress(_returnBlock);
            Builder.BuildStore(returnBlockAddress, tgtSlot);
        }

        Builder.BuildBr(_scopeJumpBlocks[CurrentBlock.Scope.Id]);
    }

    private void CompileSlotBorrowInst(SlotBorrowInst inst)
    {
        var (slotType, slotRef) = GetSlotRef(inst.BaseSlot);
        StoreSlot(inst.TargetSlot, slotRef, new BorrowTypeRef(slotType, inst.Mutable));
        MarkActive(inst.TargetSlot);
    }

    private void CompileFieldMoveInst(FieldMoveInst inst)
    {
        var slotDec = GetSlot(inst.BaseSlot);

        LLVMValueRef baseAddr;
        ConcreteTypeRef structType;
        bool isDirect;
        switch (slotDec.Type)
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
                throw new ArgumentOutOfRangeException(nameof(slotDec.Type));
        }

        var structDef = Store.Lookup<Struct>(structType.Name);
        var index = structDef.Fields.FindIndex(x => x.Name == inst.TargetField);
        var fieldDef = structDef.Fields[index];
        var structContext = new GenericContext(null, structDef.GenericParams, structType.GenericParams, null);
        var fieldType = structContext.ResolveRef(fieldDef.Type);

        var addr = Builder.BuildInBoundsGEP(
            baseAddr,
            new[]
            {
                LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0),
                LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, (ulong)index)
            },
            $"inst_{inst.Id}_faddr"
        );

        var properties = Store.GetCopyProperties(fieldType);

        LLVMValueRef destValue;
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

        var addr = Builder.BuildInBoundsGEP(
            slotVal,
            new[]
            {
                LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0),
                LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, (ulong)index)
            },
            $"inst_{inst.Id}_addr"
        );
        StoreSlot(inst.TargetSlot, addr, targetType);
        MarkActive(inst.TargetSlot);
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

        LLVMValueRef val;
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
            var value = Builder.BuildLoad(tgt, $"inst_{inst.Id}_existing_value");
            PerformDrop(value, valType);
        }

        Builder.BuildStore(val, tgt);
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

        LLVMValueRef destValue;
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

    private void CompileCastInst(CastInst inst)
    {
        var (type, value) = LoadSlot(inst.SourceSlot, $"inst_{inst.Id}_load");
        var slotLifetime = GetLifetime(inst).GetSlot(inst.SourceSlot);
        var targetType = FunctionContext.ResolveRef(inst.TargetType);
        var targetLlvmType = Backend.ConvertType(targetType);

        var (castable, unsafeCast) = Store.CanCastTypes(type, targetType);
        if (!castable)
        {
            throw new Exception($"Cannot cast from {type} to {targetType}");
        }

        if (!CurrentBlock.Scope.Unsafe && unsafeCast)
        {
            throw new Exception($"Cast from {type} to {targetType} is unsafe");
        }

        LLVMValueRef converted;
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
                    var fromWidth = PrimitiveType.GetWidth(fromKind);
                    var toKind = PrimitiveType.GetKind(targetType);
                    var toWidth = PrimitiveType.GetWidth(toKind);

                    if (toWidth == fromWidth)
                    {
                        converted = value;
                    }
                    else if (toWidth < fromWidth)
                    {
                        converted = Builder.BuildTrunc(value, targetLlvmType, $"inst_{inst.Id}_trunc");
                    }
                    else if (PrimitiveType.IsSigned(fromKind))
                    {
                        converted = Builder.BuildSExt(value, targetLlvmType, $"inst_{inst.Id}_sext");
                    }
                    else
                    {
                        converted = Builder.BuildZExt(value, targetLlvmType, $"inst_{inst.Id}_zext");
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

                        var incFunc = Backend.GetFunctionRef(
                            new FunctionRef
                            {
                                TargetMethod = ConcreteTypeRef.From(
                                    QualifiedName.From("std", "box_inc_weak"),
                                    fromRef.InnerType
                                )
                            }
                        );
                        Builder.BuildCall(incFunc, new[] { value });

                        converted = value;
                        ignoreMoved = true;
                        break;
                    }
                    case BorrowTypeRef:
                    case PointerTypeRef:
                        converted = GetBoxValuePtr(value, $"inst_{inst.Id}_ptr");
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(targetType));
                }

                break;
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(type));
        }

        converted = Builder.BuildBitCast(converted, targetLlvmType, $"inst_{inst.Id}_cast");

        if (slotLifetime.Status == SlotStatus.Moved && !ignoreMoved)
        {
            if (dropIfMoved)
            {
                PerformDrop(value, type);
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

        LLVMValueRef fromValue;
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

        LLVMValueRef boxPtr;
        TypeRef ptrType;
        LLVMValueRef ptrValue;

        if (type is DerivedRefTypeRef derivedRefTypeRef)
        {
            var structType = ConcreteTypeRef.From(
                QualifiedName.From("std", "DerivedBox"),
                derivedRefTypeRef.InnerType
            );
            var structDef = Store.Lookup<Struct>(structType.Name);
            var structContext = new GenericContext(null, structDef.GenericParams, structType.GenericParams, null);

            var boxIndex = structDef.Fields.FindIndex(x => x.Name == "box_ptr");
            boxPtr = Builder.BuildExtractValue(fromValue, (uint)boxIndex, $"inst_{inst.Id}_box_ptr");

            var valueIndex = structDef.Fields.FindIndex(x => x.Name == "value_ptr");
            ptrType = derivedRefTypeRef.InnerType;
            ptrValue = Builder.BuildExtractValue(fromValue, (uint)valueIndex, $"inst_{inst.Id}_value");
        }
        else if (type is ReferenceTypeRef referenceTypeRef)
        {
            if (!referenceTypeRef.StrongRef)
            {
                throw new Exception("Cannot derive weak reference");
            }

            boxPtr = fromValue;
            ptrValue = GetBoxValuePtr(boxPtr, $"inst_{inst.Id}_box");
            ptrType = referenceTypeRef.InnerType;
        }
        else
        {
            throw new Exception("Source is not a reference");
        }

        if (inst.FieldName != null)
        {
            var structType = (ConcreteTypeRef)ptrType;
            var structDef = Store.Lookup<Struct>(structType.Name);
            var index = structDef.Fields.FindIndex(x => x.Name == inst.FieldName);
            var fieldDef = structDef.Fields[index];
            var structContext = new GenericContext(null, structDef.GenericParams, structType.GenericParams, null);
            ptrType = structContext.ResolveRef(fieldDef.Type);

            ptrValue = Builder.BuildInBoundsGEP(
                ptrValue,
                new[]
                {
                    LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0),
                    LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, (ulong)index)
                },
                $"inst_{inst.Id}_faddr"
            );
        }

        var targetMethod = ConcreteTypeRef.From(
            QualifiedName.From("std", "derived_create"),
            ptrType
        );
        var funcRef = Backend.GetFunctionRef(new FunctionRef
        {
            TargetMethod = targetMethod
        });

        var boxLlvmType = Backend.ConvertType(
            new PointerTypeRef(
                ConcreteTypeRef.From(
                    QualifiedName.From("std", "Box"),
                    ConcreteTypeRef.From(
                        QualifiedName.From("std", "Void")
                    )
                ),
                true
            )
        );
        boxPtr = Builder.BuildBitCast(boxPtr, boxLlvmType, $"inst_{inst.Id}_cast_box");

        var destValue = Builder.BuildCall(funcRef, new[] { boxPtr, ptrValue }, $"inst_{inst.Id}_derived");
        var returnType = new DerivedRefTypeRef(ptrType, true);

        StoreSlot(inst.ResultSlot, destValue, returnType);
        MarkActive(inst.ResultSlot);
    }

    private void CompileRefBorrow(RefBorrowInst inst)
    {
        var (type, value) = LoadSlot(inst.SourceSlot, $"inst_{inst.Id}_load");

        TypeRef innerType;
        LLVMValueRef valueRef;
        if (type is ReferenceTypeRef referenceTypeRef)
        {
            if (!referenceTypeRef.StrongRef)
            {
                throw new Exception("Cannot borrow weak reference");
            }

            innerType = referenceTypeRef.InnerType;
            valueRef = GetBoxValuePtr(value, $"inst_{inst.Id}_ptr");
        }
        else if (type is DerivedRefTypeRef derivedRefTypeRef)
        {
            if (!derivedRefTypeRef.StrongRef)
            {
                throw new Exception("Cannot borrow weak reference");
            }

            innerType = derivedRefTypeRef.InnerType;
            valueRef = GetDerivedBoxValuePtr(value, $"inst_{inst.Id}_ptr");
        }
        else
        {
            throw new Exception("Source is not a reference");
        }

        var targetType = new BorrowTypeRef(innerType, false);
        var targetLlvmType = Backend.ConvertType(targetType);
        valueRef = Builder.BuildBitCast(valueRef, targetLlvmType, $"inst_{inst.Id}_cast");

        StoreSlot(inst.ResultSlot, valueRef, targetType);
        MarkActive(inst.ResultSlot);
    }

    public LLVMValueRef GetBoxValuePtr(LLVMValueRef valueRef, string name)
    {
        var structDef = Store.Lookup<Struct>(QualifiedName.From("std", "Box"));
        var index = structDef.Fields.FindIndex(x => x.Name == "value");

        return Builder.BuildInBoundsGEP(
            valueRef,
            new[]
            {
                LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0),
                LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, (ulong)index)
            },
            name
        );
    }

    public LLVMValueRef GetDerivedBoxValuePtr(LLVMValueRef valueRef, string name)
    {
        var structDef = Store.Lookup<Struct>(QualifiedName.From("std", "DerivedBox"));
        var index = structDef.Fields.FindIndex(x => x.Name == "value_ptr");

        return Builder.BuildExtractValue(
            valueRef,
            (uint)index,
            name
        );
    }

    private void CompileAllocVariantInst(AllocVariantInst inst)
    {
        var variant = Store.Lookup<Variant>(inst.VariantType.Name);
        var variantTypeRef = (ConcreteTypeRef)FunctionContext.ResolveRef(inst.VariantType);

        var variantValue = ZeroInit(variantTypeRef);
        StoreSlot(inst.SlotId, variantValue, variantTypeRef);
        MarkActive(inst.SlotId);

        var (_, baseAddr) = GetSlotRef(inst.SlotId);

        // Create type value
        var typeAddr = Builder.BuildInBoundsGEP(
            baseAddr,
            new[]
            {
                LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0),
                LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0)
            },
            $"inst_{inst.Id}_taddr"
        );
        var index = variant.Items.FindIndex(x => x.Name == inst.ItemName);
        Builder.BuildStore(LLVMValueRef.CreateConstInt(LLVMTypeRef.Int8, (ulong)index), typeAddr);

        if (inst.ItemSlot is not { } slotId) return;

        var variantItemRef = new ConcreteTypeRef(
            new QualifiedName(true, variantTypeRef.Name.Parts.Add(inst.ItemName)),
            variantTypeRef.GenericParams
        );
        var variantItemType = Backend.ConvertType(variantItemRef);

        // Store variant value
        var valueAddr = Builder.BuildInBoundsGEP(
            baseAddr,
            new[]
            {
                LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0),
                LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 1)
            },
            $"inst_{inst.Id}_vaddr"
        );
        var castedAddr = Builder.BuildBitCast(
            valueAddr,
            LLVMTypeRef.CreatePointer(variantItemType, 0),
            $"inst_{inst.Id}_vaddr_cast"
        );

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

        Builder.BuildStore(value, castedAddr);
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