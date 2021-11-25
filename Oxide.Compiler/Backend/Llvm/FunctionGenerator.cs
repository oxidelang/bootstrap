using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using LLVMSharp.Interop;
using Oxide.Compiler.IR;
using Oxide.Compiler.IR.Instructions;

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

        private Dictionary<int, LLVMValueRef> _valueMap;
        private Dictionary<int, LLVMValueRef> _localMap;
        private Dictionary<int, VariableDeclaration> _localDefs;
        private Dictionary<int, LLVMBasicBlockRef> _blockMap;
        private Dictionary<int, LLVMBasicBlockRef> _scopeReturnMap;
        private LLVMValueRef? _returnSlot;

        private Block CurrentBlock { get; set; }

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

            // Create storage slot for return value
            if (_func.ReturnType != null)
            {
                var varName = $"return_value";
                var varType = Backend.ConvertType(func.ReturnType);
                _returnSlot = Builder.BuildAlloca(varType, varName);
            }

            // Create slots for locals
            _localMap = new Dictionary<int, LLVMValueRef>();
            _localDefs = new Dictionary<int, VariableDeclaration>();

            // TODO: Reuse variable slots
            foreach (var scope in func.Scopes)
            {
                foreach (var varDef in scope.Variables.Values)
                {
                    var varName = $"scope_{scope.Id}_local_{varDef.Id}_{varDef.Name}";
                    var varType = Backend.ConvertType(varDef.Type);
                    _localMap.Add(varDef.Id, Builder.BuildAlloca(varType, varName));
                    _localDefs.Add(varDef.Id, varDef);
                }
            }

            // Load parameters
            foreach (var scope in func.Scopes)
            {
                if (scope.ParentScope != null)
                {
                    continue;
                }

                foreach (var varDef in scope.Variables.Values)
                {
                    if (!varDef.ParameterSource.HasValue)
                    {
                        continue;
                    }

                    Builder.BuildStore(_funcRef.Params[varDef.ParameterSource.Value], _localMap[varDef.Id]);
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
                    var retVal = Builder.BuildLoad(_returnSlot.Value, "loaded_return_value");
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

            _valueMap = new Dictionary<int, LLVMValueRef>();

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
                case ConstInst constInst:
                    CompileConstInst(constInst);
                    break;
                case StoreLocalInst storeLocalInst:
                    CompileStoreLocalInst(storeLocalInst);
                    break;
                case LoadLocalInst loadLocalInst:
                    CompileLoadLocalInst(loadLocalInst);
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
                case StoreFieldInst storeFieldInst:
                    CompileStoreFieldInst(storeFieldInst);
                    break;
                case LoadFieldInst loadFieldInst:
                    CompileLoadFieldInst(loadFieldInst);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(instruction));
            }
        }

        private void CompileLoadFieldInst(LoadFieldInst inst)
        {
            var structDef = Store.Lookup<Struct>(inst.TargetType);
            if (structDef == null)
            {
                throw new Exception($"Cannot find struct {inst.TargetType}");
            }

            var index = structDef.Fields.FindIndex(x => x.Name == inst.TargetField);
            var fieldDef = structDef.Fields[index];
            var addr = Builder.BuildInBoundsGEP(
                _valueMap[inst.TargetId],
                new[]
                {
                    LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0),
                    LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, (ulong)index)
                },
                $"inst_{inst.Id}_addr"
            );

            _valueMap.Add(inst.Id, CreateLoad(addr, fieldDef.Type, $"inst_{inst.Id}_load"));
        }

        private void CompileLoadLocalInst(LoadLocalInst inst)
        {
            // TODO: Check local type
            _valueMap.Add(inst.Id, CreateLoad(_localMap[inst.LocalId], inst.LocalType, $"inst_{inst.Id}"));
        }

        private LLVMValueRef CreateLoad(LLVMValueRef ptr, TypeRef typeRef, string name)
        {
            if (typeRef.Source != TypeSource.Concrete)
            {
                throw new NotImplementedException("Non concrete types not implemented");
            }

            if (typeRef.GenericParams != null && typeRef.GenericParams.Length > 0)
            {
                throw new NotImplementedException("Generics");
            }

            var obj = Store.Lookup(typeRef.Name);
            if (obj == null)
            {
                throw new Exception($"Failed to find {typeRef}");
            }

            return CreateLoad(ptr, obj, name);
        }

        private LLVMValueRef CreateLoad(LLVMValueRef ptr, OxObj type, string name)
        {
            LLVMValueRef value;
            switch (type)
            {
                case PrimitiveType:
                    value = Builder.BuildLoad(ptr, name);
                    break;
                case Struct:
                    value = Builder.BuildInBoundsGEP(
                        ptr,
                        new[] { LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0) },
                        name
                    );
                    break;
                case Function function:
                case Interface iface:
                case Variant variant:
                    throw new NotImplementedException();
                default:
                    throw new ArgumentOutOfRangeException(nameof(type));
            }

            return value;
        }

        private void CompileStoreFieldInst(StoreFieldInst inst)
        {
            var structDef = Store.Lookup<Struct>(inst.TargetType);
            if (structDef == null)
            {
                throw new Exception($"Cannot find struct {inst.TargetType}");
            }

            var index = structDef.Fields.FindIndex(x => x.Name == inst.TargetField);
            var fieldDef = structDef.Fields[index];
            var addr = Builder.BuildInBoundsGEP(
                _valueMap[inst.TargetId],
                new[]
                {
                    LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0),
                    LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, (ulong)index)
                },
                $"inst_{inst.Id}_addr"
            );

            CreateStore(addr, _valueMap[inst.ValueId], fieldDef.Type, $"inst_{inst.Id}");
        }

        private void CompileStoreLocalInst(StoreLocalInst inst)
        {
            // TODO: Check local type
            CreateStore(_localMap[inst.LocalId], _valueMap[inst.ValueId], _localDefs[inst.LocalId].Type,
                $"inst_{inst.Id}");
        }

        private void CreateStore(LLVMValueRef ptr, LLVMValueRef value, TypeRef typeRef, string name)
        {
            if (typeRef.Source != TypeSource.Concrete)
            {
                throw new NotImplementedException("Non concrete types not implemented");
            }

            if (typeRef.GenericParams != null && typeRef.GenericParams.Length > 0)
            {
                throw new NotImplementedException("Generics");
            }

            var obj = Store.Lookup(typeRef.Name);
            if (obj == null)
            {
                throw new Exception($"Failed to find {typeRef}");
            }

            CreateStore(ptr, value, obj, name);
        }

        private void CreateStore(LLVMValueRef ptr, LLVMValueRef value, OxObj type, string name)
        {
            switch (type)
            {
                case PrimitiveType:
                    Builder.BuildStore(value, ptr);
                    break;
                case Struct stct:
                {
                    for (var i = 0; i < stct.Fields.Count; i++)
                    {
                        var field = stct.Fields[i];

                        var sourceAddr = Builder.BuildInBoundsGEP(
                            value,
                            new[]
                            {
                                LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0),
                                LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, (ulong)i)
                            },
                            $"{name}_{field.Name}_saddr"
                        );

                        var source = CreateLoad(sourceAddr, field.Type, $"{name}_{field.Name}_load");

                        var destAddr = Builder.BuildInBoundsGEP(
                            ptr,
                            new[]
                            {
                                LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0),
                                LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, (ulong)i)
                            },
                            $"{name}_{field.Name}_daddr"
                        );

                        CreateStore(destAddr, source, field.Type, $"{name}_{field.Name}_store");
                    }

                    break;
                }
                case Function function:
                case Interface iface:
                case Variant variant:
                    throw new NotImplementedException();
                default:
                    throw new ArgumentOutOfRangeException(nameof(type));
            }
        }

        private void CompileAllocStructInst(AllocStructInst inst)
        {
            var baseType = Backend.ResolveBaseType(inst.StructName);
            var structConst = LLVMValueRef.CreateConstNamedStruct(baseType, new LLVMValueRef[0]);
            Builder.BuildStore(structConst, _localMap[inst.LocalId]);
        }

        private void CompileJumpInst(JumpInst jumpInst)
        {
            if (jumpInst.ConditionValue.HasValue)
            {
                Builder.BuildCondBr(_valueMap[jumpInst.ConditionValue.Value], _blockMap[jumpInst.TargetBlock],
                    _blockMap[jumpInst.ElseBlock]);
            }
            else
            {
                Builder.BuildBr(_blockMap[jumpInst.TargetBlock]);
            }

            // TODO: Real cleanup
            // throw new NotImplementedException("Jump inst");
        }

        private void CompileConstInst(ConstInst inst)
        {
            LLVMValueRef value;
            switch (inst.ConstType)
            {
                case ConstInst.ConstPrimitiveType.I32:
                    value = LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, (ulong)(int)inst.Value, true);
                    break;
                case ConstInst.ConstPrimitiveType.Bool:
                    value = LLVMValueRef.CreateConstInt(LLVMTypeRef.Int1, (ulong)((bool)inst.Value ? 1 : 0), true);
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }

            _valueMap.Add(inst.Id, value);
        }

        private void CompileArithmeticInstruction(ArithmeticInst inst)
        {
            var leftInst = GetInst(inst.LhsValue);
            var left = _valueMap[inst.LhsValue];
            var rightInst = GetInst(inst.RhsValue);
            var right = _valueMap[inst.RhsValue];
            var name = $"inst_{inst.Id}";
            LLVMValueRef value;

            if (!Equals(leftInst.ValueType, rightInst.ValueType))
            {
                throw new Exception("Lhs and rhs have different type");
            }

            var integer = IsIntegerBacked(leftInst.ValueType);
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

            _valueMap.Add(inst.Id, value);
        }

        private void CompileComparisonInst(ComparisonInst inst)
        {
            var leftInst = GetInst(inst.LhsValue);
            var left = _valueMap[inst.LhsValue];
            var rightInst = GetInst(inst.RhsValue);
            var right = _valueMap[inst.RhsValue];
            var name = $"inst_{inst.Id}";
            LLVMValueRef value;

            if (!Equals(leftInst.ValueType, rightInst.ValueType))
            {
                throw new Exception("Lhs and rhs have different type");
            }

            var integer = IsIntegerBacked(leftInst.ValueType);
            if (!integer)
            {
                throw new NotImplementedException("Comparison of non-integers not implemented");
            }

            var signed = IsSignedInteger(leftInst.ValueType);

            switch (inst.Op)
            {
                case ComparisonInst.Operation.Eq:
                    value = Builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, left, right, name);
                    break;
                case ComparisonInst.Operation.NEq:
                    value = Builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, left, right, name);
                    break;
                case ComparisonInst.Operation.GEq:
                    value = Builder.BuildICmp(signed ? LLVMIntPredicate.LLVMIntSGE : LLVMIntPredicate.LLVMIntUGE, left,
                        right, name);
                    break;
                case ComparisonInst.Operation.LEq:
                    value = Builder.BuildICmp(signed ? LLVMIntPredicate.LLVMIntSLE : LLVMIntPredicate.LLVMIntULE, left,
                        right, name);
                    break;
                case ComparisonInst.Operation.Gt:
                    value = Builder.BuildICmp(signed ? LLVMIntPredicate.LLVMIntSGT : LLVMIntPredicate.LLVMIntUGT, left,
                        right, name);
                    break;
                case ComparisonInst.Operation.Lt:
                    value = Builder.BuildICmp(signed ? LLVMIntPredicate.LLVMIntSLT : LLVMIntPredicate.LLVMIntULT, left,
                        right, name);
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }

            _valueMap.Add(inst.Id, value);
        }

        private bool IsIntegerBacked(TypeRef typeRef)
        {
            return Equals(typeRef.Name, PrimitiveType.I32.Name) || Equals(typeRef.Name, PrimitiveType.Bool.Name);
        }

        private bool IsSignedInteger(TypeRef typeRef)
        {
            return Equals(typeRef.Name, PrimitiveType.I32.Name) || Equals(typeRef.Name, PrimitiveType.Bool.Name);
        }

        private void CompileStaticCallInst(StaticCallInst inst)
        {
            var name = $"inst_{inst.Id}";
            var funcDef = Store.Lookup<Function>(inst.TargetMethod) ??
                          throw new Exception($"Failed to find unit for {inst.TargetMethod}");
            var funcRef = GetFunctionRef(funcDef);
            var args = inst.Arguments.Select(x => _valueMap[x]).ToArray();

            if (inst.ReturnType != null)
            {
                var value = Builder.BuildCall(funcRef, args, name);
                _valueMap.Add(inst.Id, value);
            }
            else
            {
                Builder.BuildCall(funcRef, args);
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

                paramTypes.Add(Backend.ConvertType(paramDef.Type));
            }

            var returnType = Backend.ConvertType(func.ReturnType);
            var funcType = LLVMTypeRef.CreateFunction(returnType, paramTypes.ToArray());
            funcRef = Module.AddFunction(funcName, funcType);

            return funcRef;
        }

        private void CompileReturnInst(ReturnInst inst)
        {
            if (inst.ResultValue.HasValue != _returnSlot.HasValue)
            {
                throw new Exception("Invalid return expression");
            }

            if (inst.ResultValue.HasValue)
            {
                var value = _valueMap[inst.ResultValue.Value];
                Builder.BuildStore(value, _returnSlot.Value);
            }

            Builder.BuildBr(_scopeReturnMap[CurrentBlock.Scope.Id]);
        }

        private Instruction GetInst(int id)
        {
            return _func.Blocks.SelectMany(block => block.Instructions).FirstOrDefault(inst => inst.Id == id);
        }
    }
}