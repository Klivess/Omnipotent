using System.Globalization;
using System.Text;

namespace Omnipotent.Services.OmniBlox.Build
{
    public sealed record LuauIssue(int Line, int Column, string Message, bool IsError)
    {
        public override string ToString() => $"{Line}:{Column}: {(IsError ? "error" : "warning")}: {Message}";
    }

    /// <summary>
    /// A Luau syntax checker: a tokenizer and a recursive-descent parser for the whole language Roblox
    /// scripts use (Lua 5.1 plus compound assignment, continue, if-expressions, string interpolation, type
    /// annotations, generics, type aliases and attributes). It builds no tree — it only answers "does this
    /// parse, and if not, where and why", fast and offline, so the build loop catches the mistakes language
    /// models make (a missing <c>end</c>, a stray <c>then</c>) before anything is uploaded. A few lints ride
    /// along as warnings (deprecated <c>wait</c>/<c>spawn</c>/<c>delay</c>, the slow parent argument of Instance.new).
    /// </summary>
    public static class LuauSyntax
    {
        public static List<LuauIssue> Check(string source)
        {
            var issues = new List<LuauIssue>();
            List<Token> tokens;
            try
            {
                tokens = new Lexer(source).Run();
            }
            catch (LuauSyntaxException ex)
            {
                issues.Add(new LuauIssue(ex.Line, ex.Column, ex.Message, true));
                return issues;
            }
            try
            {
                new Parser(tokens, issues).Chunk();
            }
            catch (LuauSyntaxException ex)
            {
                issues.Insert(0, new LuauIssue(ex.Line, ex.Column, ex.Message, true));
            }
            return issues;
        }

        public static bool IsValid(string source) => !Check(source).Any(i => i.IsError);

        // ─────────────────────────────── tokens ───────────────────────────────

        private enum T
        {
            Eof, Name, Number, String, Interp, Keyword, Op,
        }

        private sealed record Token(T Kind, string Text, int Line, int Column, List<(string Expr, int Line, int Column)>? Parts = null);

        private sealed class LuauSyntaxException : Exception
        {
            public LuauSyntaxException(int line, int column, string message) : base(message)
            {
                Line = line;
                Column = column;
            }
            public int Line { get; }
            public int Column { get; }
        }

        private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
        {
            "and", "break", "do", "else", "elseif", "end", "false", "for", "function", "if", "in", "local", "nil",
            "not", "or", "repeat", "return", "then", "true", "until", "while",
        };

        private static readonly string[] Operators =
        {
            "...", "//=", "..=", "::", "->", "==", "~=", "<=", ">=", "+=", "-=", "*=", "/=", "%=", "^=", "//", "..",
            "+", "-", "*", "/", "%", "^", "#", "<", ">", "=", "(", ")", "{", "}", "[", "]", ";", ":", ",", ".", "?", "&", "|", "@",
        };

        private sealed class Lexer
        {
            private readonly string s;
            private readonly int baseLine, baseColumn;
            private int pos, line, col;

            public Lexer(string source, int startLine = 1, int startColumn = 1)
            {
                s = source;
                baseLine = startLine;
                baseColumn = startColumn;
                line = startLine;
                col = startColumn;
            }

            private char Peek(int k = 0) => pos + k < s.Length ? s[pos + k] : '\0';

            private void Advance(int n = 1)
            {
                for (int i = 0; i < n && pos < s.Length; i++)
                {
                    if (s[pos] == '\n') { line++; col = 1; }
                    else col++;
                    pos++;
                }
            }

            private LuauSyntaxException Error(string message) => new(line, col, message);

            public List<Token> Run()
            {
                var tokens = new List<Token>();
                while (true)
                {
                    SkipTrivia();
                    if (pos >= s.Length)
                    {
                        tokens.Add(new Token(T.Eof, "<eof>", line, col));
                        return tokens;
                    }
                    int l = line, c = col;
                    char ch = Peek();
                    if (char.IsLetter(ch) || ch == '_')
                    {
                        int start = pos;
                        while (char.IsLetterOrDigit(Peek()) || Peek() == '_') Advance();
                        string word = s[start..pos];
                        tokens.Add(new Token(Keywords.Contains(word) ? T.Keyword : T.Name, word, l, c));
                        continue;
                    }
                    if (char.IsDigit(ch) || ch == '.' && char.IsDigit(Peek(1)))
                    {
                        tokens.Add(new Token(T.Number, ReadNumber(), l, c));
                        continue;
                    }
                    if (ch is '"' or '\'')
                    {
                        tokens.Add(new Token(T.String, ReadQuoted(ch), l, c));
                        continue;
                    }
                    if (ch == '[' && (Peek(1) == '[' || Peek(1) == '='))
                    {
                        int level = LongBracketLevel();
                        if (level >= 0)
                        {
                            tokens.Add(new Token(T.String, ReadLongBracket(level), l, c));
                            continue;
                        }
                    }
                    if (ch == '`')
                    {
                        tokens.Add(ReadInterpolated(l, c));
                        continue;
                    }
                    string? op = Operators.FirstOrDefault(o => string.CompareOrdinal(s, pos, o, 0, o.Length) == 0);
                    if (op == null) throw Error($"unexpected character '{ch}'");
                    Advance(op.Length);
                    tokens.Add(new Token(T.Op, op, l, c));
                }
            }

            private void SkipTrivia()
            {
                while (pos < s.Length)
                {
                    char ch = Peek();
                    if (char.IsWhiteSpace(ch)) { Advance(); continue; }
                    if (ch == '-' && Peek(1) == '-')
                    {
                        Advance(2);
                        if (Peek() == '[')
                        {
                            int level = LongBracketLevel();
                            if (level >= 0) { ReadLongBracket(level); continue; }
                        }
                        while (pos < s.Length && Peek() != '\n') Advance();
                        continue;
                    }
                    if (ch == '#' && Peek(1) == '!' && pos == 0) // shebang
                    {
                        while (pos < s.Length && Peek() != '\n') Advance();
                        continue;
                    }
                    break;
                }
            }

            /// <summary>At '[': returns the '=' count of a long bracket opener, or −1 if this is not one.</summary>
            private int LongBracketLevel()
            {
                int k = 1, level = 0;
                while (Peek(k) == '=') { level++; k++; }
                return Peek(k) == '[' ? level : -1;
            }

            private string ReadLongBracket(int level)
            {
                int l = line, c = col;
                Advance(level + 2);
                string close = "]" + new string('=', level) + "]";
                int start = pos;
                while (pos < s.Length)
                {
                    if (string.CompareOrdinal(s, pos, close, 0, close.Length) == 0)
                    {
                        string text = s[start..pos];
                        Advance(close.Length);
                        return text;
                    }
                    Advance();
                }
                throw new LuauSyntaxException(l, c, "unfinished long string or comment (missing " + close + ")");
            }

            private string ReadNumber()
            {
                int start = pos;
                if (Peek() == '0' && (Peek(1) is 'x' or 'X'))
                {
                    Advance(2);
                    while (Uri.IsHexDigit(Peek()) || Peek() == '_') Advance();
                }
                else if (Peek() == '0' && (Peek(1) is 'b' or 'B'))
                {
                    Advance(2);
                    while (Peek() is '0' or '1' or '_') Advance();
                }
                else
                {
                    while (char.IsDigit(Peek()) || Peek() == '_') Advance();
                    if (Peek() == '.' && Peek(1) != '.')
                    {
                        Advance();
                        while (char.IsDigit(Peek()) || Peek() == '_') Advance();
                    }
                    if (Peek() is 'e' or 'E')
                    {
                        Advance();
                        if (Peek() is '+' or '-') Advance();
                        if (!char.IsDigit(Peek())) throw Error("malformed number");
                        while (char.IsDigit(Peek())) Advance();
                    }
                }
                if (char.IsLetter(Peek()) || Peek() == '_') throw Error("malformed number");
                return s[start..pos];
            }

            private string ReadQuoted(char quote)
            {
                int l = line, c = col;
                Advance();
                var sb = new StringBuilder();
                while (true)
                {
                    if (pos >= s.Length || Peek() == '\n') throw new LuauSyntaxException(l, c, "unfinished string");
                    char ch = Peek();
                    if (ch == quote) { Advance(); return sb.ToString(); }
                    if (ch == '\\') { ReadEscape(); continue; }
                    sb.Append(ch);
                    Advance();
                }
            }

            private void ReadEscape()
            {
                Advance(); // backslash
                char e = Peek();
                if (e == 'z')
                {
                    Advance();
                    while (char.IsWhiteSpace(Peek())) Advance();
                    return;
                }
                if (e == 'x')
                {
                    Advance();
                    for (int i = 0; i < 2; i++)
                    {
                        if (!Uri.IsHexDigit(Peek())) throw Error("invalid \\x escape (needs two hex digits)");
                        Advance();
                    }
                    return;
                }
                if (e == 'u')
                {
                    Advance();
                    if (Peek() != '{') throw Error("invalid \\u escape (expected '{')");
                    Advance();
                    while (Uri.IsHexDigit(Peek())) Advance();
                    if (Peek() != '}') throw Error("invalid \\u escape (expected '}')");
                    Advance();
                    return;
                }
                if (char.IsDigit(e))
                {
                    for (int i = 0; i < 3 && char.IsDigit(Peek()); i++) Advance();
                    return;
                }
                // \{ and \} keep braces literal in an interpolated string (Roblox's own code writes `\{...\}`).
                if ("abfnrtv\\\"'`{}\n\r".IndexOf(e) >= 0) { Advance(); return; }
                throw Error($"invalid escape sequence '\\{e}'");
            }

            /// <summary>`text {expr} text`: each embedded expression is kept as source and parsed separately.</summary>
            private Token ReadInterpolated(int l, int c)
            {
                Advance(); // `
                var parts = new List<(string, int, int)>();
                while (true)
                {
                    if (pos >= s.Length) throw new LuauSyntaxException(l, c, "unfinished interpolated string");
                    char ch = Peek();
                    if (ch == '`') { Advance(); break; }
                    if (ch == '\\') { ReadEscape(); continue; }
                    if (ch == '{')
                    {
                        if (Peek(1) == '{') throw Error("double braces are not allowed in an interpolated string; escape one as \\{");
                        Advance();
                        int el = line, ec = col, start = pos, depth = 0;
                        while (true)
                        {
                            if (pos >= s.Length) throw new LuauSyntaxException(el, ec, "unfinished interpolated expression (missing '}')");
                            char d = Peek();
                            if (d is '"' or '\'') { ReadQuoted(d); continue; }
                            if (d == '`') { ReadInterpolated(line, col); continue; }
                            if (d == '{') depth++;
                            else if (d == '}')
                            {
                                if (depth == 0) break;
                                depth--;
                            }
                            Advance();
                        }
                        string expr = s[start..pos];
                        if (string.IsNullOrWhiteSpace(expr)) throw new LuauSyntaxException(el, ec, "empty expression in interpolated string");
                        parts.Add((expr, el, ec));
                        Advance(); // }
                        continue;
                    }
                    Advance();
                }
                return new Token(T.Interp, "`…`", l, c, parts);
            }
        }

        // ─────────────────────────────── parser ───────────────────────────────

        private sealed class Parser
        {
            private readonly List<Token> t;
            private readonly List<LuauIssue> issues;
            private int i;
            private int loopDepth;

            public Parser(List<Token> tokens, List<LuauIssue> issues)
            {
                t = tokens;
                this.issues = issues;
            }

            private Token Cur => t[i];
            private Token Next(int k = 1) => t[Math.Min(i + k, t.Count - 1)];
            private bool Is(string text) => (Cur.Kind is T.Keyword or T.Op) && Cur.Text == text;
            private bool IsName => Cur.Kind == T.Name;

            private Token Take() => t[i++ < t.Count - 1 ? i - 1 : t.Count - 1];

            private LuauSyntaxException Err(string message, Token? at = null)
            {
                var tok = at ?? Cur;
                return new LuauSyntaxException(tok.Line, tok.Column, message);
            }

            private static string Describe(Token tok) => tok.Kind switch
            {
                T.Eof => "<eof>",
                T.String => "a string",
                T.Number => $"'{tok.Text}'",
                T.Interp => "an interpolated string",
                _ => $"'{tok.Text}'",
            };

            private void Expect(string text, string? context = null)
            {
                if (Is(text)) { i++; return; }
                throw Err($"expected '{text}'{(context == null ? "" : " " + context)}, got {Describe(Cur)}");
            }

            /// <summary>Closes a block opened at <paramref name="opener"/> with 'end', naming the opener on failure.</summary>
            private void ExpectEnd(Token opener)
            {
                if (Is("end")) { i++; return; }
                throw Err($"expected 'end' (to close '{opener.Text}' at line {opener.Line}), got {Describe(Cur)}");
            }

            private string ExpectName(string what = "identifier")
            {
                // Contextual keywords are ordinary names in Luau.
                if (IsName) return Take().Text;
                throw Err($"expected {what}, got {Describe(Cur)}");
            }

            public void Chunk()
            {
                Block();
                if (Cur.Kind == T.Keyword && BlockEnd.Contains(Cur.Text))
                    throw Err($"'{Cur.Text}' has no matching opener (an extra '{Cur.Text}'?)");
                if (Cur.Kind != T.Eof) throw Err($"expected <eof>, got {Describe(Cur)}");
            }

            private static readonly HashSet<string> BlockEnd = new(StringComparer.Ordinal) { "end", "else", "elseif", "until" };

            private void Block()
            {
                while (true)
                {
                    if (Cur.Kind == T.Eof || Cur.Kind == T.Keyword && BlockEnd.Contains(Cur.Text)) return;
                    if (Is("return"))
                    {
                        i++;
                        if (!(Cur.Kind == T.Eof || Is(";") || Cur.Kind == T.Keyword && BlockEnd.Contains(Cur.Text))) ExprList();
                        if (Is(";")) i++;
                        if (!(Cur.Kind == T.Eof || Cur.Kind == T.Keyword && BlockEnd.Contains(Cur.Text)))
                            throw Err($"'return' must be the last statement in its block; got {Describe(Cur)} after it");
                        return;
                    }
                    bool last = Statement();
                    if (Is(";")) i++;
                    if (last && !(Cur.Kind == T.Eof || Cur.Kind == T.Keyword && BlockEnd.Contains(Cur.Text)))
                        throw Err($"'break'/'continue' must be the last statement in its block; got {Describe(Cur)} after it");
                }
            }

            /// <summary>Parses one statement. Returns true for break/continue (which must end a block).</summary>
            private static readonly HashSet<string> IdentifierFollowers = new(StringComparer.Ordinal)
            {
                "(", ".", "[", ":", "=", ",", "{", "+=", "-=", "*=", "/=", "//=", "%=", "^=", "..=",
            };

            private bool Statement()
            {
                if (Is(";")) { i++; return false; }
                if (Is("@")) { Attributes(); if (!Is("function") && !Is("local")) throw Err("expected 'function' after an attribute"); }
                var tok = Cur;
                if (tok.Kind == T.Keyword)
                {
                    switch (tok.Text)
                    {
                        case "if": IfStatement(); return false;
                        case "while":
                            i++;
                            Expr();
                            Expect("do", "after the 'while' condition");
                            loopDepth++; Block(); loopDepth--;
                            ExpectEnd(tok);
                            return false;
                        case "do":
                            i++;
                            Block();
                            ExpectEnd(tok);
                            return false;
                        case "for": ForStatement(); return false;
                        case "repeat":
                            i++;
                            loopDepth++; Block(); loopDepth--;
                            if (!Is("until")) throw Err($"expected 'until' (to close 'repeat' at line {tok.Line}), got {Describe(Cur)}");
                            i++;
                            Expr();
                            return false;
                        case "function":
                            i++;
                            ExpectName("function name");
                            while (Is(".")) { i++; ExpectName(); }
                            if (Is(":")) { i++; ExpectName("method name"); }
                            FuncBody(tok);
                            return false;
                        case "local":
                            i++;
                            if (Is("@")) Attributes();
                            if (Is("function"))
                            {
                                var fn = Take();
                                ExpectName("function name");
                                FuncBody(fn);
                                return false;
                            }
                            do
                            {
                                ExpectName("variable name");
                                if (Is("<")) { i++; ExpectName("attribute"); Expect(">"); }
                                if (Is(":")) { i++; Type(); }
                            } while (Accept(","));
                            if (Accept("=")) ExprList();
                            return false;
                        case "break":
                            if (loopDepth == 0) throw Err("'break' outside a loop");
                            i++;
                            return true;
                        case "return":
                            throw Err("unexpected 'return'");
                        default:
                            throw Err($"unexpected {Describe(tok)}" + (tok.Text is "end" or "else" or "elseif" or "until" ? " (it has no matching opener)" : ""));
                    }
                }

                // Contextual keywords.
                // 'continue' is a statement unless what follows shows it being used as a variable.
                if (IsName && tok.Text == "continue"
                    && !(Next().Kind == T.Op && IdentifierFollowers.Contains(Next().Text) || Next().Kind == T.String))
                {
                    if (loopDepth == 0) throw Err("'continue' outside a loop");
                    i++;
                    return true;
                }
                if (IsName && tok.Text == "export" && Next().Kind == T.Name && Next().Text == "type")
                {
                    i += 2;
                    TypeAlias();
                    return false;
                }
                if (IsName && tok.Text == "type" && Next().Kind == T.Name && (Next(2).Kind == T.Op && Next(2).Text is "=" or "<"))
                {
                    i++;
                    TypeAlias();
                    return false;
                }
                if (IsName && tok.Text == "type" && Next().Kind == T.Keyword && Next().Text == "function")
                {
                    // type function Name(...) ... end
                    i++;
                    var fn = Take();
                    ExpectName("type function name");
                    FuncBody(fn);
                    return false;
                }

                // Expression statement: a call or an assignment.
                var start = Cur;
                bool isCall = SuffixedExpr(out bool assignable);
                if (Is("=") || Is(","))
                {
                    if (!assignable) throw Err("cannot assign to this expression", start);
                    while (Accept(","))
                    {
                        SuffixedExpr(out bool ok);
                        if (!ok) throw Err("cannot assign to this expression");
                    }
                    Expect("=");
                    ExprList();
                    return false;
                }
                if (Cur.Kind == T.Op && Cur.Text is "+=" or "-=" or "*=" or "/=" or "//=" or "%=" or "^=" or "..=")
                {
                    if (!assignable) throw Err("cannot assign to this expression", start);
                    i++;
                    Expr();
                    return false;
                }
                if (!isCall)
                    throw Err($"incomplete statement: expected assignment or a function call" + (start.Kind == T.Name ? $" after '{start.Text}'" : ""), start);
                return false;
            }

            private void Attributes()
            {
                while (Is("@"))
                {
                    i++;
                    ExpectName("attribute name");
                }
            }

            private bool Accept(string text)
            {
                if (!Is(text)) return false;
                i++;
                return true;
            }

            private void IfStatement()
            {
                var opener = Take(); // if
                Expr();
                Expect("then", $"after the 'if' condition (line {opener.Line})");
                Block();
                while (Is("elseif"))
                {
                    i++;
                    Expr();
                    Expect("then", "after the 'elseif' condition");
                    Block();
                }
                if (Accept("else")) Block();
                ExpectEnd(opener);
            }

            private void ForStatement()
            {
                var opener = Take(); // for
                ExpectName("loop variable");
                if (Is(":")) { i++; Type(); }
                if (Accept("="))
                {
                    Expr();
                    Expect(",", "in the numeric 'for'");
                    Expr();
                    if (Accept(",")) Expr();
                }
                else
                {
                    while (Accept(","))
                    {
                        ExpectName("loop variable");
                        if (Is(":")) { i++; Type(); }
                    }
                    Expect("in", "in the 'for' loop");
                    ExprList();
                }
                Expect("do", "in the 'for' loop");
                loopDepth++; Block(); loopDepth--;
                ExpectEnd(opener);
            }

            private void FuncBody(Token opener)
            {
                if (Is("<")) GenericList();
                Expect("(", "to start the parameter list");
                if (!Is(")"))
                {
                    do
                    {
                        if (Accept("..."))
                        {
                            if (Is(":")) { i++; TypePackElement(); } // ...: number, ...: T...
                            break;
                        }
                        ExpectName("parameter name");
                        if (Is(":")) { i++; Type(); }
                    } while (Accept(","));
                }
                Expect(")", "to close the parameter list");
                if (Accept(":")) ReturnType();
                int savedLoop = loopDepth;
                loopDepth = 0;
                Block();
                loopDepth = savedLoop;
                ExpectEnd(opener);
            }

            private void ExprList()
            {
                Expr();
                while (Accept(",")) Expr();
            }

            private static int LeftPriority(string op) => op switch
            {
                "or" => 1,
                "and" => 2,
                "<" or ">" or "<=" or ">=" or "~=" or "==" => 3,
                ".." => 5,
                "+" or "-" => 6,
                "*" or "/" or "//" or "%" => 7,
                "^" => 10,
                _ => -1,
            };

            private static int RightPriority(string op) => op switch
            {
                ".." => 4, // right associative
                "^" => 9,  // right associative
                _ => LeftPriority(op),
            };

            private const int UnaryPriority = 8;

            private void Expr(int limit = 0)
            {
                if (Is("not") || Is("-") || Is("#"))
                {
                    i++;
                    Expr(UnaryPriority);
                }
                else
                {
                    SimpleExpr();
                }
                while (true)
                {
                    if (Is("::"))
                    {
                        i++;
                        Type();
                        continue;
                    }
                    string op = (Cur.Kind is T.Op or T.Keyword) ? Cur.Text : "";
                    int left = LeftPriority(op);
                    if (left <= limit) break;
                    i++;
                    Expr(RightPriority(op));
                }
            }

            private void SimpleExpr()
            {
                var tok = Cur;
                switch (tok.Kind)
                {
                    case T.Number:
                    case T.String:
                        i++;
                        return;
                    case T.Interp:
                        i++;
                        foreach (var (expr, line, column) in tok.Parts!) SubExpression(expr, line, column);
                        return;
                    case T.Keyword when tok.Text is "nil" or "true" or "false":
                        i++;
                        return;
                    case T.Keyword when tok.Text == "function":
                        i++;
                        FuncBody(tok);
                        return;
                    case T.Keyword when tok.Text == "if":
                        IfExpression();
                        return;
                    case T.Op when tok.Text == "...":
                        i++;
                        return;
                    case T.Op when tok.Text == "{":
                        TableConstructor();
                        return;
                    case T.Op when tok.Text == "@":
                        Attributes();
                        if (!Is("function")) throw Err("expected 'function' after an attribute");
                        var fn = Take();
                        FuncBody(fn);
                        return;
                }
                SuffixedExpr(out _);
            }

            private void SubExpression(string source, int line, int column)
            {
                var tokens = new Lexer(source, line, column).Run();
                var sub = new Parser(tokens, issues);
                sub.Expr();
                if (sub.Cur.Kind != T.Eof) throw sub.Err($"unexpected {Describe(sub.Cur)} in interpolated expression");
            }

            private void IfExpression()
            {
                var opener = Take(); // if
                Expr();
                Expect("then", "in the if-expression");
                Expr();
                while (Accept("elseif"))
                {
                    Expr();
                    Expect("then", "in the if-expression");
                    Expr();
                }
                if (!Is("else")) throw Err($"an if-expression (line {opener.Line}) needs an 'else' branch, got {Describe(Cur)}");
                i++;
                Expr();
            }

            /// <summary>primary { suffix }. Returns whether it ended in a call; <paramref name="assignable"/> whether it can be assigned to.</summary>
            private bool SuffixedExpr(out bool assignable)
            {
                var tok = Cur;
                assignable = false;
                bool isCall = false;
                if (tok.Kind == T.Name)
                {
                    i++;
                    assignable = true;
                }
                else if (Is("("))
                {
                    i++;
                    Expr();
                    Expect(")", $"to close '(' at line {tok.Line}");
                }
                else
                {
                    throw Err($"unexpected {Describe(tok)}" + (tok.Kind == T.Keyword && tok.Text is "then" or "do" or "end" ? " — check the line above for a missing condition or an extra word" : ""));
                }

                while (true)
                {
                    if (Is("."))
                    {
                        i++;
                        if (!IsName && Cur.Kind != T.Keyword) throw Err($"expected a field name after '.', got {Describe(Cur)}");
                        i++;
                        assignable = true;
                        isCall = false;
                    }
                    else if (Is("["))
                    {
                        var open = Take();
                        Expr();
                        Expect("]", $"to close '[' at line {open.Line}");
                        assignable = true;
                        isCall = false;
                    }
                    else if (Is(":"))
                    {
                        i++;
                        if (!IsName && Cur.Kind != T.Keyword) throw Err($"expected a method name after ':', got {Describe(Cur)}");
                        string method = Take().Text;
                        if (IsInstantiation) TypeInstantiation();
                        if (!(Is("(") || Is("{") || Cur.Kind == T.String))
                            throw Err($"expected '(' to call method '{method}', got {Describe(Cur)}");
                        CallArgs();
                        assignable = false;
                        isCall = true;
                    }
                    else if (IsInstantiation)
                    {
                        // Explicit type instantiation: React.useRef<<Frame?>>(nil). Never a comparison: '<' cannot start an expression.
                        TypeInstantiation();
                        assignable = false;
                        isCall = false;
                    }
                    else if (Is("(") || Is("{") || Cur.Kind == T.String)
                    {
                        // Luau refuses a '(' on a new line after a complete expression: it could be a call or a new statement.
                        if (Is("(") && i > 0 && t[i - 1].Line < Cur.Line)
                            throw Err("ambiguous syntax: this '(' looks like a call of the previous line's expression but could start a new statement; put ';' before it or join the lines");
                        CallArgs();
                        assignable = false;
                        isCall = true;
                    }
                    else break;
                }
                return isCall;
            }

            private void CallArgs()
            {
                if (Cur.Kind == T.String) { i++; return; }
                if (Is("{")) { TableConstructor(); return; }
                var open = Take(); // (
                if (!Is(")")) ExprList();
                Expect(")", $"to close the call opened at line {open.Line}");
            }

            private void TableConstructor()
            {
                var open = Take(); // {
                while (!Is("}"))
                {
                    if (Is("["))
                    {
                        i++;
                        Expr();
                        Expect("]");
                        Expect("=", "in the table field");
                        Expr();
                    }
                    else if (IsName && Next().Kind == T.Op && Next().Text == "=")
                    {
                        i += 2;
                        Expr();
                    }
                    else
                    {
                        Expr();
                    }
                    if (!Accept(",") && !Accept(";")) break;
                }
                if (!Is("}")) throw Err($"expected '}}' (to close '{{' at line {open.Line}), got {Describe(Cur)} — is a ',' missing between table fields?");
                i++;
            }

            // ─────────────── types ───────────────

            private void TypeAlias()
            {
                ExpectName("type name");
                if (Is("<")) GenericList(allowDefaults: true);
                Expect("=", "in the type alias");
                Type();
            }

            private void GenericList(bool allowDefaults = false)
            {
                Expect("<");
                do
                {
                    ExpectName("generic name");
                    Accept("...");
                    if (allowDefaults && Accept("=")) { if (Is("(")) TypePackOrType(); else Type(); }
                } while (Accept(","));
                Expect(">", "to close the generic list");
            }

            /// <summary>A return type: a type (a parenthesised one may be optional or part of a union: (() -> ())?), a pack, '...T' or 'T...'.</summary>
            private void ReturnType() => TypePackElement();

            /// <summary>One entry of a type list: a type, a variadic '...T', or a generic type pack 'T...'.</summary>
            private void TypePackElement()
            {
                if (Accept("...")) { Type(); return; }
                Type();
                Accept("...");
            }

            private bool IsInstantiation => Is("<") && Next().Kind == T.Op && Next().Text == "<";

            /// <summary>'&lt;&lt;' types '&gt;&gt;' after a function or method name.</summary>
            private void TypeInstantiation()
            {
                var open = Cur;
                i += 2;
                if (!Is(">"))
                    do { TypePackElement(); } while (Accept(","));
                Expect(">", $"to close the type instantiation at line {open.Line}");
                Expect(">", $"to close the type instantiation at line {open.Line}");
            }

            private void Type()
            {
                if (Is("|") || Is("&")) i++; // leading separator is allowed
                SimpleTypeWithSuffix();
                while (Is("|") || Is("&"))
                {
                    i++;
                    SimpleTypeWithSuffix();
                }
            }

            private void SimpleTypeWithSuffix()
            {
                SimpleType();
                while (Accept("?")) { }
            }

            private void SimpleType()
            {
                var tok = Cur;
                if (tok.Kind == T.Keyword && tok.Text is "nil" or "true" or "false") { i++; return; }
                if (tok.Kind == T.String) { i++; return; }
                if (Is("{")) { TableType(); return; }
                if (Is("(") || Is("<"))
                {
                    if (Is("<")) GenericList();
                    TypePackOrType(requireArrowAfterGenerics: tok.Text == "<");
                    return;
                }
                if (Is("...")) { i++; Type(); return; }
                if (tok.Kind == T.Name && tok.Text == "typeof" && Next().Kind == T.Op && Next().Text == "(")
                {
                    i += 2;
                    Expr();
                    Expect(")", "to close typeof(");
                    return;
                }
                if (tok.Kind == T.Name)
                {
                    i++;
                    if (Accept(".")) ExpectName("type name");
                    if (Is("<"))
                    {
                        i++;
                        if (!Is(">"))
                            do { TypePackElement(); } while (Accept(","));
                        Expect(">", "to close the type arguments");
                    }
                    return;
                }
                throw Err($"expected a type, got {Describe(tok)}");
            }

            /// <summary>'(' types ')' — a parenthesised type, a type pack, or the parameter list of a function type.</summary>
            private void TypePackOrType(bool requireArrowAfterGenerics = false)
            {
                var open = Take(); // (
                if (!Is(")"))
                {
                    do
                    {
                        // Named parameters in function types: (x: number) -> ()
                        if (IsName && Next().Kind == T.Op && Next().Text == ":") i += 2;
                        TypePackElement();
                    } while (Accept(","));
                }
                Expect(")", $"to close '(' at line {open.Line}");
                if (Accept("->")) ReturnType();
                else if (requireArrowAfterGenerics) throw Err("expected '->' after a generic function type's parameters");
            }

            private void TableType()
            {
                var open = Take(); // {
                if (Is("}")) { i++; return; }
                // Array shorthand {T}: a type not followed by ':' as the only entry.
                int save = i;
                if (!(Is("[") || IsName && Next().Kind == T.Op && Next().Text == ":" || IsName && Cur.Text is "read" or "write" && Next().Kind == T.Name))
                {
                    Type();
                    if (Accept("}")) return;
                    i = save;
                }
                while (!Is("}"))
                {
                    if (IsName && Cur.Text is "read" or "write" && (Next().Kind == T.Name || Next().Kind == T.Op && Next().Text == "[")) i++;
                    if (Is("["))
                    {
                        i++;
                        Type();
                        Expect("]");
                        Expect(":", "in the table type indexer");
                        Type();
                    }
                    else
                    {
                        if (!IsName && Cur.Kind != T.Keyword) throw Err($"expected a property name in the table type, got {Describe(Cur)}");
                        i++;
                        Expect(":", "after the table type property name");
                        Type();
                    }
                    if (!Accept(",") && !Accept(";")) break;
                }
                if (!Is("}")) throw Err($"expected '}}' (to close the table type at line {open.Line}), got {Describe(Cur)}");
                i++;
            }
        }

        // ─────────────────────────────── lints ───────────────────────────────

        private static readonly (string Pattern, string Message)[] Lints =
        {
            ("wait(", "'wait' is deprecated; use task.wait"),
            ("spawn(", "'spawn' is deprecated; use task.spawn"),
            ("delay(", "'delay' is deprecated; use task.delay"),
        };

        /// <summary>Cheap pattern lints on top of the parse (warnings only).</summary>
        public static List<LuauIssue> Lint(string source)
        {
            var issues = new List<LuauIssue>();
            var lines = source.Split('\n');
            for (int n = 0; n < lines.Length; n++)
            {
                string code = StripLineComment(lines[n]);
                foreach (var (pattern, message) in Lints)
                {
                    int idx = code.IndexOf(pattern, StringComparison.Ordinal);
                    while (idx >= 0)
                    {
                        bool standalone = idx == 0 || !(char.IsLetterOrDigit(code[idx - 1]) || code[idx - 1] is '_' or '.' or ':');
                        if (standalone)
                        {
                            issues.Add(new LuauIssue(n + 1, idx + 1, message, false));
                            break;
                        }
                        idx = code.IndexOf(pattern, idx + 1, StringComparison.Ordinal);
                    }
                }
                int inst = code.IndexOf("Instance.new(", StringComparison.Ordinal);
                if (inst >= 0)
                {
                    int close = code.IndexOf(')', inst);
                    string args = close > inst ? code[(inst + 13)..close] : "";
                    if (args.Contains(',')) issues.Add(new LuauIssue(n + 1, inst + 1, "Instance.new with a parent argument is slow; set .Parent last", false));
                }
            }
            return issues;
        }

        private static string StripLineComment(string line)
        {
            bool inString = false;
            char quote = '\0';
            for (int k = 0; k < line.Length - 1; k++)
            {
                char ch = line[k];
                if (inString)
                {
                    if (ch == '\\') { k++; continue; }
                    if (ch == quote) inString = false;
                    continue;
                }
                if (ch is '"' or '\'' or '`') { inString = true; quote = ch; continue; }
                if (ch == '-' && line[k + 1] == '-') return line[..k];
            }
            return line;
        }

        internal static string Format(IEnumerable<LuauIssue> issues, string file) =>
            string.Join("\n", issues.Select(x => $"{file}:{x.Line}:{x.Column}: {(x.IsError ? "error" : "warning")}: {x.Message}"));

        internal static int CountLines(string source) => source.Count(c => c == '\n') + 1;

        internal static string Number(double d) => d.ToString(CultureInfo.InvariantCulture);
    }
}
