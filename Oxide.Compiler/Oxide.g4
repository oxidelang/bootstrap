grammar Oxide;

WS:                 [ \t\r\n\u000C]+ -> channel(HIDDEN);
COMMENT:            '/*' .*? '*/'    -> channel(HIDDEN);
LINE_COMMENT:       '//' ~[\r\n]*    -> channel(HIDDEN);

PACKAGE:    'package';
IMPORT:     'import';
PUBLIC:     'public';
PRIVATE:    'private';
MUT:        'mut';
STRUCT:     'struct';
ENUM:       'enum';
IMPL:       'impl';
IFACE:      'interface';
WHERE:      'where';
FOR:        'for';
LET:        'let';
FN:         'fn';
UNSAFE:     'unsafe';

REF:        'ref';
WEAK:       'weak';
DERIVED:    '~';

//SELF_TYPE: 'Self';
SELF_FIELD: 'self';

RETURN: 'return';
IF:     'if';
ELSE:   'else';

LBRACK: '(';
RBRACK: ')';
LARROW: '<';
RARROW: '>';
LBRACE: '{';
RBRACE: '}';
COLON:  ':';
DCOLON: '::';
SEMI:   ';';
ARROW:  '->';
AMP:    '&';
PERIOD: '.';
COMMA:  ',';

PLUS:       '+';
MINUS:      '-';
EQUAL:      '=';
NOTEQ:      '!=';
EQUALTO:    '==';
STAR:       '*';
DIV:        '/';
MOD:        '%';
NOT:        '!';
AS:         'as';

OR_OP:      '||';
AND_OP:     '&&';
INC_OR_OP:  '|';
EX_OR_OP:   '^';
LEQ_OP:     '<=';
GEQ_OP:     '>=';

IDENTIFIER:     Letter LetterOrDigit*;
INT_NUMBER:     Digits;
HEX_NUMBER:     '0' [xX] HexDigit ((HexDigit | '_')* HexDigit)? [lL]?;
BINARY_NUMBER:  '0' [bB] BinaryDigit ((BinaryDigit | '_')* BinaryDigit)? [lL]?;
CHAR_LITERAL:   '\'' (~['\\\r\n] | EscapeSequence) '\'';
STRING_LITERAL: '"' (~["\\\r\n] | EscapeSequence)* '"';
TRUE:           'true';
FALSE:          'false';

fragment EscapeSequence
    : '\\' [btnfr"'\\]
    | '\\' 'u'+ HexDigit HexDigit HexDigit HexDigit
    ;

fragment HexDigits
    : HexDigit ((HexDigit | '_')* HexDigit)?
    ;
fragment HexDigit
    : [0-9a-fA-F]
    ;

fragment BinaryDigit
    : [01]
    ;

fragment Digits
    : Digit ((Digit | '_')* Digit)?
    ;

fragment Digit
    : [0-9]
    ;

fragment LetterOrDigit
    : Letter
    | [0-9]
    ;

fragment Letter
    : [a-zA-Z$_]
    ;

compilation_unit
    : package import_stmt* top_level* EOF
    ;

package
    : PACKAGE qualified_name SEMI
    ;

qualified_name
    : qualified_name_part #relative_qualified_name
    | DCOLON qualified_name_part #absolute_qualified_name
    ;

qualified_name_part
    : IDENTIFIER
    | qualified_name_part DCOLON IDENTIFIER 
    ;

top_level
    : struct_def #struct_top_level
    | enum_def #enum_top_level
    | impl_stmt #impl_top_level
    | fn_def #fn_top_level
    | iface_def #iface_top_level
    ;

import_stmt
    : IMPORT qualified_name (AS name)? SEMI
    ;

struct_def
    : visibility? STRUCT name generic_def? LBRACE field_def* RBRACE
    ;

generic_def
    : LARROW name (COMMA name)* RARROW
    ;

field_def
    : visibility? name COLON type COMMA
    ;

enum_def
    : visibility? ENUM name generic_def? LBRACE variant_def* RBRACE
    ;

variant_def
    : visibility? name (LBRACE field_def* RBRACE)? COMMA
    ;

impl_stmt
    : IMPL (qualified_name FOR)? type where? (SEMI | impl_body)
    ;

impl_body
    : LBRACE fn_def* RBRACE
    ;

where
    : WHERE where_clause (COMMA where_clause)*
    ;

where_clause
    : name COLON type (PLUS type)*
    ;

iface_def
    : visibility? IFACE name generic_def? LBRACE fn_def* RBRACE
    ;

fn_def
    : visibility? FN name LBRACK (parameter (COMMA parameter)*)? RBRACK (ARROW type)? fn_body
    ;

parameter
    : name COLON type
    | type_flags SELF_FIELD
    ;

fn_body
    : block
    | SEMI
    ;

block
    : LBRACE statements?  RBRACE
    ;

statements
   : statement+ expression?
   | expression
   ;

statement
    : SEMI
    | expression SEMI
    | block_expression
    | variable_statement
    ;

expression
    : or_expression
    | RETURN or_expression
    | base_expression EQUAL or_expression
    ;

or_expression
	: and_expression
	| or_expression OR_OP and_expression
	;

and_expression
	: inc_or_expression
	| and_expression AND_OP inc_or_expression 
	;

inc_or_expression
	: ex_or_expression
	| inc_or_expression INC_OR_OP ex_or_expression 
	;

ex_or_expression
	: bit_and_expression
	| ex_or_expression EX_OR_OP bit_and_expression 
	;

bit_and_expression
	: equal_expression
	| bit_and_expression AMP equal_expression 
	;

equal_expression
	: comparison_expression
	| equal_expression EQUALTO comparison_expression
	| equal_expression NOTEQ comparison_expression
	;

comparison_expression
	: cast_expression
	| comparison_expression LARROW cast_expression
	| comparison_expression RARROW cast_expression
	| comparison_expression LEQ_OP cast_expression
	| comparison_expression GEQ_OP cast_expression
	;

cast_expression
    : shift_expression
    | cast_expression AS type
    ;

shift_expression
	: add_expression
	| shift_expression left_shift add_expression
	| shift_expression right_shift add_expression
	;

add_expression
	: multiply_expression
	| add_expression PLUS multiply_expression 
	| add_expression MINUS multiply_expression
	;

multiply_expression
	: unary_expression
	| multiply_expression STAR unary_expression
	| multiply_expression DIV unary_expression
	| multiply_expression MOD unary_expression
	;

unary_expression
	: base_expression
	| MINUS unary_expression
	| NOT unary_expression
	| AMP unary_expression
	| LBRACK type RBRACK unary_expression
	;

base_expression
    : DERIVED? LBRACK expression RBRACK
    | literal
    | DERIVED? SELF_FIELD
    | DERIVED? qualified_name
    | struct_initialiser
    | block_expression
    | base_expression PERIOD name
    | base_expression LBRACK arguments? RBRACK
    ;

block_expression
    : UNSAFE? block
    | if_expression
    ;

if_expression
    : IF expression block (ELSE (block | if_expression))?
    ;

arguments
	: argument (COMMA argument)* COMMA?
	;

argument
	: label? expression
	;

label
    : name COLON
    ;

struct_initialiser
    : name type_generic_params LBRACE field_initialiser* RBRACE
    ;

field_initialiser
    : label expression COMMA
    | name COMMA
    ;

variable_statement
    : LET MUT? name (COLON type)? EQUAL expression SEMI
    ;

name
    : IDENTIFIER
    ;

type
    : type_flags qualified_name type_generic_params?
    ;

type_generic_params
    : LARROW type (COMMA type)* RARROW
    ;

type_flags
    : UNSAFE? MUT? (REF | DERIVED | WEAK)? 
    ;

visibility
    : PUBLIC #public_visibility
    | PRIVATE #private_visibility
    ;

literal
    : boolean_literal #outer_bool_literal
    | INT_NUMBER #int_literal
    | HEX_NUMBER #hex_literal
    | BINARY_NUMBER #binary_literal
    | STRING_LITERAL #string_literal
    ;

boolean_literal
	: TRUE
	| FALSE
	;

// Ensure we don't conflict with generics
left_shift
	: f=LARROW s=LARROW {$f.index + 1 == $s.index}?
	;

right_shift
	: f=RARROW s=RARROW {$f.index + 1 == $s.index}?
	;