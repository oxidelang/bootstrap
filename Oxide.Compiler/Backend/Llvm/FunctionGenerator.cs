using System;
using System.Collections.Generic;
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

            Builder.BuildBr(_blockMap[funcDef.EntryBlock]);

            // Compile bodies
            foreach (var block in funcDef.Blocks)
            {
                CompileBlock(block);
            }

            // Temporary
            Builder.BuildRetVoid();

            Builder.Dispose();
        }

        private void CompileBlock(Block block)
        {
            var mainBlock = _blockMap[block.Id];
            Builder.PositionAtEnd(mainBlock);

            _valueMap = new Dictionary<int, LLVMValueRef>();

            foreach (var instruction in block.Instructions)
            {
                CompileInstruction(instruction);
            }
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