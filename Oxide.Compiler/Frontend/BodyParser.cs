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

namespace Oxide.Compiler.Frontend
{
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

        public GenericContext GenericContext { get; private set; }

        public BodyParser(IrStore store, IrUnit unit, FileParser fileParser, Function function)
        {
            _store = store;
            _unit = unit;
            _function = function;
            _fileParser = fileParser;
            Scopes = new List<Scope>();
            Blocks = new Dictionary<int, Block>();
            _lastScopeId = 0;
            _lastBlockId = 0;
            LastSlotId = 0;
        }

        public int ParseBody(OxideParser.BlockContext ctx)
        {
            var scope = PushScope();

            for (var i = 0; i < _function.Parameters.Count; i++)
            {
                var paramDef = _function.Parameters[i];
                if (paramDef.IsThis)
                {
                    throw new NotImplementedException("This params are not supported");
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

            // TODO: Improve
            if (!CurrentBlock.HasTerminated)
            {
                // TODO: Attempt to return value from finalOp

                if (_function.ReturnType != null)
                {
                    throw new Exception("Function does not return value");
                }

                CurrentBlock.AddInstruction(new ReturnInst
                {
                    Id = ++LastInstId
                });
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

        private void ParseAssignStatement(OxideParser.Assign_statementContext assignStatementContext)
        {
            var exp = ParseExpression(assignStatementContext.expression()).GenerateMove(this, CurrentBlock);
            if (exp == null)
            {
                throw new Exception($"No value returned");
            }

            switch (assignStatementContext.assign_target())
            {
                case OxideParser.Deref_assign_targetContext derefAssignTargetContext:
                {
                    var addr = ParseUnaryExpression(derefAssignTargetContext.unary_expression());
                    if (addr == null)
                    {
                        throw new Exception($"Cannot store into no value");
                    }

                    var addrSlot = addr.GenerateMove(this, CurrentBlock);
                    switch (addrSlot.Type)
                    {
                        case BorrowTypeRef borrowTypeRef:
                            if (!borrowTypeRef.MutableRef)
                            {
                                throw new Exception("Cannot store into non-mutable pointer");
                            }

                            break;
                        case PointerTypeRef pointerTypeRef:
                            if (!pointerTypeRef.MutableRef)
                            {
                                throw new Exception("Cannot store into non-mutable pointer");
                            }

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

                    CurrentBlock.AddInstruction(new StoreIndirectInst
                    {
                        Id = ++LastInstId,
                        TargetSlot = addrSlot.Id,
                        ValueSlot = exp.Id
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

                    var structType = tgt.Type.GetConcreteBaseType();
                    var structDef = Lookup<Struct>(structType.Name);
                    var structContext = new GenericContext(null, structDef.GenericParams, structType.GenericParams);
                    var fieldDef = structDef.Fields.Single(x => x.Name == fieldName);

                    if (!exp.Type.Equals(fieldDef.Type))
                    {
                        throw new Exception($"Cannot assign {exp.Type} to {fieldDef.Type}");
                    }

                    var fieldTgt = new FieldUnrealisedAccess(
                        tgt,
                        fieldDef.Name,
                        structContext.ResolveRef(fieldDef.Type),
                        fieldDef.Mutable
                    );
                    var fieldSlot = fieldTgt.GenerateRef(this, CurrentBlock, true);

                    CurrentBlock.AddInstruction(new StoreIndirectInst
                    {
                        Id = ++LastInstId,
                        TargetSlot = fieldSlot.Id,
                        ValueSlot = exp.Id
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

                    CurrentBlock.AddInstruction(new MoveInst
                    {
                        Id = ++LastInstId,
                        DestSlot = varDec.Id,
                        SrcSlot = exp.Id
                    });
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
                    if (child.UNSAFE() != null)
                    {
                        throw new NotImplementedException("Unsafe blocks");
                    }

                    var originalScope = CurrentScope;
                    var originalBlock = CurrentBlock;

                    PushScope();
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
                        throw new NotImplementedException("TODO");
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
            switch (ctx)
            {
                case OxideParser.Simple_if_expressionContext child:
                {
                    var condSlot = ParseExpression(child.expression()).GenerateMove(this, CurrentBlock);
                    if (!Equals(condSlot.Type, PrimitiveType.BoolRef))
                    {
                        throw new Exception("Non-bool value");
                    }

                    var originalScope = CurrentScope;
                    var originalBlock = CurrentBlock;

                    // Configure jump paths
                    var returnBlock = NewBlock(originalScope);
                    PushScope();
                    var trueBlock = NewBlock(CurrentScope);
                    RestoreScope(originalScope);

                    var hasElse = child.else_block != null || child.else_if != null;
                    var falseBlock = hasElse ? NewBlock(CurrentScope) : null;
                    originalBlock.AddInstruction(new JumpInst
                    {
                        Id = ++LastInstId,
                        ConditionSlot = condSlot.Id,
                        TargetBlock = trueBlock.Id,
                        ElseBlock = hasElse ? falseBlock.Id : returnBlock.Id
                    });

                    MakeCurrent(trueBlock);
                    var trueSlot = ParseBlock(child.body)?.GenerateMove(this, CurrentBlock);
                    var trueFinalBlock = CurrentBlock;
                    if (trueFinalBlock.HasTerminated)
                    {
                        throw new NotImplementedException("TODO");
                    }

                    SlotDeclaration falseSlot = null;
                    Block falseFinalBlock = null;
                    if (hasElse)
                    {
                        MakeCurrent(falseBlock);
                        falseSlot = (child.else_block != null
                            ? ParseBlock(child.else_block)
                            : ParseIfExpression(child.else_if))?.GenerateMove(this, CurrentBlock);
                        falseFinalBlock = CurrentBlock;
                        if (falseFinalBlock.HasTerminated)
                        {
                            throw new NotImplementedException("TODO");
                        }
                    }

                    MakeCurrent(returnBlock);

                    if (hasElse && trueSlot != null && falseSlot != null)
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

                        return new SlotUnrealisedAccess(resultDec);
                    }

                    trueFinalBlock.AddInstruction(new JumpInst
                    {
                        Id = ++LastInstId,
                        TargetBlock = returnBlock.Id
                    });
                    if (hasElse)
                    {
                        falseFinalBlock.AddInstruction(new JumpInst
                        {
                            Id = ++LastInstId,
                            TargetBlock = returnBlock.Id
                        });
                    }

                    return null;
                }
                case OxideParser.Let_if_expressionContext:
                    throw new NotImplementedException("If let expressions");
                default:
                    throw new ArgumentOutOfRangeException(nameof(ctx));
            }
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
                Type = PrimitiveType.BoolRef
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
                    throw new NotImplementedException("cast expression");
                    break;
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
                case OxideParser.Box_unary_expressionContext boxUnaryExpressionContext:
                    throw new NotImplementedException("Box expression");
                    break;
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

            if (!IsCopyType(innerTypeRef))
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
                    throw new NotImplementedException("Method call expression");
                    break;
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
                    throw new NotImplementedException("This expressions");
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(ctx));
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
                    throw new NotImplementedException("This field accesses");
                default:
                    throw new ArgumentOutOfRangeException(nameof(baseType));
            }

            var structDef = Lookup<Struct>(structType.Name);
            if (structDef == null)
            {
                throw new Exception($"Failed to find {structType.Name}");
            }

            var structContext = new GenericContext(null, structDef.GenericParams, structType.GenericParams);

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
            var structContext = new GenericContext(null, structDef.GenericParams, concreteTypeRef.GenericParams);

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
            if (ctx.qn_generics != null)
            {
                throw new NotImplementedException("Generic params on QN in function call expressions not implemented");
            }

            var qns = ctx.qualified_name();
            if (qns.Length != 1)
            {
                throw new NotImplementedException("Generic param derived function calls not implemented");
            }

            if (ctx.method_generics != null)
            {
                throw new NotImplementedException("Generic method params in function call expressions not implemented");
            }

            var qn1 = qns[0].Parse();
            var resolvedName = ResolveQN(qn1);
            var functionDef = Lookup<Function>(resolvedName);
            if (functionDef == null)
            {
                throw new Exception($"Unable to find unit for {resolvedName}");
            }

            var argumentCtxs = ctx.arguments() != null
                ? ctx.arguments().argument()
                : Array.Empty<OxideParser.ArgumentContext>();

            if (argumentCtxs.Length != functionDef.Parameters.Count)
            {
                throw new Exception($"{functionDef.Name} takes {functionDef.Parameters.Count} parameters");
            }

            var argIds = new List<int>();
            for (var i = 0; i < argumentCtxs.Length; i++)
            {
                var argument = argumentCtxs[i];
                var parameter = functionDef.Parameters[i];

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

                if (!expSlot.Type.Equals(parameter.Type))
                {
                    throw new Exception($"Parameter type mismatch {exp.Type} != {parameter.Type}");
                }

                argIds.Add(expSlot.Id);
            }

            if (functionDef.ReturnType != null)
            {
                var resultDec = CurrentScope.DefineSlot(new SlotDeclaration
                {
                    Id = ++LastSlotId,
                    Name = null,
                    Type = functionDef.ReturnType,
                    Mutable = false
                });

                CurrentBlock.AddInstruction(new StaticCallInst
                {
                    Id = ++LastInstId,
                    TargetMethod = functionDef.Name,
                    Arguments = argIds.ToImmutableList(),
                    ResultSlot = resultDec.Id
                });

                return new SlotUnrealisedAccess(resultDec);
            }

            CurrentBlock.AddInstruction(new StaticCallInst
            {
                Id = ++LastInstId,
                TargetMethod = functionDef.Name,
                Arguments = argIds.ToImmutableList(),
            });
            return null;
        }

        private UnrealisedAccess ParseQnExpression(OxideParser.Qualified_base_expressionContext ctx)
        {
            return new SlotUnrealisedAccess(ResolveQn(ctx.qualified_name(), ctx.type_generic_params()));
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
                    slotType = PrimitiveType.I32Ref;
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
                            slotType = PrimitiveType.BoolRef;
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
                            slotType = PrimitiveType.BoolRef;
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

        private Scope PushScope()
        {
            var scope = new Scope
            {
                Id = ++_lastScopeId,
                ParentScope = CurrentScope
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

        private void MakeCurrent(Block block)
        {
            RestoreScope(block.Scope);
            _currentBlockId = block.Id;
        }

        private TypeRef ParseType(OxideParser.TypeContext ctx)
        {
            return _fileParser.ParseType(ctx, _function.GenericParams);
        }

        private QualifiedName ResolveQN(QualifiedName qn)
        {
            return _fileParser.ResolveQN(qn);
        }

        public OxObj Lookup(QualifiedName qn)
        {
            return _unit.Lookup(qn) ?? _store.Lookup(qn);
        }

        public T Lookup<T>(QualifiedName qn) where T : OxObj
        {
            return _unit.Lookup<T>(qn) ?? _store.Lookup<T>(qn);
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
                    var baseType = Lookup(concreteTypeRef.Name);

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

        private BaseTypeRef ParseDirectType(OxideParser.Direct_typeContext ctx)
        {
            return _fileParser.ParseDirectType(ctx, _function.GenericParams);
        }
    }
}