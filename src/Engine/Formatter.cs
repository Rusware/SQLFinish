using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace SQLFinish.Engine
{
    /// <summary>
    /// Token-based T-SQL layout: upper-cases keywords, puts clauses on their own lines, stacks
    /// select/set lists and indents subqueries, blocks and CASE. It only ever changes whitespace
    /// and keyword case; <see cref="Format"/> verifies that before returning.
    /// </summary>
    public static class Formatter
    {
        // ponytail: fixed style (4 spaces, UPPER keywords, trailing commas). Add an options page when someone wants another style.
        const string IndentUnit = "    ";

        enum LevelKind { Top, Sub, Inline, Cols }

        sealed class Level
        {
            public LevelKind Kind;
            public int Indent;
            public string Clause = "";
            public bool StackList;   // commas in the current clause start a new line
            public bool Between;     // the next AND belongs to BETWEEN
            public bool DdlTable;    // CREATE TABLE seen: the next "(" opens a column list
            public bool Breaks => Kind == LevelKind.Top || Kind == LevelKind.Sub;
        }

        static readonly HashSet<string> JoinWords = new HashSet<string> { "INNER", "LEFT", "RIGHT", "FULL", "CROSS", "OUTER", "JOIN" };
        static readonly HashSet<string> JoinHints = new HashSet<string> { "INNER", "LEFT", "RIGHT", "FULL", "CROSS", "OUTER", "HASH", "LOOP", "MERGE", "REMOTE" };

        // Always start a line.
        static readonly HashSet<string> Strong = new HashSet<string>
        {
            "SELECT", "FROM", "WHERE", "HAVING", "UNION", "EXCEPT", "INTERSECT", "VALUES", "DECLARE", "IF", "WHILE", "PRINT",
            "RETURN", "GO", "USE", "TRUNCATE", "MERGE", "RAISERROR", "THROW", "COMMIT", "ROLLBACK", "EXEC", "EXECUTE",
            "INSERT", "UPDATE", "DELETE", "SET", "CREATE", "ALTER", "DROP",
        };

        // Start a line only where the author already had one: too ambiguous to decide from tokens alone.
        static readonly HashSet<string> Weak = new HashSet<string>
        {
            "WITH", "FETCH", "OPEN", "CLOSE", "DEALLOCATE", "BREAK", "CONTINUE", "GOTO", "WAITFOR", "GRANT", "DENY", "REVOKE",
            "OPTION", "OUTPUT", "USING", "AS", "FOR", "SAVE", "DBCC", "KILL", "BACKUP", "RESTORE", "REVERT", "CHECKPOINT",
        };

        // Where a select/set/declare list ends, for the single-item lookahead.
        static readonly HashSet<string> ListEnd = new HashSet<string>(Strong)
        {
            "INTO", "GROUP", "ORDER", "BEGIN", "END", "OPTION", "FOR", "ELSE", "WITH",
        };

        public static string Format(string sql)
        {
            var all = Tokenizer.Scan(sql);
            var toks = new List<Tok>();      // everything except whitespace
            var newlines = new List<int>();  // line breaks in the whitespace before each token
            int pendingNl = 0;
            foreach (var t in all)
            {
                if (t.Type == TokType.Ws) { pendingNl += t.Text.Count(c => c == '\n'); continue; }
                toks.Add(t);
                newlines.Add(pendingNl);
                pendingNl = 0;
            }
            if (toks.Count == 0) return sql;

            string eol = sql.Contains("\n") && !sql.Contains("\r\n") ? "\n" : "\r\n";
            var sb = new StringBuilder(sql.Length + 64);
            bool atLineStart = true, noSpaceNext = false, mustBreak = false; // mustBreak: a "--" comment ended the line
            int lineIndent = 0, pendingBreak = -1;
            var levels = new Stack<Level>();
            levels.Push(new Level { Kind = LevelKind.Top });
            var blocks = new Stack<KeyValuePair<bool, int>>(); // (isCase, indent)
            var forced = new Dictionary<int, int>();           // token index -> indent of a forced line break
            Tok prev = null;                                   // previous non-comment token

            Func<int, Tok> code = from =>
            {
                for (int k = from; k < toks.Count; k++) if (toks[k].Type != TokType.Comment) return toks[k];
                return null;
            };

            Action<int, int> newLine = (index, indent) =>
            {
                indent = Math.Max(0, indent);
                if (sb.Length > 0)
                {
                    while (sb.Length > 0 && sb[sb.Length - 1] == ' ') sb.Length--;
                    sb.Append(eol);
                    if (newlines[index] > 1) sb.Append(eol); // keep the author's paragraph breaks
                }
                for (int k = 0; k < indent; k++) sb.Append(IndentUnit);
                lineIndent = indent;
                atLineStart = true;
            };

            for (int i = 0; i < toks.Count; i++)
            {
                var t = toks[i];
                var L = levels.Peek();

                if (t.Type == TokType.Comment)
                {
                    if (i == 0 || newlines[i] > 0)
                    {
                        var after = code(i + 1);
                        bool startsStatement = after != null && L.Breaks && after.Type == TokType.Word && Strong.Contains(after.Upper);
                        newLine(i, pendingBreak >= 0 ? pendingBreak : startsStatement ? L.Indent : lineIndent);
                    }
                    else if (!atLineStart) sb.Append(' ');
                    sb.Append(t.Text.TrimEnd());
                    atLineStart = false;
                    noSpaceNext = false;
                    mustBreak = t.Text.StartsWith("--");
                    continue;
                }

                string up = t.Upper;
                bool afterDot = prev != null && (prev.IsP(".") || prev.IsP("::"));
                bool isKw = t.IsKeyword && !afterDot;
                string text = isKw ? up : t.Text;
                var next = code(i + 1);
                int brk = -1;
                bool space = true;

                if (t.Type == TokType.Punct)
                {
                    switch (t.Text)
                    {
                        case "(":
                            space = prev == null || !(prev.Type == TokType.Quoted
                                || (prev.Type == TokType.Word && (!prev.IsKeyword || Keywords.CallLike.Contains(prev.Text))));
                            if (L.Clause == "INSERT" || L.DdlTable) space = true;
                            if (prev != null && prev.IsP(")")) space = true;
                            break;
                        case ")":
                            space = false;
                            if (levels.Count > 1)
                            {
                                var closed = levels.Pop();
                                if (closed.Kind != LevelKind.Inline) brk = closed.Indent - 1;
                                L = levels.Peek();
                                if (closed.Kind == LevelKind.Cols) L.Clause = "";
                            }
                            break;
                        case ",":
                        case ";":
                        case ".":
                        case "::":
                            space = false;
                            break;
                    }
                }
                else if (isKw && L.Breaks)
                {
                    bool caseOpen = blocks.Count > 0 && blocks.Peek().Key;
                    bool hadNewline = i == 0 || newlines[i] > 0;
                    bool prevIs(params string[] words) => prev != null && prev.Type == TokType.Word && words.Contains(prev.Upper);

                    if ((up == "WHEN" || up == "ELSE") && caseOpen) brk = blocks.Peek().Value;
                    else if (up == "END")
                    {
                        if (blocks.Count == 0) brk = L.Indent;
                        else
                        {
                            var b = blocks.Pop();
                            if (b.Key) brk = b.Value - 1;
                            else { L.Indent = Math.Max(0, L.Indent - 1); brk = L.Indent; L.Clause = ""; L.StackList = false; }
                        }
                    }
                    else if (up == "BEGIN" || up == "ELSE" || up == "WHEN") brk = L.Indent;
                    else if (up == "GROUP" || up == "ORDER") { if (next != null && next.Is("BY")) brk = L.Indent; }
                    else if (up == "INTO") { if (L.Clause == "SELECT") brk = L.Indent; }
                    else if (up == "ON") { if (L.Clause == "FROM") brk = L.Indent + 1; }
                    else if (up == "AND" || up == "OR")
                    {
                        if (up == "AND" && L.Between) L.Between = false;
                        else if (L.Clause == "WHERE" || L.Clause == "HAVING") brk = L.Indent + 1;
                        else if (L.Clause == "ON") brk = L.Indent + 2;
                    }
                    else if (JoinWords.Contains(up))
                    {
                        if (!(prev != null && prev.Type == TokType.Word && JoinHints.Contains(prev.Upper)) && StartsJoin(toks, i)) brk = L.Indent;
                    }
                    else if (Strong.Contains(up))
                    {
                        brk = L.Indent;
                        if (up == "FROM" && prevIs("DELETE")) brk = -1;
                        else if ((up == "INSERT" || up == "UPDATE" || up == "DELETE")
                                 && (prevIs("FOR", "ON", "AFTER", "OF", "THEN") || (prev != null && prev.IsP(",") && L.Clause == "DDL"))) brk = -1;
                        else if ((up == "EXEC" || up == "EXECUTE") && prevIs("WITH")) brk = -1;
                        else if (L.Clause == "DDL" && (up == "CREATE" || up == "ALTER" || up == "DROP" || up == "IF" || up == "SET") && !hadNewline) brk = -1;
                    }
                    else if (Weak.Contains(up))
                    {
                        if (hadNewline && !(up == "WITH" && next != null && next.IsP("("))) brk = L.Indent;
                    }
                }

                if (brk < 0 && forced.TryGetValue(i, out int f)) brk = f;
                bool keepsPending = t.IsP(",") || t.IsP(";")
                    || ((up == "TRY" || up == "CATCH") && prev != null && (prev.Is("BEGIN") || prev.Is("END")));
                if (!keepsPending)
                {
                    if (brk < 0) brk = pendingBreak;
                    pendingBreak = -1;
                }
                if (mustBreak && brk < 0) brk = lineIndent;
                mustBreak = false;

                if (brk >= 0) newLine(i, brk);
                else if (space && !atLineStart && !noSpaceNext) sb.Append(' ');
                sb.Append(text);
                atLineStart = false;
                noSpaceNext = false;

                // What this token means for the tokens after it.
                if (t.Type == TokType.Punct)
                {
                    switch (t.Text)
                    {
                        case "(":
                            noSpaceNext = true;
                            var kind = LevelKind.Inline;
                            if (next != null && next.Is("SELECT")) kind = LevelKind.Sub;
                            else if (L.DdlTable || (prev != null && prev.Is("TABLE"))) kind = LevelKind.Cols;
                            L.DdlTable = false;
                            levels.Push(new Level { Kind = kind, Indent = lineIndent + 1 });
                            if (kind == LevelKind.Cols) pendingBreak = lineIndent + 1;
                            break;
                        case ",":
                            if (L.Kind == LevelKind.Cols) pendingBreak = L.Indent;
                            else if (L.Breaks && L.StackList) pendingBreak = L.Indent + 1;
                            break;
                        case ";":
                            if (L.Breaks) { pendingBreak = L.Indent; L.Clause = ""; L.StackList = false; L.Between = false; }
                            break;
                        case ".":
                        case "::":
                            noSpaceNext = true;
                            break;
                        case "-":
                        case "+":
                        case "~":
                            // unary when nothing that can end an operand precedes it
                            if (prev == null || (prev.Type == TokType.Punct && !prev.IsP(")")) || (prev.IsKeyword && !prev.Is("END")))
                                noSpaceNext = true;
                            break;
                    }
                }
                else if (isKw)
                {
                    if (up == "BETWEEN") L.Between = true;
                    if (L.Breaks)
                    {
                        switch (up)
                        {
                            case "CASE":
                                blocks.Push(new KeyValuePair<bool, int>(true, lineIndent + 1));
                                break;
                            case "BEGIN":
                                if (next != null && (next.Is("TRAN") || next.Is("TRANSACTION") || next.Is("DISTRIBUTED"))) break;
                                blocks.Push(new KeyValuePair<bool, int>(false, L.Indent));
                                L.Indent++;
                                L.Clause = "";
                                L.StackList = false;
                                pendingBreak = L.Indent;
                                break;
                            case "GO":
                                pendingBreak = L.Indent;
                                L.Clause = "";
                                break;
                            case "SELECT":
                            case "SET":
                            case "DECLARE":
                                L.Clause = up;
                                L.Between = false;
                                L.StackList = StackList(toks, i, up == "SELECT", out int first);
                                if (L.StackList) forced[first] = L.Indent + 1;
                                break;
                            case "VALUES":
                                L.Clause = up;
                                L.StackList = true;
                                break;
                            case "FROM":
                            case "JOIN":
                            case "APPLY":
                                L.Clause = "FROM";
                                L.StackList = false;
                                break;
                            case "ON":
                                if (L.Clause == "FROM") L.Clause = "ON";
                                break;
                            case "WHERE":
                            case "HAVING":
                            case "INSERT":
                                L.Clause = up;
                                L.StackList = false;
                                L.Between = false;
                                break;
                            case "IF":
                            case "WHILE":
                                if (L.Clause != "DDL") L.Clause = "COND";
                                L.StackList = false;
                                break;
                            case "CREATE":
                            case "ALTER":
                            case "DROP":
                            case "TRUNCATE":
                            case "GRANT":
                                L.Clause = "DDL";
                                L.StackList = false;
                                L.DdlTable = up == "CREATE" && next != null && next.Is("TABLE");
                                break;
                            case "GROUP":
                            case "ORDER":
                            case "UNION":
                            case "EXCEPT":
                            case "INTERSECT":
                            case "UPDATE":
                            case "DELETE":
                            case "MERGE":
                            case "EXEC":
                            case "EXECUTE":
                            case "INTO":
                            case "PRINT":
                            case "RETURN":
                            case "USE":
                                if (!(up == "INTO" && L.Clause == "INSERT")) { L.Clause = up == "INTO" ? "FROM" : ""; L.StackList = false; }
                                break;
                        }
                    }
                }

                prev = t;
            }

            string result = sb.ToString().TrimEnd();
            if (sql.EndsWith("\n")) result += eol;
            Verify(all, result);
            return result;
        }

        /// <summary>
        /// Formats only the statement around <paramref name="position"/> (statements are separated by ";" and GO)
        /// and returns the whole script. A statement that sits indented keeps that indentation on every line.
        /// </summary>
        public static string FormatStatementAt(string sql, int position)
        {
            int start = 0, end = sql.Length;
            foreach (var t in Tokenizer.Scan(sql))
            {
                if (!t.IsP(";") && !t.Is("GO")) continue;
                if (t.End <= position) start = t.End;
                else { end = t.IsP(";") ? t.End : t.Start; break; }
            }
            while (start < end && char.IsWhiteSpace(sql[start])) start++;
            while (end > start && char.IsWhiteSpace(sql[end - 1])) end--;
            if (start == end) return sql;

            int lineStart = start;
            while (lineStart > 0 && sql[lineStart - 1] != '\n') lineStart--;
            string lead = sql.Substring(lineStart, start - lineStart);
            if (lead.Trim().Length > 0) lead = ""; // something else shares the line: nothing to align with

            string eol = sql.Contains("\n") && !sql.Contains("\r\n") ? "\n" : "\r\n";
            var sb = new StringBuilder();
            // Re-indent at line breaks between tokens only: a break inside a string or comment is content.
            foreach (var t in Tokenizer.Scan(Format(sql.Substring(start, end - start))))
                sb.Append(t.Type == TokType.Ws ? t.Text.Replace("\r\n", "\n").Replace("\n", eol + lead) : t.Text);
            return sql.Substring(0, start) + sb + sql.Substring(end);
        }

        /// <summary>True when the join keyword at <paramref name="i"/> begins a JOIN / APPLY (not LEFT(...) the function).</summary>
        static bool StartsJoin(List<Tok> toks, int i)
        {
            for (int k = i; k < toks.Count; k++)
            {
                var t = toks[k];
                if (t.Type == TokType.Comment) continue;
                if (t.Is("JOIN") || t.Is("APPLY")) return true;
                if (t.Type != TokType.Word || !JoinHints.Contains(t.Upper)) return false;
            }
            return false;
        }

        /// <summary>
        /// Looks ahead from a SELECT / SET / DECLARE: does its list hold more than one item?
        /// <paramref name="first"/> is the first item's token index, after any DISTINCT / TOP n.
        /// </summary>
        static bool StackList(List<Tok> toks, int i, bool isSelect, out int first)
        {
            int k = i + 1;
            Func<int, int> skipComments = from => { while (from < toks.Count && toks[from].Type == TokType.Comment) from++; return from; };
            k = skipComments(k);
            if (isSelect)
            {
                while (k < toks.Count && (toks[k].Is("DISTINCT") || toks[k].Is("ALL"))) k = skipComments(k + 1);
                if (k < toks.Count && toks[k].Is("TOP"))
                {
                    k = skipComments(k + 1);
                    if (k < toks.Count && toks[k].IsP("("))
                    {
                        int d = 0;
                        for (; k < toks.Count; k++)
                        {
                            if (toks[k].IsP("(")) d++;
                            else if (toks[k].IsP(")") && --d == 0) { k++; break; }
                        }
                    }
                    else k++;
                    k = skipComments(k);
                    if (k < toks.Count && toks[k].Is("PERCENT")) k = skipComments(k + 1);
                }
            }
            first = k;

            int depth = 0, cases = 0;
            for (; k < toks.Count; k++)
            {
                var t = toks[k];
                if (t.Type == TokType.Comment) continue;
                if (t.IsP("(")) depth++;
                else if (t.IsP(")")) { if (--depth < 0) return false; }
                else if (depth > 0) continue;
                else if (t.IsP(",")) return true;
                else if (t.IsP(";")) return false;
                else if (t.Is("CASE")) cases++;
                else if (t.Is("END") && cases > 0) cases--;
                else if (cases == 0 && t.Type == TokType.Word && ListEnd.Contains(t.Upper)) return false;
            }
            return false;
        }

        /// <summary>The formatted script must be the original token for token; anything else is a bug, so refuse.</summary>
        static void Verify(List<Tok> original, string formatted)
        {
            var a = original.Where(t => t.Type != TokType.Ws).ToList();
            var b = Tokenizer.Scan(formatted).Where(t => t.Type != TokType.Ws).ToList();
            bool same = a.Count == b.Count;
            for (int i = 0; same && i < a.Count; i++)
                same = a[i].Type == b[i].Type && string.Equals(a[i].Text.TrimEnd(), b[i].Text.TrimEnd(),
                    a[i].Type == TokType.Word ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
            if (!same) throw new InvalidOperationException("SQLFinish: formatting would have changed the script, so it was left untouched.");
        }
    }
}
