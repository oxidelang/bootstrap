using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Oxide.Compiler.IR;
using Oxide.Compiler.IR.Instructions;
using Oxide.Compiler.IR.TypeRefs;
using Oxide.Compiler.IR.Types;
using Oxide.Compiler.Middleware;
using Oxide.Compiler.Parser;

namespace Oxide.Compiler.Frontend;

public class BodyParser
{
    private readonly IrStore _store;
    private readonly IrUnit _unit;
    private readonly FileParser _fileParser;
    private readonly Function _function;

    public List<Scope> Scopes { get; private set; }
    private int _lastScopeId;
    private Scope CurrentScope { get; set; }

    public int LastSlotId { get; set; }

    public Dictionary<int, Block> Blocks { get; private set; }
    private int _lastBlockId;
    private int _currentBlockId;
    private Block CurrentBlock => Blocks[_currentBlockId];

    public int LastInstId { get; set; }

    public ConcreteTypeRef ThisType { get; }

    public WhereConstraints Constraints { get; }

    private ImmutableArray<string> _parentGenerics;

    public BodyParser(IrStore store, IrUnit unit, FileParser fileParser, Function function,
        ConcreteTypeRef thisType, ImmutableArray<string> parentGenerics, WhereConstraints constraints)
    {
        _store = store;
        _unit = unit;
        _function = function;
        _fileParser = fileParser;
        ThisType = thisType;
        Scopes = new List<Scope>();
        Blocks = new Dictionary<int, Block>();
        _lastScopeId = 0;
        _lastBlockId = 0;
        LastSlotId = 0;
        _parentGenerics = parentGenerics;
        Constraints = constraints;
    }

    public int ParseBody(OxideParser.BlockContext ctx)
    {
        var scope = PushScope(false);

        for (var i = 0; i < _function.Parameters.Count; i++)
        {
            var paramDef = _function.Parameters[i];
            if (paramDef.IsThis && i != 0)
            {
                throw new Exception("This params are only supported in the first position");
            }

            scope.DefineSlot(new SlotDeclaration
            {
                Id = ++LastSlotId,
                Name = paramDef.Name,
                Type = paramDef.Type,
                Mutable = false,
                ParameterSource = i
            });
        }

        var block = NewBlock(CurrentScope);
        MakeCurrent(block);

        // TODO: Init context
        var finalOp = ParseBlock(ctx);

        if (!CurrentBlock.HasTerminated)
        {
            if (finalOp != null)
            {
                var slot = finalOp.GenerateMove(this, CurrentBlock);
                if (!Equals(_function.ReturnType, slot.Type))
                {
                    throw new Exception("Implicit return value did not match function return type");
                }

                CurrentBlock.AddInstruction(new ReturnInst
                {
                    Id = ++LastInstId,
                    ReturnSlot = slot.Id
                });
            }
            else
            {
                if (_function.ReturnType != null)
                {
                    throw new Exception($"Function does not return value");
                }

                CurrentBlock.AddInstruction(new ReturnInst
                {
                    Id = ++LastInstId
                });
            }
        }

        return block.Id;
    }

    private UnrealisedAccess ParseBlock(OxideParser.BlockContext ctx)
    {
        if (ctx.statement() != null)
        {
            foreach (var statement in ctx.statement())
            {
                ParseStatement(statement);
            }
        }

        return ctx.expression() != null ? ParseExpression(ctx.expression()) : null;
    }

    private void ParseStatement(OxideParser.StatementContext ctx)
    {
        switch (ctx)
        {
            case OxideParser.Empty_statementContext:
                // Ignore empty statement
                break;
            case OxideParser.Block_expression_statementContext blockExpressionStatementContext:
                ParseBlockExpression(blockExpressionStatementContext.block_expression());
                break;
            case OxideParser.Expression_statementContext expressionStatementContext:
                ParseExpression(expressionStatementContext.expression());
                break;
            case OxideParser.Loop_statement_topContext loopStatementTopContext:
                ParseLoopStatement(loopStatementTopContext.loop_statement());
                break;
            case OxideParser.Variable_statement_topContext variableStatementTopContext:
                ParseVariableStatement(variableStatementTopContext.variable_statement());
                break;
            case OxideParser.Assign_statement_topContext assignStatementTopContext:
                ParseAssignStatement(assignStatementTopContext.assign_statement());
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(ctx));
        }
    }

    private void ParseLoopStatement(OxideParser.Loop_statementContext ctx)
    {
        var originalScope = CurrentScope;
        var originalBlock = CurrentBlock;

        // Configure jump paths
        var returnBlock = NewBlock(originalScope);
        PushScope(false);
        var condBlock = NewBlock(CurrentScope);
        RestoreScope(originalScope);
        PushScope(false);
        var bodyBlock = NewBlock(CurrentScope);
        RestoreScope(originalScope);

        // Create initial jump
        MakeCurrent(originalBlock);
        CurrentBlock.AddInstruction(new JumpInst
        {
            Id = ++LastInstId,
            TargetBlock = condBlock.Id,
        });

        // Create condition block
        MakeCurrent(condBlock);
        var condSlot = ParseExpression(ctx.expression()).GenerateMove(this, CurrentBlock);
        if (!Equals(condSlot.Type, PrimitiveKind.Bool.GetRef()))
        {
            throw new Exception("Non-bool value");
        }

        CurrentBlock.AddInstruction(new JumpInst
        {
            Id = ++LastInstId,
            ConditionSlot = condSlot.Id,
            TargetBlock = bodyBlock.Id,
            ElseBlock = returnBlock.Id
        });

        // Create loop body
        MakeCurrent(bodyBlock);
        ParseBlock(ctx.block());
        CurrentBlock.AddInstruction(new JumpInst
        {
            Id = ++LastInstId,
            TargetBlock = condBlock.Id
        });

        // Continue in return block
        MakeCurrent(returnBlock);
    }

    private void ParseAssignStatement(OxideParser.Assign_statementContext inst)
    {
        var exp = ParseExpression(inst.expression()).GenerateMove(this, CurrentBlock);
        if (exp == null)
        {
            throw new Exception($"No value returned");
        }

        switch (inst.assign_target())
        {
            case OxideParser.Deref_assign_targetContext derefAssignTargetContext:
            {
                var addr = ParseUnaryExpression(derefAssignTargetContext.unary_expression());
                if (addr == null)
                {
                    throw new Exception($"Cannot store into no value");
                }

                var addrSlot = addr.GenerateMove(this, CurrentBlock);
                TypeRef targetType;
                switch (addrSlot.Type)
                {
                    case BorrowTypeRef borrowTypeRef:
                        if (!borrowTypeRef.MutableRef)
                        {
                            throw new Exception("Cannot store into non-mutable pointer");
                        }

                        targetType = borrowTypeRef.InnerType;
                        break;
                    case PointerTypeRef pointerTypeRef:
                        if (!pointerTypeRef.MutableRef)
                        {
                            throw new Exception("Cannot store into non-mutable pointer");
                        }

                        targetType = pointerTypeRef.InnerType;
                        break;
                    case ReferenceTypeRef referenceTypeRef:
                        throw new Exception("Cannot store into reference type");
                    case ThisTypeRef thisTypeRef:
                    case ConcreteTypeRef concreteTypeRef:
                    case GenericTypeRef genericTypeRef:
                    case DerivedTypeRef derivedTypeRef:
                        throw new Exception("Cannot store into base type");
                    default:
                        throw new ArgumentOutOfRangeException();
                }

                int valueSlot;
                if (inst.assign_op() is OxideParser.Equal_assign_opContext)
                {
                    valueSlot = exp.Id;
                }
                else
                {
                    var tempDec = CurrentScope.DefineSlot(new SlotDeclaration
                    {
                        Id = ++LastSlotId,
                        Name = null,
                        Type = targetType,
                        Mutable = true
                    });
                    valueSlot = tempDec.Id;

                    CurrentBlock.AddInstruction(new LoadIndirectInst
                    {
                        Id = ++LastInstId,
                        TargetSlot = valueSlot,
                        AddressSlot = addrSlot.Id
                    });

                    CurrentBlock.AddInstruction(new ArithmeticInst
                    {
                        Id = ++LastInstId,
                        ResultSlot = valueSlot,
                        LhsValue = valueSlot,
                        RhsValue = exp.Id,
                        Op = inst.assign_op() switch
                        {
                            OxideParser.Minus_assign_opContext => ArithmeticInst.Operation.Minus,
                            OxideParser.Plus_assign_opContext => ArithmeticInst.Operation.Add,
                            _ => throw new ArgumentOutOfRangeException()
                        }
                    });
                }

                CurrentBlock.AddInstruction(new StoreIndirectInst
                {
                    Id = ++LastInstId,
                    TargetSlot = addrSlot.Id,
                    ValueSlot = valueSlot
                });
                break;
            }
            case OxideParser.Field_assign_targetContext fieldAssignTargetContext:
            {
                var fieldName = fieldAssignTargetContext.name().GetText();
                var tgt = ParseBaseExpression(fieldAssignTargetContext.base_expression());
                if (tgt == null)
                {
                    throw new Exception($"Cannot access {fieldName} on value with no type");
                }

                var structType = tgt.Type.GetBaseType() switch
                {
                    ConcreteTypeRef concreteTypeRef => concreteTypeRef,
                    ThisTypeRef => ThisType ?? throw new Exception("This is not valid in this context"),
                    DerivedTypeRef => throw new Exception("Cannot access field of derived generic type"),
                    GenericTypeRef => throw new Exception("Cannot access field of generic type"),
                    _ => throw new ArgumentOutOfRangeException()
                };

                var structDef = Lookup<Struct>(structType.Name);
                var structContext =
                    new GenericContext(null, structDef.GenericParams, structType.GenericParams, null);
                var fieldDef = structDef.Fields.Single(x => x.Name == fieldName);
                var fieldType = structContext.ResolveRef(fieldDef.Type);

                if (!exp.Type.Equals(fieldType))
                {
                    throw new Exception($"Cannot assign {exp.Type} to {fieldDef.Type}");
                }

                var fieldTgt = new FieldUnrealisedAccess(
                    tgt,
                    fieldDef.Name,
                    fieldType,
                    fieldDef.Mutable
                );
                var fieldSlot = fieldTgt.GenerateRef(this, CurrentBlock, true);

                int valueSlot;
                if (inst.assign_op() is OxideParser.Equal_assign_opContext)
                {
                    valueSlot = exp.Id;
                }
                else
                {
                    var tempDec = CurrentScope.DefineSlot(new SlotDeclaration
                    {
                        Id = ++LastSlotId,
                        Name = null,
                        Type = fieldType,
                        Mutable = true
                    });
                    valueSlot = tempDec.Id;

                    CurrentBlock.AddInstruction(new LoadIndirectInst
                    {
                        Id = ++LastInstId,
                        TargetSlot = valueSlot,
                        AddressSlot = fieldSlot.Id
                    });

                    CurrentBlock.AddInstruction(new ArithmeticInst
                    {
                        Id = ++LastInstId,
                        ResultSlot = valueSlot,
                        LhsValue = valueSlot,
                        RhsValue = exp.Id,
                        Op = inst.assign_op() switch
                        {
                            OxideParser.Minus_assign_opContext => ArithmeticInst.Operation.Minus,
                            OxideParser.Plus_assign_opContext => ArithmeticInst.Operation.Add,
                            _ => throw new ArgumentOutOfRangeException()
                        }
                    });
                }

                CurrentBlock.AddInstruction(new StoreIndirectInst
                {
                    Id = ++LastInstId,
                    TargetSlot = fieldSlot.Id,
                    ValueSlot = valueSlot
                });
                break;
            }
            case OxideParser.Qualified_assign_targetContext qualifiedAssignTargetContext:
            {
                var varDec = ResolveQn(qualifiedAssignTargetContext.qualified_name(),
                    qualifiedAssignTargetContext.type_generic_params());

                if (!exp.Type.Equals(varDec.Type))
                {
                    throw new Exception($"Cannot assign {exp.Type} to {varDec.Type}");
                }

                if (inst.assign_op() is OxideParser.Equal_assign_opContext)
                {
                    CurrentBlock.AddInstruction(new MoveInst
                    {
                        Id = ++LastInstId,
                        DestSlot = varDec.Id,
                        SrcSlot = exp.Id
                    });
                }
                else
                {
                    CurrentBlock.AddInstruction(new ArithmeticInst
                    {
                        Id = ++LastInstId,
                        ResultSlot = varDec.Id,
                        LhsValue = varDec.Id,
                        RhsValue = exp.Id,
                        Op = inst.assign_op() switch
                        {
                            OxideParser.Minus_assign_opContext => ArithmeticInst.Operation.Minus,
                            OxideParser.Plus_assign_opContext => ArithmeticInst.Operation.Add,
                            _ => throw new ArgumentOutOfRangeException()
                        }
                    });
                }

                break;
            }
            default:
                throw new ArgumentOutOfRangeException();
        }
    }

    private UnrealisedAccess ParseBlockExpression(OxideParser.Block_expressionContext ctx)
    {
        switch (ctx)
        {
            case OxideParser.Block_block_expressionContext child:
            {
                var originalScope = CurrentScope;
                var originalBlock = CurrentBlock;

                PushScope(child.UNSAFE() != null);
                var block = NewBlock(CurrentScope);
                MakeCurrent(block);

                originalBlock.AddInstruction(new JumpInst
                {
                    Id = ++LastInstId,
                    TargetBlock = block.Id
                });

                var finalOpSlot = ParseBlock(child.block());

                var finalBlock = CurrentBlock;
                if (finalBlock.HasTerminated)
                {
                    return null;
                }

                var returnBlock = NewBlock(originalScope);
                MakeCurrent(returnBlock);

                if (finalOpSlot != null)
                {
                    var slot = finalOpSlot.GenerateMove(this, finalBlock);

                    var resultDec = originalScope.DefineSlot(new SlotDeclaration
                    {
                        Id = ++LastSlotId,
                        Name = null,
                        Type = slot.Type,
                        Mutable = false
                    });

                    finalBlock.AddInstruction(new MoveInst
                    {
                        Id = ++LastInstId,
                        SrcSlot = slot.Id,
                        DestSlot = resultDec.Id
                    });
                    finalBlock.AddInstruction(new JumpInst
                    {
                        Id = ++LastInstId,
                        TargetBlock = returnBlock.Id
                    });
                    return new SlotUnrealisedAccess(resultDec);
                }

                finalBlock.AddInstruction(new JumpInst
                {
                    Id = ++LastInstId,
                    TargetBlock = returnBlock.Id
                });
                return null;
            }
            case OxideParser.If_block_expressionContext child:
                return ParseIfExpression(child.if_expression());
            default:
                throw new ArgumentOutOfRangeException(nameof(ctx));
        }
    }

    private UnrealisedAccess ParseIfExpression(OxideParser.If_expressionContext ctx)
    {
        var originalScope = CurrentScope;
        var originalBlock = CurrentBlock;

        // Configure jump paths
        var returnBlock = NewBlock(originalScope);
        PushScope(false);
        var trueBlock = NewBlock(CurrentScope);
        RestoreScope(originalScope);

        var hasElse = ctx.else_block != null || ctx.else_if != null;
        Block falseBlock = null;
        if (hasElse)
        {
            PushScope(false);
            falseBlock = NewBlock(CurrentScope);
            RestoreScope(originalScope);
        }

        MakeCurrent(originalBlock);

        switch (ctx.if_condition())
        {
            case OxideParser.Simple_if_conditionContext simpleIfConditionContext:
            {
                var condSlot = ParseExpression(simpleIfConditionContext.expression())
                    .GenerateMove(this, CurrentBlock);
                if (!Equals(condSlot.Type, PrimitiveKind.Bool.GetRef()))
                {
                    throw new Exception("Non-bool value");
                }

                originalBlock.AddInstruction(new JumpInst
                {
                    Id = ++LastInstId,
                    ConditionSlot = condSlot.Id,
                    TargetBlock = trueBlock.Id,
                    ElseBlock = hasElse ? falseBlock.Id : returnBlock.Id
                });
                break;
            }
            case OxideParser.Var_if_conditionContext ifVar:
            {
                var variantSlot = ParseExpression(ifVar.expression())
                    .GenerateMove(this, CurrentBlock);

                var qns = ifVar.qualified_name();
                ConcreteTypeRef variantType;
                string itemName;

                if (ifVar.qn_generics != null)
                {
                    variantType = new ConcreteTypeRef(
                        ResolveQN(qns[0].Parse()),
                        ifVar.qn_generics.type().Select(ParseType).ToImmutableArray()
                    );
                    itemName = qns[1].Parse().Parts.Single();
                }
                else
                {
                    var resolvedName = ResolveQN(qns[0].Parse());
                    variantType = new ConcreteTypeRef(
                        new QualifiedName(
                            true,
                            resolvedName.Parts.RemoveAt(resolvedName.Parts.Length - 1)
                        ),
                        ImmutableArray<TypeRef>.Empty
                    );
                    itemName = resolvedName.Parts.Last();
                }

                var variant = Lookup<Variant>(variantType.Name);
                var variantContext = new GenericContext(
                    null,
                    variant.GenericParams,
                    variantType.GenericParams,
                    null
                );

                var itemRef = new ConcreteTypeRef(
                    new QualifiedName(true,
                        variantType.Name.Parts.Add(itemName)
                    ),
                    variantType.GenericParams
                );
                var variantItem = Lookup<Struct>(itemRef.Name);

                var mappings = new Dictionary<string, string>();
                switch (ifVar.if_var_values())
                {
                    case OxideParser.Tuple_if_var_valuesContext tupleCtx:
                    {
                        var names = tupleCtx.name();
                        for (var i = 0; i < names.Length; i++)
                        {
                            var name = names[i].GetText();
                            var fieldName = variantItem.Fields[i].Name;
                            mappings.Add(fieldName, name);
                        }

                        break;
                    }
                    default:
                        throw new ArgumentOutOfRangeException();
                }

                var itemSlot = trueBlock.Scope.DefineSlot(new SlotDeclaration
                {
                    Id = ++LastSlotId,
                    Name = null,
                    Type = variantSlot.Type switch
                    {
                        BaseTypeRef => itemRef,
                        BorrowTypeRef borrowTypeRef => new BorrowTypeRef(
                            itemRef,
                            borrowTypeRef.MutableRef
                        ),
                        PointerTypeRef pointerTypeRef => new BorrowTypeRef(
                            itemRef,
                            pointerTypeRef.MutableRef
                        ),
                        ReferenceTypeRef referenceTypeRef => throw new NotImplementedException(),
                        _ => throw new ArgumentOutOfRangeException()
                    },
                    Mutable = false,
                    SetOnJump = true,
                });

                originalBlock.AddInstruction(new JumpVariantInst
                {
                    Id = ++LastInstId,
                    VariantSlot = variantSlot.Id,
                    VariantItemType = itemRef,
                    ItemSlot = itemSlot.Id,
                    TargetBlock = trueBlock.Id,
                    ElseBlock = hasElse ? falseBlock.Id : returnBlock.Id
                });

                foreach (var pair in mappings)
                {
                    var fieldName = pair.Key;
                    var field = variantItem.Fields.Single(x => x.Name == fieldName);
                    var fieldType = variantContext.ResolveRef(field.Type);
                    var varName = pair.Value;

                    var fieldSlot = trueBlock.Scope.DefineSlot(new SlotDeclaration
                    {
                        Id = ++LastSlotId,
                        Name = varName,
                        Type = variantSlot.Type switch
                        {
                            BaseTypeRef => fieldType,
                            BorrowTypeRef borrowTypeRef => new BorrowTypeRef(
                                fieldType,
                                borrowTypeRef.MutableRef
                            ),
                            PointerTypeRef pointerTypeRef => new BorrowTypeRef(
                                fieldType,
                                pointerTypeRef.MutableRef
                            ),
                            ReferenceTypeRef referenceTypeRef => throw new NotImplementedException(),
                            _ => throw new ArgumentOutOfRangeException()
                        },
                        Mutable = false
                    });

                    switch (variantSlot.Type)
                    {
                        case BaseTypeRef:
                            trueBlock.AddInstruction(new FieldMoveInst
                            {
                                Id = ++LastInstId,
                                BaseSlot = itemSlot.Id,
                                TargetField = fieldName,
                                TargetSlot = fieldSlot.Id,
                            });
                            break;
                        case BorrowTypeRef borrowTypeRef:
                            trueBlock.AddInstruction(new FieldBorrowInst
                            {
                                Id = ++LastInstId,
                                BaseSlot = itemSlot.Id,
                                TargetField = fieldName,
                                TargetSlot = fieldSlot.Id,
                                Mutable = borrowTypeRef.MutableRef
                            });
                            break;
                        case PointerTypeRef pointerTypeRef:
                            throw new NotImplementedException();
                        case ReferenceTypeRef referenceTypeRef:
                            throw new NotImplementedException();
                        default:
                            throw new ArgumentOutOfRangeException();
                    }
                }

                break;
            }
            default:
                throw new ArgumentOutOfRangeException();
        }

        MakeCurrent(trueBlock);
        var trueSlot = ParseBlock(ctx.body)?.GenerateMove(this, CurrentBlock);
        var trueFinalBlock = CurrentBlock;

        SlotDeclaration falseSlot = null;
        Block falseFinalBlock = null;
        if (hasElse)
        {
            MakeCurrent(falseBlock);
            falseSlot = (ctx.else_block != null
                ? ParseBlock(ctx.else_block)
                : ParseIfExpression(ctx.else_if))?.GenerateMove(this, CurrentBlock);
            falseFinalBlock = CurrentBlock;
        }

        if (hasElse && trueFinalBlock.HasTerminated && falseFinalBlock.HasTerminated)
        {
            MakeCurrent(trueBlock);
            RemoveBlock(returnBlock);
        }
        else
        {
            MakeCurrent(returnBlock);
        }


        if (hasElse && trueSlot != null && falseSlot != null && !trueFinalBlock.HasTerminated &&
            !falseFinalBlock.HasTerminated)
        {
            if (!Equals(trueSlot.Type, falseSlot.Type))
            {
                throw new NotImplementedException("Incompatible if block true and false path values");
            }

            var resultDec = originalScope.DefineSlot(new SlotDeclaration
            {
                Id = ++LastSlotId,
                Name = null,
                Type = trueSlot.Type,
                Mutable = false
            });

            if (!trueFinalBlock.HasTerminated)
            {
                trueFinalBlock.AddInstruction(new MoveInst
                {
                    Id = ++LastInstId,
                    SrcSlot = trueSlot.Id,
                    DestSlot = resultDec.Id,
                });
                trueFinalBlock.AddInstruction(new JumpInst
                {
                    Id = ++LastInstId,
                    TargetBlock = returnBlock.Id
                });
            }

            if (!falseFinalBlock.HasTerminated)
            {
                falseFinalBlock.AddInstruction(new MoveInst
                {
                    Id = ++LastInstId,
                    SrcSlot = falseSlot.Id,
                    DestSlot = resultDec.Id
                });
                falseFinalBlock.AddInstruction(new JumpInst
                {
                    Id = ++LastInstId,
                    TargetBlock = returnBlock.Id
                });
            }

            return new SlotUnrealisedAccess(resultDec);
        }

        if (!trueFinalBlock.HasTerminated)
        {
            trueFinalBlock.AddInstruction(new JumpInst
            {
                Id = ++LastInstId,
                TargetBlock = returnBlock.Id
            });
        }

        if (hasElse && !falseFinalBlock.HasTerminated)
        {
            falseFinalBlock.AddInstruction(new JumpInst
            {
                Id = ++LastInstId,
                TargetBlock = returnBlock.Id
            });
        }

        return null;
    }

    private void ParseVariableStatement(OxideParser.Variable_statementContext ctx)
    {
        var name = ctx.name().GetText();
        var type = ctx.type() != null ? ParseType(ctx.type()) : null;
        var mutable = ctx.MUT() != null;
        int? valueSlot = null;

        if (ctx.expression() != null)
        {
            var exp = ParseExpression(ctx.expression())?.GenerateMove(this, CurrentBlock);
            if (exp == null)
            {
                throw new Exception($"No value returned");
            }

            if (type == null || Equals(type, exp.Type))
            {
                type = exp.Type;
            }
            else
            {
                throw new NotImplementedException("Type compatability checking not implemented");
            }

            valueSlot = exp.Id;
        }
        else if (ctx.type() == null)
        {
            throw new Exception("Variable declarations without an expression must have a type");
        }

        if (type == null)
        {
            throw new Exception($"Unable to resolve type for variable {name}");
        }

        var varDec = CurrentScope.DefineSlot(new SlotDeclaration
        {
            Id = ++LastSlotId,
            Name = name,
            Type = type,
            Mutable = mutable
        });

        if (valueSlot.HasValue)
        {
            CurrentBlock.AddInstruction(new MoveInst
            {
                Id = ++LastInstId,
                SrcSlot = valueSlot.Value,
                DestSlot = varDec.Id
            });
        }
    }

    private UnrealisedAccess ParseExpression(OxideParser.ExpressionContext ctx)
    {
        switch (ctx)
        {
            case OxideParser.Pass_expressionContext passExpressionContext:
                return ParseOrExpression(passExpressionContext.or_expression());
            case OxideParser.Return_expressionContext returnExpressionContext:
            {
                var result = returnExpressionContext.or_expression() != null
                    ? (int?)ParseOrExpression(returnExpressionContext.or_expression())
                        .GenerateMove(this, CurrentBlock).Id
                    : null;
                CurrentBlock.AddInstruction(new ReturnInst
                {
                    Id = ++LastInstId,
                    ReturnSlot = result
                });
                return null;
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(ctx));
        }
    }

    private UnrealisedAccess ParseOrExpression(OxideParser.Or_expressionContext ctx)
    {
        switch (ctx)
        {
            case OxideParser.Pass_or_expressionContext passOrExpressionContext:
                return ParseAndExpression(passOrExpressionContext.and_expression());
            case OxideParser.Op_or_expressionContext opOrExpressionContext:
                throw new NotImplementedException("Or expression");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(ctx));
        }
    }

    private UnrealisedAccess ParseAndExpression(OxideParser.And_expressionContext ctx)
    {
        switch (ctx)
        {
            case OxideParser.Pass_and_expressionContext passAndExpressionContext:
                return ParseIncOrExpression(passAndExpressionContext.inc_or_expression());
            case OxideParser.Op_and_expressionContext opAndExpressionContext:
                throw new NotImplementedException("And expression");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(ctx));
        }
    }

    private UnrealisedAccess ParseIncOrExpression(OxideParser.Inc_or_expressionContext ctx)
    {
        switch (ctx)
        {
            case OxideParser.Pass_inc_or_expressionContext passIncOrExpressionContext:
                return ParseExOrExpression(passIncOrExpressionContext.ex_or_expression());
            case OxideParser.Op_inc_or_expressionContext opIncOrExpressionContext:
                throw new NotImplementedException("Inc or expression");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(ctx));
        }
    }

    private UnrealisedAccess ParseExOrExpression(OxideParser.Ex_or_expressionContext ctx)
    {
        switch (ctx)
        {
            case OxideParser.Pass_ex_or_expressionContext passExOrExpressionContext:
                return ParseBitAndExpression(passExOrExpressionContext.bit_and_expression());
            case OxideParser.Op_ex_or_expressionContext opExOrExpressionContext:
                throw new NotImplementedException("Ex or expression");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(ctx));
        }
    }

    private UnrealisedAccess ParseBitAndExpression(OxideParser.Bit_and_expressionContext ctx)
    {
        switch (ctx)
        {
            case OxideParser.Pass_bit_and_expressionContext passBitAndExpressionContext:
                return ParseEqualExpression(passBitAndExpressionContext.equal_expression());
            case OxideParser.Op_bit_and_expressionContext opBitAndExpressionContext:
                throw new NotImplementedException("Bit and expression");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(ctx));
        }
    }

    private UnrealisedAccess ParseEqualExpression(OxideParser.Equal_expressionContext ctx)
    {
        UnrealisedAccess left;
        UnrealisedAccess right;
        ComparisonInst.Operation op;

        switch (ctx)
        {
            case OxideParser.Pass_equal_expressionContext passEqualExpressionContext:
                return ParseComparisonExpression(passEqualExpressionContext.comparison_expression());
            case OxideParser.Eq_equal_expressionContext child:
                left = ParseEqualExpression(child.equal_expression());
                right = ParseComparisonExpression(child.comparison_expression());
                op = ComparisonInst.Operation.Eq;
                break;
            case OxideParser.Ne_equal_expressionContext child:
                left = ParseEqualExpression(child.equal_expression());
                right = ParseComparisonExpression(child.comparison_expression());
                op = ComparisonInst.Operation.NEq;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(ctx));
        }

        return CreateComparison(left, right, op);
    }

    private UnrealisedAccess ParseComparisonExpression(OxideParser.Comparison_expressionContext ctx)
    {
        UnrealisedAccess left;
        UnrealisedAccess right;
        ComparisonInst.Operation op;
        switch (ctx)
        {
            case OxideParser.Pass_comparison_expressionContext passComparisonExpressionContext:
                return ParseCastExpression(passComparisonExpressionContext.cast_expression());
            case OxideParser.Geq_comparison_expressionContext child:
                left = ParseComparisonExpression(child.comparison_expression());
                right = ParseCastExpression(child.cast_expression());
                op = ComparisonInst.Operation.GEq;
                break;
            case OxideParser.Gt_comparison_expressionContext child:
                left = ParseComparisonExpression(child.comparison_expression());
                right = ParseCastExpression(child.cast_expression());
                op = ComparisonInst.Operation.Gt;
                break;
            case OxideParser.Leq_comparison_expressionContext child:
                left = ParseComparisonExpression(child.comparison_expression());
                right = ParseCastExpression(child.cast_expression());
                op = ComparisonInst.Operation.LEq;
                break;
            case OxideParser.Lt_comparison_expressionContext child:
                left = ParseComparisonExpression(child.comparison_expression());
                right = ParseCastExpression(child.cast_expression());
                op = ComparisonInst.Operation.Lt;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(ctx));
        }

        return CreateComparison(left, right, op);
    }

    private UnrealisedAccess CreateComparison(UnrealisedAccess left, UnrealisedAccess right,
        ComparisonInst.Operation op)
    {
        if (!left.Type.Equals(right.Type))
        {
            throw new NotImplementedException("Comparison of different types not implemented");
        }

        var leftSlot = left.GenerateMove(this, CurrentBlock);
        var rightSlot = right.GenerateMove(this, CurrentBlock);

        var resultSlot = CurrentScope.DefineSlot(new SlotDeclaration
        {
            Id = ++LastSlotId,
            Type = PrimitiveKind.Bool.GetRef()
        });

        CurrentBlock.AddInstruction(new ComparisonInst
        {
            Id = ++LastInstId,
            LhsValue = leftSlot.Id,
            RhsValue = rightSlot.Id,
            ResultSlot = resultSlot.Id,
            Op = op
        });
        return new SlotUnrealisedAccess(resultSlot);
    }

    private UnrealisedAccess ParseCastExpression(OxideParser.Cast_expressionContext ctx)
    {
        switch (ctx)
        {
            case OxideParser.Pass_cast_expressionContext passCastExpressionContext:
                return ParseShiftExpression(passCastExpressionContext.shift_expression());
            case OxideParser.Op_cast_expressionContext opCastExpressionContext:
            {
                var exp = ParseCastExpression(opCastExpressionContext.cast_expression());
                var target = ParseType(opCastExpressionContext.type());
                var slot = exp.GenerateMove(this, CurrentBlock);

                var (castable, unsafeCast) = _store.CanCastTypes(slot.Type, target);
                if (!castable)
                {
                    throw new Exception($"Cannot cast from {slot.Type} to {target}");
                }

                if (!CurrentScope.Unsafe && unsafeCast)
                {
                    throw new Exception($"Cast from {slot.Type} to {target} is unsafe");
                }

                var resultSlot = CurrentScope.DefineSlot(new SlotDeclaration
                {
                    Id = ++LastSlotId,
                    Type = target
                });

                CurrentBlock.AddInstruction(new CastInst
                {
                    Id = ++LastInstId,
                    SourceSlot = slot.Id,
                    ResultSlot = resultSlot.Id,
                    TargetType = target
                });

                return new SlotUnrealisedAccess(resultSlot);
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(ctx));
        }
    }

    private UnrealisedAccess ParseShiftExpression(OxideParser.Shift_expressionContext ctx)
    {
        switch (ctx)
        {
            case OxideParser.Pass_shift_expressionContext passShiftExpressionContext:
                return ParseAddExpression(passShiftExpressionContext.add_expression());
            case OxideParser.Left_shift_expressionContext leftShiftExpressionContext:
                throw new NotImplementedException("left shift expression");
                break;
            case OxideParser.Right_shift_expressionContext rightShiftExpressionContext:
                throw new NotImplementedException("right shift expression");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(ctx));
        }
    }

    private UnrealisedAccess ParseAddExpression(OxideParser.Add_expressionContext ctx)
    {
        UnrealisedAccess left;
        UnrealisedAccess right;
        ArithmeticInst.Operation op;

        switch (ctx)
        {
            case OxideParser.Pass_add_expressionContext passAddExpressionContext:
                return ParseMultiplyExpression(passAddExpressionContext.multiply_expression());
            case OxideParser.Minus_add_expressionContext minusAddExpressionContext:
                left = ParseAddExpression(minusAddExpressionContext.add_expression());
                right = ParseMultiplyExpression(minusAddExpressionContext.multiply_expression());
                op = ArithmeticInst.Operation.Minus;
                break;
            case OxideParser.Plus_add_expressionContext plusAddExpressionContext:
                left = ParseAddExpression(plusAddExpressionContext.add_expression());
                right = ParseMultiplyExpression(plusAddExpressionContext.multiply_expression());
                op = ArithmeticInst.Operation.Add;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(ctx));
        }

        if (!left.Type.Equals(right.Type))
        {
            throw new NotImplementedException("Arithmetic of different types not implemented");
        }

        var leftSlot = left.GenerateMove(this, CurrentBlock);
        var rightSlot = right.GenerateMove(this, CurrentBlock);

        var resultSlot = CurrentScope.DefineSlot(new SlotDeclaration
        {
            Id = ++LastSlotId,
            Type = left.Type
        });

        CurrentBlock.AddInstruction(new ArithmeticInst
        {
            Id = ++LastInstId,
            LhsValue = leftSlot.Id,
            RhsValue = rightSlot.Id,
            ResultSlot = resultSlot.Id,
            Op = op
        });

        return new SlotUnrealisedAccess(resultSlot);
    }

    private UnrealisedAccess ParseMultiplyExpression(OxideParser.Multiply_expressionContext ctx)
    {
        switch (ctx)
        {
            case OxideParser.Pass_multiply_expressionContext passMultiplyExpressionContext:
                return ParseUnaryExpression(passMultiplyExpressionContext.unary_expression());
            case OxideParser.Div_multiply_expressionContext divMultiplyExpressionContext:
                throw new NotImplementedException("Div expression");
                break;
            case OxideParser.Mod_multiply_expressionContext modMultiplyExpressionContext:
                throw new NotImplementedException("Mod expression");
                break;
            case OxideParser.Mul_multiply_expressionContext mulMultiplyExpressionContext:
                throw new NotImplementedException("Multiply expression");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(ctx));
        }
    }

    private UnrealisedAccess ParseUnaryExpression(OxideParser.Unary_expressionContext ctx)
    {
        switch (ctx)
        {
            case OxideParser.Pass_unary_expressionContext passUnaryExpressionContext:
                return ParseBaseExpression(passUnaryExpressionContext.base_expression());
            case OxideParser.Minus_unary_expressionContext minusUnaryExpressionContext:
                throw new NotImplementedException("Minus expression");
                break;
            case OxideParser.Not_unary_expressionContext notUnaryExpressionContext:
                throw new NotImplementedException("Not expression");
                break;
            case OxideParser.Ref_unary_expressionContext refUnaryExpressionContext:
                return ParseRefExpression(refUnaryExpressionContext);
            case OxideParser.Deref_unary_expressionContext derefUnaryExpressionContext:
                return ParseDerefExpression(derefUnaryExpressionContext);
            default:
                throw new ArgumentOutOfRangeException(nameof(ctx));
        }
    }

    private UnrealisedAccess ParseRefExpression(OxideParser.Ref_unary_expressionContext ctx)
    {
        var exp = ParseUnaryExpression(ctx.unary_expression());
        if (exp == null)
        {
            throw new Exception("No value to create borrow from");
        }

        if (exp.Type is ReferenceTypeRef { StrongRef: false })
        {
            throw new NotImplementedException("Cannot borrow weak ref");
        }

        var slot = exp.GenerateRef(this, CurrentBlock, ctx.MUT() != null);
        return new SlotUnrealisedAccess(slot);
    }

    private UnrealisedAccess ParseDerefExpression(OxideParser.Deref_unary_expressionContext ctx)
    {
        var exp = ParseUnaryExpression(ctx.unary_expression());
        if (exp == null)
        {
            throw new Exception("No value to create borrow from");
        }

        if (exp.Type is not BorrowTypeRef && exp.Type is not PointerTypeRef)
        {
            throw new NotImplementedException("Can only load borrows or pointers");
        }

        var slot = exp.GenerateMove(this, CurrentBlock);

        TypeRef innerTypeRef;
        switch (slot.Type)
        {
            case BorrowTypeRef borrowTypeRef:
                innerTypeRef = borrowTypeRef.InnerType;
                break;
            case PointerTypeRef pointerTypeRef:
                innerTypeRef = pointerTypeRef.InnerType;
                break;
            default:
                throw new ArgumentOutOfRangeException();
        }

        var properties = _store.GetCopyProperties(innerTypeRef, Constraints);
        if (!properties.CanCopy)
        {
            throw new Exception("Cannot deref non-copyable type");
        }

        var resultSlot = CurrentScope.DefineSlot(new SlotDeclaration
        {
            Id = ++LastSlotId,
            Name = null,
            Type = innerTypeRef,
            Mutable = false
        });

        CurrentBlock.AddInstruction(new LoadIndirectInst
        {
            Id = ++LastInstId,
            AddressSlot = slot.Id,
            TargetSlot = resultSlot.Id
        });

        return new SlotUnrealisedAccess(resultSlot);
    }

    private UnrealisedAccess ParseBaseExpression(OxideParser.Base_expressionContext ctx)
    {
        switch (ctx)
        {
            case OxideParser.Literal_base_expressionContext literalBaseExpressionContext:
                return ParseLiteral(literalBaseExpressionContext.literal());
            case OxideParser.Method_call_base_expressionContext methodCallBaseExpressionContext:
                return ParseMethodCallExpression(methodCallBaseExpressionContext);
            case OxideParser.Access_base_expressionContext accessBaseExpressionContext:
                return ParseAccessExpression(accessBaseExpressionContext);
            case OxideParser.Block_base_expressionContext blockBaseExpressionContext:
                return ParseBlockExpression(blockBaseExpressionContext.block_expression());
            case OxideParser.Bracket_base_expressionContext bracketBaseExpressionContext:
                return ParseExpression(bracketBaseExpressionContext.expression());
            case OxideParser.Function_call_base_expressionContext functionCallBaseExpressionContext:
                return ParseFunctionCall(functionCallBaseExpressionContext);
            case OxideParser.Qualified_base_expressionContext qualifiedBaseExpressionContext:
                return ParseQnExpression(qualifiedBaseExpressionContext);
            case OxideParser.Struct_base_expressionContext structBaseExpressionContext:
                return ParseStructInitialiser(structBaseExpressionContext.struct_initialiser());
            case OxideParser.This_base_expressionContext thisBaseExpressionContext:
                return ParseThisExpression(thisBaseExpressionContext);
            default:
                throw new ArgumentOutOfRangeException(nameof(ctx));
        }
    }

    private UnrealisedAccess ParseMethodCallExpression(OxideParser.Method_call_base_expressionContext ctx)
    {
        var methodName = ctx.name().GetText();

        var baseExp = ParseBaseExpression(ctx.base_expression());
        if (baseExp == null)
        {
            throw new Exception($"Cannot access {methodName} on value with no type");
        }

        var baseType = baseExp.Type.GetBaseType();
        ConcreteTypeRef iface;
        Function func;
        GenericContext functionContext;

        switch (baseType)
        {
            case ConcreteTypeRef concreteTypeRef:
            {
                var result = ResolveFunction(concreteTypeRef, methodName);
                iface = result.Interface;
                func = result.Function;
                functionContext = new GenericContext(null, result.ImplementationGenerics, concreteTypeRef);
                break;
            }
            case DerivedTypeRef derivedTypeRef:
            case GenericTypeRef genericTypeRef:
                throw new NotImplementedException();
            case ThisTypeRef:
            {
                var concreteTypeRef = ThisType ?? throw new Exception("This is not valid in this context");
                var obj = Lookup(concreteTypeRef.Name);
                if (obj.GenericParams != null && obj.GenericParams.Count > 0)
                {
                    throw new NotImplementedException("Generics");
                }

                var result = ResolveFunction(concreteTypeRef, methodName);
                iface = result.Interface;
                func = result.Function;
                functionContext = new GenericContext(null, result.ImplementationGenerics, concreteTypeRef);
                break;
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(baseType));
        }

        if (func == null)
        {
            throw new Exception($"Method not found {methodName}");
        }

        var methodGenerics = ImmutableArray<TypeRef>.Empty;
        if (ctx.method_generics != null)
        {
            methodGenerics = ctx.method_generics.type().Select(ParseType).ToImmutableArray();
            functionContext = new GenericContext(functionContext, func.GenericParams, methodGenerics,
                functionContext.ThisRef);
        }

        var argumentCtxs = ctx.arguments() != null
            ? ctx.arguments().argument()
            : Array.Empty<OxideParser.ArgumentContext>();

        if (argumentCtxs.Length + 1 != func.Parameters.Count)
        {
            throw new Exception($"{func.Name} takes {func.Parameters.Count - 1} parameters");
        }

        var argIds = new List<int>();
        var thisParam = func.Parameters[0];
        if (!thisParam.IsThis)
        {
            throw new Exception("First param not 'this'");
        }

        var thisSlot = CoerceType(functionContext.ResolveRef(thisParam.Type), baseExp);
        argIds.Add(thisSlot.Id);

        for (var i = 0; i < argumentCtxs.Length; i++)
        {
            var argument = argumentCtxs[i];
            var parameter = func.Parameters[1 + i];
            var parameterType = functionContext.ResolveRef(parameter.Type);

            if (argument.label() != null)
            {
                throw new NotImplementedException("Argument labels are not implemented");
            }

            var exp = ParseExpression(argument.expression());
            if (exp == null)
            {
                throw new Exception("Argument does not return a value");
            }

            var expSlot = exp.GenerateMove(this, CurrentBlock);

            if (!expSlot.Type.Equals(parameterType))
            {
                throw new Exception($"Parameter type mismatch {exp.Type} != {parameterType}");
            }

            argIds.Add(expSlot.Id);
        }

        if (func.ReturnType != null)
        {
            var returnType = functionContext.ResolveRef(func.ReturnType);

            var resultDec = CurrentScope.DefineSlot(new SlotDeclaration
            {
                Id = ++LastSlotId,
                Name = null,
                Type = returnType,
                Mutable = false
            });

            CurrentBlock.AddInstruction(new StaticCallInst
            {
                Id = ++LastInstId,
                TargetMethod = new ConcreteTypeRef(func.Name, methodGenerics),
                TargetImplementation = iface,
                TargetType = baseType,
                Arguments = argIds.ToImmutableList(),
                ResultSlot = resultDec.Id
            });

            return new SlotUnrealisedAccess(resultDec);
        }

        CurrentBlock.AddInstruction(new StaticCallInst
        {
            Id = ++LastInstId,
            TargetMethod = new ConcreteTypeRef(func.Name, methodGenerics),
            TargetImplementation = iface,
            TargetType = baseType,
            Arguments = argIds.ToImmutableList(),
        });
        return null;
    }

    private SlotDeclaration CoerceType(TypeRef targetType, UnrealisedAccess current)
    {
        switch (targetType)
        {
            case BaseTypeRef baseTypeRef:
            {
                if (!Equals(current.Type, baseTypeRef))
                {
                    throw new Exception("Type does not match expected");
                }

                return current.GenerateMove(this, CurrentBlock);
            }
            case BorrowTypeRef borrowTypeRef:
            {
                // TODO: Check current type
                switch (current.Type)
                {
                    case BaseTypeRef:
                    {
                        if (!Equals(current.Type, borrowTypeRef.InnerType))
                        {
                            throw new Exception("Type does not match expected");
                        }

                        return current.GenerateRef(this, CurrentBlock, borrowTypeRef.MutableRef);
                    }
                    case BorrowTypeRef existingRef:
                        if (borrowTypeRef.MutableRef && !existingRef.MutableRef)
                        {
                            throw new Exception("Method requires mutable borrow");
                        }

                        if (!Equals(current.Type, borrowTypeRef))
                        {
                            if (Equals(current.Type.GetBaseType(), borrowTypeRef.GetBaseType()) &&
                                !Equals(existingRef.GetBaseType(),
                                    existingRef.InnerType) // Ensure we don't deref type, just indirections
                               )
                            {
                                var addrSlot = current.GenerateMove(this, CurrentBlock);
                                var tempDec = CurrentScope.DefineSlot(new SlotDeclaration
                                {
                                    Id = ++LastSlotId,
                                    Name = null,
                                    Type = existingRef.InnerType,
                                    Mutable = true
                                });

                                CurrentBlock.AddInstruction(new LoadIndirectInst
                                {
                                    Id = ++LastInstId,
                                    TargetSlot = tempDec.Id,
                                    AddressSlot = addrSlot.Id
                                });

                                return CoerceType(targetType, new SlotUnrealisedAccess(tempDec));
                            }

                            throw new Exception("Type does not match expected");
                        }

                        return current.GenerateMove(this, CurrentBlock);
                    case PointerTypeRef pointerTypeRef:
                        throw new NotImplementedException();
                    case ReferenceTypeRef referenceTypeRef:
                    {
                        if (!Equals(referenceTypeRef.InnerType, borrowTypeRef.InnerType))
                        {
                            throw new Exception("Type does not match expected");
                        }

                        var refSlot = current.GenerateMove(this, CurrentBlock);
                        var tempDec = CurrentScope.DefineSlot(new SlotDeclaration
                        {
                            Id = ++LastSlotId,
                            Name = null,
                            Type = borrowTypeRef,
                            Mutable = true
                        });

                        CurrentBlock.AddInstruction(new RefBorrowInst
                        {
                            Id = ++LastInstId,
                            SourceSlot = refSlot.Id,
                            ResultSlot = tempDec.Id
                        });

                        return tempDec;
                    }
                    default:
                        throw new ArgumentOutOfRangeException();
                }
            }
            case PointerTypeRef pointerTypeRef:
            case ReferenceTypeRef referenceTypeRef:
                throw new NotImplementedException();
            default:
                throw new ArgumentOutOfRangeException();
        }
    }

    private FieldUnrealisedAccess ParseAccessExpression(OxideParser.Access_base_expressionContext ctx)
    {
        var fieldName = ctx.name().GetText();

        var exp = ParseBaseExpression(ctx.base_expression());
        if (exp == null)
        {
            throw new Exception($"Cannot access {fieldName} on value with no type");
        }

        var baseType = exp.Type.GetBaseType();
        ConcreteTypeRef structType;
        switch (baseType)
        {
            case ConcreteTypeRef concreteTypeRef:
                structType = concreteTypeRef;
                break;
            case DerivedTypeRef derivedTypeRef:
                throw new NotImplementedException("Derived type field accesses");
            case GenericTypeRef genericTypeRef:
                throw new NotImplementedException("Generic type field accesses");
            case ThisTypeRef thisTypeRef:
            {
                if (ThisType == null)
                {
                    throw new Exception("This expression not expected");
                }

                structType = ThisType;
                break;
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(baseType));
        }

        var structDef = Lookup<Struct>(structType.Name);
        if (structDef == null)
        {
            throw new Exception($"Failed to find {structType.Name}");
        }

        var structContext = new GenericContext(null, structDef.GenericParams, structType.GenericParams, null);

        var fieldDef = structDef.Fields.Single(x => x.Name == fieldName);
        return new FieldUnrealisedAccess(
            exp,
            fieldDef.Name,
            structContext.ResolveRef(fieldDef.Type),
            fieldDef.Mutable
        );
    }

    private UnrealisedAccess ParseStructInitialiser(OxideParser.Struct_initialiserContext ctx)
    {
        var typeRef = ParseDirectType(ctx.direct_type());

        ConcreteTypeRef concreteTypeRef;
        switch (typeRef)
        {
            case ConcreteTypeRef concreteTypeRefInner:
                concreteTypeRef = concreteTypeRefInner;
                break;
            case DerivedTypeRef derivedTypeRef:
            case GenericTypeRef genericTypeRef:
            case ThisTypeRef thisTypeRef:
                throw new NotImplementedException();
            default:
                throw new ArgumentOutOfRangeException(nameof(typeRef));
        }

        var structDef = Lookup<Struct>(concreteTypeRef.Name);
        var structContext = new GenericContext(null, structDef.GenericParams, concreteTypeRef.GenericParams, null);

        var structSlot = CurrentScope.DefineSlot(new SlotDeclaration
        {
            Id = ++LastSlotId,
            Name = null,
            Type = concreteTypeRef,
            Mutable = true
        });

        CurrentBlock.AddInstruction(new AllocStructInst
        {
            Id = ++LastInstId,
            SlotId = structSlot.Id,
            StructType = concreteTypeRef
        });

        var accessSlot = CurrentScope.DefineSlot(new SlotDeclaration
        {
            Id = ++LastSlotId,
            Name = null,
            Type = new BorrowTypeRef(structSlot.Type, true),
            Mutable = false
        });

        CurrentBlock.AddInstruction(new SlotBorrowInst
        {
            Id = ++LastInstId,
            BaseSlot = structSlot.Id,
            Mutable = true,
            TargetSlot = accessSlot.Id
        });

        foreach (var fieldInit in ctx.field_initialiser())
        {
            string fieldName;
            UnrealisedAccess fieldValue;

            switch (fieldInit)
            {
                case OxideParser.Label_field_initialiserContext labelFieldInitialiserContext:
                {
                    fieldName = labelFieldInitialiserContext.label().Parse();
                    fieldValue = ParseExpression(labelFieldInitialiserContext.expression());
                    break;
                }
                case OxideParser.Var_field_initialiserContext varFieldInitialiserContext:
                {
                    fieldName = varFieldInitialiserContext.name().GetText();

                    var sourceSlot = CurrentScope.ResolveVariable(fieldName);
                    if (sourceSlot == null)
                    {
                        throw new Exception($"Unknown local {fieldName}");
                    }

                    fieldValue = new SlotUnrealisedAccess(sourceSlot);
                    break;
                }
                default:
                    throw new ArgumentOutOfRangeException(nameof(fieldInit));
            }

            if (fieldValue == null)
            {
                throw new Exception($"Cannot assign {fieldName} to no value");
            }

            var fieldSlot = fieldValue.GenerateMove(this, CurrentBlock);

            var fieldDef = structDef.Fields.Single(x => x.Name == fieldName);
            var resolvedFieldType = structContext.ResolveRef(fieldDef.Type);

            if (!resolvedFieldType.Equals(fieldSlot.Type))
            {
                throw new Exception($"Incompatible types for {fieldName}");
            }

            var accessFieldSlot = CurrentScope.DefineSlot(new SlotDeclaration
            {
                Id = ++LastSlotId,
                Name = null,
                Type = new BorrowTypeRef(resolvedFieldType, true),
                Mutable = false
            });

            CurrentBlock.AddInstruction(new FieldBorrowInst
            {
                Id = ++LastInstId,
                BaseSlot = accessSlot.Id,
                Mutable = true,
                TargetSlot = accessFieldSlot.Id,
                TargetField = fieldName,
            });

            CurrentBlock.AddInstruction(new StoreIndirectInst
            {
                Id = ++LastInstId,
                TargetSlot = accessFieldSlot.Id,
                ValueSlot = fieldSlot.Id
            });
        }

        // TODO: Check the user provided each field at least once and no more

        return new SlotUnrealisedAccess(structSlot);
    }

    private UnrealisedAccess ParseFunctionCall(OxideParser.Function_call_base_expressionContext ctx)
    {
        var qns = ctx.qualified_name();
        var argumentCtxs = ctx.arguments() != null
            ? ctx.arguments().argument()
            : Array.Empty<OxideParser.ArgumentContext>();

        Function functionDef;
        BaseTypeRef targetType;
        ConcreteTypeRef targetImplementation;
        QualifiedName targetMethod;
        GenericContext functionContext;

        if (ctx.qn_generics != null)
        {
            var targetName = ResolveQN(qns[0].Parse());
            var targetGenerics = ctx.qn_generics.type().Select(ParseType).ToImmutableArray();
            var target = new ConcreteTypeRef(targetName, targetGenerics);
            var functionName = qns[1].Parse().Parts.Single();

            switch (Lookup(targetName))
            {
                case null:
                    throw new Exception($"Unable to resolve {targetName}");
                case Struct:
                    break;
                case Function:
                case PrimitiveType:
                case Interface:
                    throw new Exception("Unexpected type");
                case Variant:
                    return ParseTupleVariantAlloc(target, functionName, argumentCtxs);
                default:
                    throw new ArgumentOutOfRangeException();
            }


            var result = ResolveFunction(target, functionName);
            targetImplementation = result.Interface;
            functionDef = result.Function;
            if (functionDef == null)
            {
                throw new Exception(
                    $"Unable to find resolve {targetName}<{string.Join(", ", targetGenerics)}>::{functionName}");
            }

            targetType = target;
            targetMethod = new QualifiedName(false, new[] { functionName });
            functionContext = new GenericContext(null, result.ImplementationGenerics, null);
        }
        else
        {
            var resolvedName = ResolveQN(qns[0].Parse());
            var resolved = Lookup(resolvedName, true);

            switch (resolved)
            {
                case null:
                    throw new Exception($"Unable to resolve {resolvedName}");
                case Function function:
                    functionDef = function;
                    break;
                case Interface:
                case PrimitiveType:
                case Struct:
                    throw new Exception($"Uncallable type {resolvedName}");
                case Variant:
                    return ParseTupleVariantAlloc(
                        new ConcreteTypeRef(
                            new QualifiedName(
                                true,
                                resolvedName.Parts.RemoveAt(resolvedName.Parts.Length - 1)
                            ),
                            ImmutableArray<TypeRef>.Empty
                        ),
                        resolvedName.Parts.Last(),
                        argumentCtxs
                    );
                default:
                    throw new ArgumentOutOfRangeException(nameof(resolved));
            }

            targetType = null;
            targetImplementation = null;
            targetMethod = functionDef.Name;
            functionContext = GenericContext.Default;
        }

        var methodGenerics = ImmutableArray<TypeRef>.Empty;
        if (ctx.method_generics != null)
        {
            methodGenerics = ctx.method_generics.type().Select(ParseType).ToImmutableArray();
            functionContext = new GenericContext(functionContext, functionDef.GenericParams, methodGenerics,
                functionContext.ThisRef);
        }

        if (argumentCtxs.Length != functionDef.Parameters.Count)
        {
            throw new Exception($"{functionDef.Name} takes {functionDef.Parameters.Count} parameters");
        }

        var argIds = new List<int>();
        for (var i = 0; i < argumentCtxs.Length; i++)
        {
            var argument = argumentCtxs[i];
            var parameter = functionDef.Parameters[i];
            var paramType = functionContext.ResolveRef(parameter.Type);

            if (argument.label() != null)
            {
                throw new NotImplementedException("Argument labels are not implemented");
            }

            var exp = ParseExpression(argument.expression());
            if (exp == null)
            {
                throw new Exception("Argument does not return a value");
            }

            var expSlot = exp.GenerateMove(this, CurrentBlock);

            if (!expSlot.Type.Equals(paramType))
            {
                throw new Exception($"Parameter type mismatch {exp.Type} != {paramType}");
            }

            argIds.Add(expSlot.Id);
        }

        if (functionDef.ReturnType != null)
        {
            var returnType = functionContext.ResolveRef(functionDef.ReturnType);
            var resultDec = CurrentScope.DefineSlot(new SlotDeclaration
            {
                Id = ++LastSlotId,
                Name = null,
                Type = returnType,
                Mutable = false
            });

            CurrentBlock.AddInstruction(new StaticCallInst
            {
                Id = ++LastInstId,
                TargetType = targetType,
                TargetMethod = new ConcreteTypeRef(targetMethod, methodGenerics),
                TargetImplementation = targetImplementation,
                Arguments = argIds.ToImmutableList(),
                ResultSlot = resultDec.Id
            });

            return new SlotUnrealisedAccess(resultDec);
        }

        CurrentBlock.AddInstruction(new StaticCallInst
        {
            Id = ++LastInstId,
            TargetType = targetType,
            TargetMethod = new ConcreteTypeRef(targetMethod, methodGenerics),
            TargetImplementation = targetImplementation,
            Arguments = argIds.ToImmutableList(),
        });
        return null;
    }

    private UnrealisedAccess ParseTupleVariantAlloc(ConcreteTypeRef target, string itemName,
        OxideParser.ArgumentContext[] argumentCtxs)
    {
        var variant = Lookup<Variant>(target.Name);
        var variantContext = new GenericContext(null, variant.GenericParams, target.GenericParams, null);

        if (!variant.TryGetItem(itemName, out var item))
        {
            throw new Exception($"Unknown variant item {itemName}");
        }

        var itemRef = new ConcreteTypeRef(
            new QualifiedName(true, variant.Name.Parts.Add(item.Name)),
            target.GenericParams
        );

        int? itemSlotId = null;
        if (item.Content != null)
        {
            if (item.NamedFields)
            {
                throw new Exception("Cannot use tuple initializer on named struct");
            }

            var variantItemSlot = CurrentScope.DefineSlot(new SlotDeclaration
            {
                Id = ++LastSlotId,
                Name = null,
                Type = itemRef,
                Mutable = true
            });
            itemSlotId = variantItemSlot.Id;

            CurrentBlock.AddInstruction(new AllocStructInst
            {
                Id = ++LastInstId,
                SlotId = variantItemSlot.Id,
                StructType = itemRef
            });

            var accessItemSlot = CurrentScope.DefineSlot(new SlotDeclaration
            {
                Id = ++LastSlotId,
                Name = null,
                Type = new BorrowTypeRef(itemRef, true),
                Mutable = false
            });

            CurrentBlock.AddInstruction(new SlotBorrowInst
            {
                Id = ++LastInstId,
                BaseSlot = variantItemSlot.Id,
                Mutable = true,
                TargetSlot = accessItemSlot.Id
            });

            var fields = item.Content.Fields;
            if (fields.Count != argumentCtxs.Length)
            {
                throw new Exception("Invalid number of tuple arguments");
            }


            for (var i = 0; i < fields.Count; i++)
            {
                var field = fields[i];
                var fieldType = variantContext.ResolveRef(field.Type);
                var arg = argumentCtxs[i];

                if (arg.label() != null)
                {
                    throw new Exception("Argument labels are not supported in tuples");
                }

                var exp = ParseExpression(arg.expression());
                if (exp == null)
                {
                    throw new Exception($"Argument {i} does not return a value");
                }

                var expSlot = exp.GenerateMove(this, CurrentBlock);
                if (!expSlot.Type.Equals(fieldType))
                {
                    throw new Exception($"Field type mismatch {exp.Type} != {fieldType}");
                }

                var accessFieldSlot = CurrentScope.DefineSlot(new SlotDeclaration
                {
                    Id = ++LastSlotId,
                    Name = null,
                    Type = new BorrowTypeRef(fieldType, true),
                    Mutable = false
                });

                CurrentBlock.AddInstruction(new FieldBorrowInst
                {
                    Id = ++LastInstId,
                    BaseSlot = accessItemSlot.Id,
                    Mutable = true,
                    TargetSlot = accessFieldSlot.Id,
                    TargetField = field.Name,
                });

                CurrentBlock.AddInstruction(new StoreIndirectInst
                {
                    Id = ++LastInstId,
                    TargetSlot = accessFieldSlot.Id,
                    ValueSlot = expSlot.Id
                });
            }
        }

        var variantSlot = CurrentScope.DefineSlot(new SlotDeclaration
        {
            Id = ++LastSlotId,
            Name = null,
            Type = target,
            Mutable = true
        });

        CurrentBlock.AddInstruction(new AllocVariantInst
        {
            Id = ++LastInstId,
            SlotId = variantSlot.Id,
            VariantType = target,
            ItemName = item.Name,
            ItemSlot = itemSlotId
        });

        return new SlotUnrealisedAccess(variantSlot);
    }

    private UnrealisedAccess ParseQnExpression(OxideParser.Qualified_base_expressionContext ctx)
    {
        return new SlotUnrealisedAccess(ResolveQn(ctx.qualified_name(), ctx.type_generic_params()));
    }

    private UnrealisedAccess ParseThisExpression(OxideParser.This_base_expressionContext ctx)
    {
        var varDec = CurrentScope.ResolveVariable("this");
        if (varDec == null)
        {
            throw new Exception($"Unknown variable this");
        }

        return new SlotUnrealisedAccess(varDec);
    }

    private SlotDeclaration ResolveQn(OxideParser.Qualified_nameContext[] qns,
        OxideParser.Type_generic_paramsContext typeGenericParams)
    {
        if (typeGenericParams != null)
        {
            throw new NotImplementedException("Generic params on QN expressions not implemented");
        }

        if (qns.Length != 1)
        {
            throw new NotImplementedException("Generic param derived QN not implemented");
        }

        var qn1 = qns[0].Parse();
        if (qn1.Parts.Length > 1)
        {
            throw new NotImplementedException("Non-local QN expressions not implemented");
        }

        var varName = qn1.Parts[0];
        var varDec = CurrentScope.ResolveVariable(varName);
        if (varDec == null)
        {
            throw new Exception($"Unknown variable {varName}");
        }

        return varDec;
    }

    private SlotUnrealisedAccess ParseLiteral(OxideParser.LiteralContext ctx)
    {
        var targetSlot = ++LastSlotId;
        TypeRef slotType;

        switch (ctx)
        {
            case OxideParser.Binary_literalContext binaryLiteralContext:
                throw new NotImplementedException("Binary literal");
                break;
            case OxideParser.Hex_literalContext hexLiteralContext:
                throw new NotImplementedException("Hex literal");
                break;
            case OxideParser.Int_literalContext intLiteralContext:
            {
                var val = int.Parse(intLiteralContext.GetText());
                CurrentBlock.AddInstruction(new ConstInst
                {
                    Id = ++LastInstId,
                    TargetSlot = targetSlot,
                    ConstType = PrimitiveKind.I32,
                    Value = val
                });
                slotType = PrimitiveKind.I32.GetRef();
                break;
            }
            case OxideParser.Outer_bool_literalContext outerBoolLiteralContext:
            {
                switch (outerBoolLiteralContext.boolean_literal())
                {
                    case OxideParser.True_boolean_literalContext:
                    {
                        CurrentBlock.AddInstruction(new ConstInst
                        {
                            Id = ++LastInstId,
                            TargetSlot = targetSlot,
                            ConstType = PrimitiveKind.Bool,
                            Value = true
                        });
                        slotType = PrimitiveKind.Bool.GetRef();
                        break;
                    }
                    case OxideParser.False_boolean_literalContext:
                    {
                        CurrentBlock.AddInstruction(new ConstInst
                        {
                            Id = ++LastInstId,
                            TargetSlot = targetSlot,
                            ConstType = PrimitiveKind.Bool,
                            Value = false
                        });
                        slotType = PrimitiveKind.Bool.GetRef();
                        break;
                    }
                    default:
                        throw new ArgumentOutOfRangeException();
                }

                break;
            }
            case OxideParser.String_literalContext stringLiteralContext:
                throw new NotImplementedException("String literal");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(ctx));
        }

        var slotDec = CurrentScope.DefineSlot(new SlotDeclaration
        {
            Id = targetSlot,
            Type = slotType,
        });
        return new SlotUnrealisedAccess(slotDec);
    }

    private Scope PushScope(bool @unsafe)
    {
        var scope = new Scope
        {
            Id = ++_lastScopeId,
            ParentScope = CurrentScope,
            Unsafe = (CurrentScope?.Unsafe ?? false) || @unsafe,
        };

        CurrentScope = scope;
        Scopes.Add(scope);

        return scope;
    }

    private void RestoreScope(Scope target)
    {
        CurrentScope = target;
    }

    private Block NewBlock(Scope scope)
    {
        var block = new Block
        {
            Id = ++_lastBlockId,
            Scope = scope
        };
        Blocks.Add(block.Id, block);
        return block;
    }

    private void RemoveBlock(Block block)
    {
        Blocks.Remove(block.Id);
    }

    private void MakeCurrent(Block block)
    {
        RestoreScope(block.Scope);
        _currentBlockId = block.Id;
    }

    private QualifiedName ResolveQN(QualifiedName qn)
    {
        return _fileParser.ResolveQN(qn);
    }

    public OxObj Lookup(QualifiedName qn, bool returnVariant = false)
    {
        return _unit.Lookup(qn, returnVariant) ?? _store.Lookup(qn, returnVariant);
    }

    public T Lookup<T>(QualifiedName qn) where T : OxObj
    {
        return _unit.Lookup<T>(qn) ?? _store.Lookup<T>(qn);
    }

    public ResolvedFunction ResolveFunction(ConcreteTypeRef target, string functionName)
    {
        return _unit.ResolveFunction(_store, target, functionName) ?? _store.ResolveFunction(target, functionName);
    }

    private TypeRef ParseType(OxideParser.TypeContext ctx)
    {
        return _fileParser.ParseType(ctx, _function.GenericParams.AddRange(_parentGenerics));
    }

    private BaseTypeRef ParseDirectType(OxideParser.Direct_typeContext ctx)
    {
        return _fileParser.ParseDirectType(ctx, _function.GenericParams.AddRange(_parentGenerics));
    }
}