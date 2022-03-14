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

/// <summary>
/// Generate llvm ir for a oxide function body
/// </summary>
public partial class FunctionGenerator
{
    public LlvmBackend Backend { get; }

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
        // Sanity check
        if (!CurrentBlock.Scope.CanAccessSlot(slot))
        {
            throw new Exception($"Slot is not accessible from {CurrentBlock.Id}");
        }

        return _slotDefs[slot];
    }

    public void StoreSlot(int slot, LLVMValueRef value, TypeRef type, bool ignoreChecks = false)
    {
        // Sanity checks
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

    /// <summary>
    /// Mark slot as active by storing true to liveness stack alloc
    /// </summary>
    public void MarkActive(int slot)
    {
        if (_slotLivenessMap.TryGetValue(slot, out var valRef))
        {
            Builder.BuildStore(LLVMValueRef.CreateConstInt(LLVMTypeRef.Int1, 1), valRef);
        }
    }

    /// <summary>
    /// Mark slot as not active by storing false to liveness stack alloc
    /// </summary>
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

        Builder.BuildCall(dropFunc, new[] {value});
    }

    /// <summary>
    /// Check liveness value before calling drop function for slot
    /// </summary>
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

    /// <summary>
    /// Copy value from one location to another
    /// </summary>
    private LLVMValueRef GenerateCopy(TypeRef type, CopyProperties properties, LLVMValueRef pointer, string name)
    {
        if (!properties.CanCopy)
        {
            throw new ArgumentException("Invalid copy properties");
        }

        // Use fast bitwise copy if possible
        if (properties.BitwiseCopy)
        {
            return Builder.BuildLoad(pointer, $"{name}_copy");
        }

        // Otherwise use "Copyable" implementation

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

        return Builder.BuildCall(funcRef, new[] {pointer}, $"{name}_copyfunc");
    }

    private (LLVMValueRef value, TypeRef ty) ConvertConstant(PrimitiveKind kind, object sourceValue)
    {
        LLVMValueRef value;
        TypeRef valType;
        switch (kind)
        {
            case PrimitiveKind.I32:
                value = LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, (ulong) (int) sourceValue, true);
                valType = PrimitiveKind.I32.GetRef();
                break;
            case PrimitiveKind.Bool:
                value = LLVMValueRef.CreateConstInt(LLVMTypeRef.Int1, (ulong) ((bool) sourceValue ? 1 : 0), true);
                valType = PrimitiveKind.Bool.GetRef();
                break;
            default:
                throw new ArgumentOutOfRangeException();
        }

        return (value, valType);
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