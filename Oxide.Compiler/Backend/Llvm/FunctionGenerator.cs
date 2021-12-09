using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using LLVMSharp.Interop;
using Oxide.Compiler.IR;
using Oxide.Compiler.IR.Instructions;
using Oxide.Compiler.IR.TypeRefs;
using Oxide.Compiler.IR.Types;
using Oxide.Compiler.Middleware;

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

        private GenericContext GenericContext { get; }

        public FunctionGenerator(LlvmBackend backend)
        {
            Backend = backend;
        }

        public void Compile(Function func)
        {
            _func = func;

            if (!func.GenericParams.IsEmpty)
            {
                throw new NotImplementedException("Generic function generation is not implemented");
            }

            Builder = Backend.Context.CreateBuilder();

            _funcRef = GetFunctionRef(func);
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
                var varType = Backend.ConvertType(func.ReturnType, GenericContext);
                _returnSlot = -1;
                _slotMap.Add(_returnSlot.Value, Builder.BuildAlloca(varType, varName));
                _slotDefs.Add(_returnSlot.Value, new SlotDeclaration
                {
                    Id = _returnSlot.Value,
                    Mutable = true,
                    Type = _func.ReturnType
                });
            }

            // TODO: Reuse variable slots
            foreach (var scope in func.Scopes)
            {
                foreach (var slotDef in scope.Slots.Values)
                {
                    var varName = $"scope_{scope.Id}_slot_{slotDef.Id}_{slotDef.Name ?? "autogen"}";
                    var varType = Backend.ConvertType(slotDef.Type, GenericContext);
                    _slotMap.Add(slotDef.Id, Builder.BuildAlloca(varType, varName));
                    _slotDefs.Add(slotDef.Id, slotDef);
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
                    if (!Equals(retType, _func.ReturnType))
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
            StoreSlot(inst.SlotId, ZeroInit(inst.StructType), inst.StructType);
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
                    var baseType = Backend.ConvertType(concreteTypeRef, GenericContext);
                    var structContext = new GenericContext(null, @struct.GenericParams, concreteTypeRef.GenericParams);

                    var consts = new List<LLVMValueRef>();
                    foreach (var fieldDef in @struct.Fields)
                    {
                        consts.Add(ZeroInit(structContext.ResolveRef(fieldDef.Type)));
                    }

                    return LLVMValueRef.CreateConstNamedStruct(baseType, consts.ToArray());
                }
                case Interface @interface:
                case Variant variant:
                    throw new NotImplementedException($"Zero init not implemented for {val}");
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

        private void CompileStaticCallInst(StaticCallInst inst)
        {
            if (inst.TargetImplementation != null)
            {
                throw new NotImplementedException("Targeted calls");
            }

            var name = $"inst_{inst.Id}";
            var funcDef = Store.Lookup<Function>(inst.TargetMethod) ??
                          throw new Exception($"Failed to find unit for {inst.TargetMethod}");
            var funcRef = GetFunctionRef(funcDef);

            if (funcDef.Parameters.Count != inst.Arguments.Count)
            {
                throw new Exception("Invalid number of arguments");
            }

            var args = new List<LLVMValueRef>();

            for (var i = 0; i < funcDef.Parameters.Count; i++)
            {
                var param = funcDef.Parameters[i];
                var (argType, argVal) = LoadSlot(inst.Arguments[i], $"{name}_param_{param.Name}");
                if (!Equals(argType, param.Type))
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

                var value = Builder.BuildCall(funcRef, args.ToArray(), $"{name}_ret");
                StoreSlot(inst.ResultSlot.Value, value, funcDef.ReturnType);
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

        public LLVMValueRef GetFunctionRef(Function func)
        {
            if (!func.GenericParams.IsEmpty)
            {
                throw new NotImplementedException("Generic function support is not implemented");
            }

            var funcName = func.Name.ToString();
            var funcRef = Module.GetNamedFunction(funcName);
            if (funcRef != null) return funcRef;

            var paramTypes = new List<LLVMTypeRef>();
            foreach (var paramDef in func.Parameters)
            {
                if (paramDef.IsThis)
                {
                    throw new NotImplementedException("This parameter support is not implemented");
                }

                paramTypes.Add(Backend.ConvertType(paramDef.Type, GenericContext));
            }

            var returnType = Backend.ConvertType(func.ReturnType, GenericContext);
            var funcType = LLVMTypeRef.CreateFunction(returnType, paramTypes.ToArray());
            funcRef = Module.AddFunction(funcName, funcType);

            return funcRef;
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
                if (!Equals(retType, _func.ReturnType))
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
            var structContext = new GenericContext(null, structDef.GenericParams, structType.GenericParams);
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
            var structContext = new GenericContext(null, structDef.GenericParams, structType.GenericParams);

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