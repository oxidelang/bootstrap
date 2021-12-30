using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using LLVMSharp.Interop;
using Oxide.Compiler.IR;
using Oxide.Compiler.IR.Instructions;
using Oxide.Compiler.IR.TypeRefs;
using Oxide.Compiler.IR.Types;
using Oxide.Compiler.Middleware;
using Oxide.Compiler.Middleware.Usage;

namespace Oxide.Compiler.Backend.Llvm
{
    public class FunctionGenerator
    {
        private LlvmBackend Backend { get; }

        private LLVMModuleRef Module => Backend.Module;

        private IrStore Store => Backend.Store;

        private LLVMBuilderRef Builder { get; set; }

        private Function _func;

        private LLVMValueRef _funcRef;

        private Dictionary<int, LLVMValueRef> _slotMap;
        private Dictionary<int, SlotDeclaration> _slotDefs;
        private Dictionary<int, LLVMBasicBlockRef> _blockMap;
        private Dictionary<int, LLVMBasicBlockRef> _scopeReturnMap;
        private int? _returnSlot;

        private Block CurrentBlock { get; set; }

        private GenericContext FunctionContext { get; set; }

        public FunctionGenerator(LlvmBackend backend)
        {
            Backend = backend;
        }

        public void Compile(FunctionRef key, Function func, GenericContext context)
        {
            _func = func;
            FunctionContext = context;

            if (!func.GenericParams.IsEmpty)
            {
                throw new NotImplementedException("Generic function generation is not implemented");
            }

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
                }
            }

            // Jump to first block
            Builder.BuildBr(_blockMap[func.EntryBlock]);

            // Create "return" paths for scopes
            _scopeReturnMap = new Dictionary<int, LLVMBasicBlockRef>();
            foreach (var scope in func.Scopes)
            {
                if (scope.ParentScope == null)
                {
                    CreateScopeReturn(scope);
                }
            }

            // Compile bodies
            foreach (var block in func.Blocks)
            {
                CompileBlock(block);
            }

            Builder.Dispose();
        }

        private void CreateScopeReturn(Scope scope)
        {
            var block = _funcRef.AppendBasicBlock($"scope_{scope.Id}_return");
            Builder.PositionAtEnd(block);

            // TODO: Cleanup any values that need cleanup

            if (scope.ParentScope == null)
            {
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
            }
            else
            {
                Builder.BuildBr(_scopeReturnMap[scope.ParentScope.Id]);
            }

            _scopeReturnMap.Add(scope.Id, block);

            // Generate children's return path now that parents exists
            foreach (var childScope in _func.Scopes)
            {
                if (childScope.ParentScope != scope)
                {
                    continue;
                }

                CreateScopeReturn(childScope);
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
                case AllocVariantInst allocVariantInst:
                    CompileAllocVariantInst(allocVariantInst);
                    break;
                case JumpVariantInst jumpVariantInst:
                    CompileJumpVariantInst(jumpVariantInst);
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

        private void StoreSlot(int slot, LLVMValueRef value, TypeRef type, bool ignoreChecks = false)
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

        private (TypeRef type, LLVMValueRef value) LoadSlot(int slot, string name, bool ignoreChecks = false)
        {
            if (!ignoreChecks && !CurrentBlock.Scope.CanAccessSlot(slot))
            {
                throw new Exception($"Slot is not accessible from {CurrentBlock.Id}");
            }

            return (_slotDefs[slot].Type, Builder.BuildLoad(_slotMap[slot], name));
        }

        private (TypeRef type, LLVMValueRef value) GetSlotRef(int slot, bool ignoreChecks = false)
        {
            if (!ignoreChecks && !CurrentBlock.Scope.CanAccessSlot(slot))
            {
                throw new Exception($"Slot is not accessible from {CurrentBlock.Id}");
            }

            return (_slotDefs[slot].Type, _slotMap[slot]);
        }

        private void CompileMoveInst(MoveInst inst)
        {
            var (type, value) = LoadSlot(inst.SrcSlot, $"inst_{inst.Id}_load");
            // if (!IsCopyType(type))
            // {
            //     TODO: Check validity
            // }

            StoreSlot(inst.DestSlot, value, type);
        }

        private void CompileConstInst(ConstInst inst)
        {
            LLVMValueRef value;
            TypeRef valType;
            switch (inst.ConstType)
            {
                case PrimitiveKind.I32:
                    value = LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, (ulong)(int)inst.Value, true);
                    valType = PrimitiveType.I32Ref;
                    break;
                case PrimitiveKind.Bool:
                    value = LLVMValueRef.CreateConstInt(LLVMTypeRef.Int1, (ulong)((bool)inst.Value ? 1 : 0), true);
                    valType = PrimitiveType.BoolRef;
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }

            StoreSlot(inst.TargetSlot, value, valType);
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

            switch (inst.Op)
            {
                case ArithmeticInst.Operation.Add:
                    value = Builder.BuildAdd(left, right, name);
                    break;
                case ArithmeticInst.Operation.Minus:
                    value = Builder.BuildSub(left, right, name);
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }

            StoreSlot(inst.ResultSlot, value, leftType);
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

            StoreSlot(inst.ResultSlot, value, PrimitiveType.BoolRef);
        }

        private void CompileAllocStructInst(AllocStructInst inst)
        {
            var structType = FunctionContext.ResolveRef(inst.StructType);
            StoreSlot(inst.SlotId, ZeroInit(structType), structType);
        }

        private LLVMValueRef ZeroInit(TypeRef tref)
        {
            if (tref is not ConcreteTypeRef concreteTypeRef)
            {
                throw new NotImplementedException("Zero init of non direct type not implemented");
            }

            var val = Store.Lookup(concreteTypeRef.Name);
            switch (val)
            {
                case null:
                    throw new Exception($"Unable to resolve {tref}");
                case PrimitiveType primitiveType:
                    switch (primitiveType.Kind)
                    {
                        case PrimitiveKind.I32:
                            return LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0);
                        case PrimitiveKind.Bool:
                            return LLVMValueRef.CreateConstInt(LLVMTypeRef.Int1, 0);
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
                if (!Equals(condType, PrimitiveType.BoolRef))
                {
                    throw new Exception("Invalid condition type");
                }

                Builder.BuildCondBr(cond, _blockMap[inst.TargetBlock], _blockMap[inst.ElseBlock]);
            }
            else
            {
                Builder.BuildBr(_blockMap[inst.TargetBlock]);
            }

            // TODO: Real cleanup
            // throw new NotImplementedException("Jump inst");
        }

        private void CompileJumpVariantInst(JumpVariantInst inst)
        {
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
                    inst.VariantItemType.Name.Parts.RemoveAt(inst.VariantItemType.Name.Parts.Length - 1)
                ),
                inst.VariantItemType.GenericParams
            );

            if (!Equals(varType.GetBaseType(), expectedVarType))
            {
                throw new Exception($"Variant type mismatch {varType} {expectedVarType}");
            }

            var variant = Store.Lookup<Variant>(expectedVarType.Name);
            var itemName = inst.VariantItemType.Name.Parts.Last();
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
            Builder.BuildCondBr(condVal, trueBlock, _blockMap[inst.ElseBlock]);
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
            var variantItemType = Backend.ConvertType(inst.VariantItemType);
            var castedAddr = Builder.BuildBitCast(
                itemAddr,
                LLVMTypeRef.CreatePointer(variantItemType, 0),
                $"inst_{inst.Id}_iaddr_cast"
            );

            switch (varType)
            {
                case BaseTypeRef:
                    if (IsCopyType(inst.VariantItemType))
                    {
                        var value = Builder.BuildLoad(castedAddr, $"inst_{inst.Id}_copy");
                        StoreSlot(inst.ItemSlot, value, inst.VariantItemType, true);
                    }
                    else
                    {
                        throw new NotImplementedException("Field moves");
                    }

                    break;
                case BorrowTypeRef borrowTypeRef:
                    StoreSlot(
                        inst.ItemSlot,
                        castedAddr,
                        new BorrowTypeRef(
                            inst.VariantItemType,
                            borrowTypeRef.MutableRef
                        ),
                        true
                    );
                    break;
                case PointerTypeRef pointerTypeRef:
                    throw new NotImplementedException();
                case ReferenceTypeRef referenceTypeRef:
                    throw new NotImplementedException();
                default:
                    throw new ArgumentOutOfRangeException();
            }

            Builder.BuildBr(_blockMap[inst.TargetBlock]);
            Builder.PositionAtEnd(_blockMap[CurrentBlock.Id]);
        }

        private void CompileStaticCallInst(StaticCallInst inst)
        {
            Function funcDef;
            GenericContext funcContext;
            FunctionRef key;

            if (inst.TargetType != null)
            {
                var targetType = (ConcreteTypeRef)FunctionContext.ResolveRef(inst.TargetType);
                (_, funcDef) = Store.LookupImplementation(
                    targetType,
                    inst.TargetImplementation,
                    inst.TargetMethod.Name.Parts.Single()
                );

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
                    TargetMethod = inst.TargetMethod
                };
            }
            else
            {
                funcDef = Store.Lookup<Function>(inst.TargetMethod.Name);
                funcContext = GenericContext.Default;

                key = new FunctionRef
                {
                    TargetMethod = inst.TargetMethod
                };
            }

            if (funcDef == null)
            {
                throw new Exception($"Failed to find unit for {inst.TargetMethod}");
            }

            if (inst.TargetMethod.GenericParams.Length > 0)
            {
                funcContext = new GenericContext(funcContext, funcDef.GenericParams, inst.TargetMethod.GenericParams,
                    funcContext.ThisRef);
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
                var (argType, argVal) = LoadSlot(inst.Arguments[i], $"{name}_param_{param.Name}");

                bool matches;
                switch (argType)
                {
                    case BaseTypeRef:
                    case ReferenceTypeRef:
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
                if (!Equals(retType, FunctionContext.ResolveRef(_func.ReturnType)))
                {
                    throw new Exception("Invalid return type");
                }

                StoreSlot(_returnSlot.Value, retValue, retType, true);
            }

            Builder.BuildBr(_scopeReturnMap[CurrentBlock.Scope.Id]);
        }

        private void CompileSlotBorrowInst(SlotBorrowInst inst)
        {
            var slotRef = _slotMap[inst.BaseSlot];
            var slotDef = GetSlot(inst.BaseSlot);
            StoreSlot(inst.TargetSlot, slotRef, new BorrowTypeRef(slotDef.Type, inst.Mutable));
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
                    (_, baseAddr) = LoadSlot(inst.BaseSlot, $"inst_{inst.Id}_base");
                    isDirect = false;

                    if (borrowTypeRef.InnerType is not ConcreteTypeRef concreteTypeRef)
                    {
                        throw new Exception("Cannot borrow field from non borrowed direct type");
                    }

                    structType = concreteTypeRef;
                    break;
                case BaseTypeRef:
                    (_, baseAddr) = GetSlotRef(inst.BaseSlot);
                    isDirect = true;
                    throw new NotImplementedException("direct field moves");
                case PointerTypeRef:
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

            if (!Equals(fieldType, PrimitiveType.I32Ref))
            {
                throw new NotImplementedException("Field moves");
                // TODO: moves
            }

            if (IsCopyType(fieldType))
            {
                var value = Builder.BuildLoad(addr, $"inst_{inst.Id}_copy");
                StoreSlot(inst.TargetSlot, value, fieldType);
            }
            else if (!isDirect)
            {
                throw new Exception("Cannot move non-copyable field from a reference");
            }
            else
            {
                throw new NotImplementedException("Field moves");
            }
        }

        private void CompileFieldBorrowInst(FieldBorrowInst inst)
        {
            var (slotType, slotVal) = LoadSlot(inst.BaseSlot, $"inst_{inst.Id}_base");

            ConcreteTypeRef structType;
            switch (slotType)
            {
                case BorrowTypeRef borrowTypeRef:
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
                case BaseTypeRef:
                    throw new Exception("Cannot borrow field from base type");
                case PointerTypeRef:
                case ReferenceTypeRef:
                    throw new NotImplementedException();
                default:
                    throw new ArgumentOutOfRangeException(nameof(slotType));
            }

            var structDef = Store.Lookup<Struct>(structType.Name);
            var index = structDef.Fields.FindIndex(x => x.Name == inst.TargetField);
            var fieldDef = structDef.Fields[index];
            var structContext = new GenericContext(null, structDef.GenericParams, structType.GenericParams, null);

            var addr = Builder.BuildInBoundsGEP(
                slotVal,
                new[]
                {
                    LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0),
                    LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, (ulong)index)
                },
                $"inst_{inst.Id}_addr"
            );
            StoreSlot(inst.TargetSlot, addr, new BorrowTypeRef(structContext.ResolveRef(fieldDef.Type), inst.Mutable));
        }

        private void CompileStoreIndirectInst(StoreIndirectInst inst)
        {
            var (tgtType, tgt) = LoadSlot(inst.TargetSlot, $"inst_{inst.Id}_tgt");
            var (valType, val) = LoadSlot(inst.ValueSlot, $"inst_{inst.Id}_value");

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

                    break;
                case PointerTypeRef:
                case ReferenceTypeRef:
                    throw new NotImplementedException();
                default:
                    throw new ArgumentOutOfRangeException(nameof(tgtType));
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
                case PointerTypeRef:
                case ReferenceTypeRef:
                    throw new NotImplementedException();
                default:
                    throw new ArgumentOutOfRangeException(nameof(addr));
            }

            if (!IsCopyType(innerTypeRef))
            {
                throw new Exception("Cannot deref non-copyable type");
            }

            var value = Builder.BuildLoad(addr, $"inst_{inst.Id}_load");
            StoreSlot(inst.TargetSlot, value, innerTypeRef);
        }

        private void CompileCastInst(CastInst inst)
        {
            var (type, value) = LoadSlot(inst.SourceSlot, $"inst_{inst.Id}_load");
            var targetType = FunctionContext.ResolveRef(inst.TargetType);

            LLVMValueRef converted;


            switch (type)
            {
                case BaseTypeRef baseTypeRef:
                    if (Equals(baseTypeRef, targetType))
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
                    if (targetType is not BorrowTypeRef)
                    {
                        throw new Exception("Incompatible conversion");
                    }

                    if (!CurrentBlock.Scope.Unsafe)
                    {
                        throw new Exception("Conversion is unsafe");
                    }

                    converted = value;
                    break;
                }
                case PointerTypeRef:
                {
                    if (targetType is not PointerTypeRef)
                    {
                        throw new Exception("Incompatible conversion");
                    }

                    if (!CurrentBlock.Scope.Unsafe)
                    {
                        throw new Exception("Conversion is unsafe");
                    }

                    converted = value;
                    break;
                }
                case ReferenceTypeRef referenceTypeRef:
                    throw new NotImplementedException();
                default:
                    throw new ArgumentOutOfRangeException(nameof(type));
            }

            StoreSlot(inst.ResultSlot, converted, targetType);
        }

        private void CompileAllocVariantInst(AllocVariantInst inst)
        {
            var variant = Store.Lookup<Variant>(inst.VariantType.Name);
            var variantTypeRef = FunctionContext.ResolveRef(inst.VariantType);
            var variantItemRef = new ConcreteTypeRef(
                new QualifiedName(true, inst.VariantType.Name.Parts.Add(inst.ItemName)),
                inst.VariantType.GenericParams
            );
            var variantItemType = Backend.ConvertType(variantItemRef);

            var variantValue = ZeroInit(variantTypeRef);
            StoreSlot(inst.SlotId, variantValue, variantTypeRef);

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

            Builder.BuildStore(value, castedAddr);
        }

        private bool IsIntegerBacked(TypeRef typeRef)
        {
            if (typeRef is not ConcreteTypeRef concreteTypeRef)
            {
                throw new NotImplementedException("Non direct types");
            }

            return Equals(concreteTypeRef.Name, PrimitiveType.I32.Name) ||
                   Equals(concreteTypeRef.Name, PrimitiveType.Bool.Name);
        }

        private bool IsSignedInteger(TypeRef typeRef)
        {
            if (typeRef is not ConcreteTypeRef concreteTypeRef)
            {
                throw new NotImplementedException("Non direct types");
            }

            return Equals(concreteTypeRef.Name, PrimitiveType.I32.Name) ||
                   Equals(concreteTypeRef.Name, PrimitiveType.Bool.Name);
        }

        private bool IsCopyType(TypeRef type)
        {
            switch (type)
            {
                case BorrowTypeRef:
                case PointerTypeRef:
                    return true;
                case ReferenceTypeRef:
                    return false;
                case ConcreteTypeRef concreteTypeRef:
                {
                    var baseType = Store.Lookup(concreteTypeRef.Name);
                    switch (baseType)
                    {
                        case PrimitiveType primitiveType:
                            return true;
                        case Struct @struct:
                            // TODO
                            return false;
                        case Interface @interface:
                        case Variant variant:
                            throw new NotImplementedException();
                        default:
                            throw new ArgumentOutOfRangeException(nameof(baseType));
                    }
                }
                case BaseTypeRef baseTypeRef:
                    throw new Exception("Unresolved");
                default:
                    throw new ArgumentOutOfRangeException(nameof(type));
            }
        }
    }
}