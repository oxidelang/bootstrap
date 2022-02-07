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

public partial class FunctionGenerator
{
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
}