using System;
using System.Collections.Generic;
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

        private FunctionDef _funcDef;

        private LLVMValueRef _funcRef;

        private Dictionary<int, LLVMValueRef> _valueMap;
        private Dictionary<int, LLVMValueRef> _localMap;
        private Dictionary<int, LLVMBasicBlockRef> _blockMap;
        private Dictionary<int, LLVMBasicBlockRef> _scopeReturnMap;
        private LLVMValueRef? _returnSlot;

        private Block CurrentBlock { get; set; }

        public FunctionGenerator(LlvmBackend backend)
        {
            Backend = backend;
        }

        public void Compile(FunctionDef funcDef)
        {
            _funcDef = funcDef;

            if (!funcDef.GenericParams.IsEmpty)
            {
                throw new NotImplementedException("Generic function generation is not implemented");
            }

            Builder = LLVMBuilderRef.Create(Module.Context);

            _funcRef = GetFunctionRef(funcDef);
            if (funcDef.IsExtern)
            {
                // _funcRef.Linkage = LLVMLinkage.LLVMExternalLinkage;
                return;
            }

            var entryBlock = _funcRef.AppendBasicBlock("entry");

            _blockMap = new Dictionary<int, LLVMBasicBlockRef>();
            foreach (var block in funcDef.Blocks)
            {
                _blockMap.Add(block.Id, _funcRef.AppendBasicBlock($"scope_{block.Scope.Id}_block_{block.Id}"));
            }

            // Generate entry block
            Builder.PositionAtEnd(entryBlock);

            // Create storage slot for return value
            if (_funcDef.ReturnType != null)
            {
                var varName = $"return_value";
                var varType = ConvertType(funcDef.ReturnType);
                _returnSlot = Builder.BuildAlloca(varType, varName);
            }

            // Create slots for locals
            _localMap = new Dictionary<int, LLVMValueRef>();

            // TODO: Reuse variable slots
            foreach (var scope in funcDef.Scopes)
            {
                foreach (var varDef in scope.Variables.Values)
                {
                    var varName = $"scope_{scope.Id}_local_{varDef.Id}_{varDef.Name}";
                    var varType = ConvertType(varDef.Type);
                    _localMap.Add(varDef.Id, Builder.BuildAlloca(varType, varName));
                }
            }

            // Load parameters
            foreach (var scope in funcDef.Scopes)
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
            Builder.BuildBr(_blockMap[funcDef.EntryBlock]);

            // Create "return" paths for scopes
            _scopeReturnMap = new Dictionary<int, LLVMBasicBlockRef>();
            foreach (var scope in funcDef.Scopes)
            {
                if (scope.ParentScope == null)
                {
                    CreateScopeReturn(scope);
                }
            }

            // Compile bodies
            foreach (var block in funcDef.Blocks)
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
            foreach (var childScope in _funcDef.Scopes)
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
                case JumpInst jumpInst:
                    throw new NotImplementedException("Jump inst");
                    break;
                case StaticCallInst staticCallInst:
                    CompileStaticCallInst(staticCallInst);
                    break;
                case ReturnInst returnInst:
                    CompileReturnInst(returnInst);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(instruction));
            }
        }

        private void CompileConstInst(ConstInst inst)
        {
            LLVMValueRef value;
            switch (inst.ConstType)
            {
                case ConstInst.PrimitiveType.I32:
                    value = LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, (ulong)(int)inst.Value, true);
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }

            _valueMap.Add(inst.Id, value);
        }

        private void CompileLoadLocalInst(LoadLocalInst inst)
        {
            var val = Builder.BuildLoad(_localMap[inst.LocalId], $"inst_{inst.Id}");
            _valueMap.Add(inst.Id, val);
        }

        private void CompileStoreLocalInst(StoreLocalInst inst)
        {
            Builder.BuildStore(_valueMap[inst.ValueId], _localMap[inst.LocalId]);
        }

        private void CompileArithmeticInstruction(ArithmeticInst inst)
        {
            var left = _valueMap[inst.LhsValue];
            var right = _valueMap[inst.RhsValue];
            var name = $"inst_{inst.Id}";
            LLVMValueRef value;

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

        private void CompileStaticCallInst(StaticCallInst inst)
        {
            var unit = Store.FindUnitForQn(inst.TargetMethod) ??
                       throw new Exception($"Failed to find unit for {inst.TargetMethod}");
            var funDef = unit.Functions[inst.TargetMethod];
            var funcRef = GetFunctionRef(funDef);
            var args = inst.Arguments.Select(x => _valueMap[x]).ToArray();

            var value = Builder.BuildCall(funcRef, args);
            if (_funcDef.ReturnType != null)
            {
                _valueMap.Add(inst.Id, value);
            }
        }

        public LLVMValueRef GetFunctionRef(FunctionDef funcDef)
        {
            if (!funcDef.GenericParams.IsEmpty)
            {
                throw new NotImplementedException("Generic function support is not implemented");
            }

            var funcName = funcDef.Name.ToString();
            var funcRef = Module.GetNamedFunction(funcName);
            if (funcRef != null) return funcRef;

            var paramTypes = new List<LLVMTypeRef>();
            foreach (var paramDef in funcDef.Parameters)
            {
                if (paramDef.IsThis)
                {
                    throw new NotImplementedException("This parameter support is not implemented");
                }

                paramTypes.Add(ConvertType(paramDef.Type));
            }

            var returnType = ConvertType(funcDef.ReturnType);
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

        private LLVMTypeRef ConvertType(TypeDef typeDef)
        {
            if (typeDef == null)
            {
                return LLVMTypeRef.Void;
            }

            if ((typeDef.GenericParams != null && !typeDef.GenericParams.IsEmpty) ||
                typeDef.Source != TypeSource.Concrete)
            {
                throw new NotImplementedException("Generic type support is not implemented");
            }

            LLVMTypeRef baseType;
            if (CommonTypes.I32.Name.Equals(typeDef.Name))
            {
                baseType = LLVMTypeRef.Int32;
            }
            else
            {
                throw new Exception($"Unresolved type {typeDef.Name}");
            }

            switch (typeDef.Category)
            {
                case TypeCategory.Direct:
                    return baseType;
                case TypeCategory.Pointer:
                    throw new NotImplementedException("Pointer types not implemented");
                    break;
                case TypeCategory.Reference:
                    throw new NotImplementedException("Reference types not implemented");
                    break;
                case TypeCategory.StrongReference:
                    throw new NotImplementedException("Strong reference types not implemented");
                    break;
                case TypeCategory.WeakReference:
                    throw new NotImplementedException("Weak reference types not implemented");
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }
    }
}