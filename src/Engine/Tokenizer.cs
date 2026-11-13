using System;
using System.Collections.Generic;

namespace SQLFinish.Engine
{
    public enum TokType { Ws, Comment, String, Quoted, Word, Number, Punct }

    public sealed class Tok
    {
        public TokType Type;
        public int Start;
        public string Text;
        public bool Closed = true; // false for an unterminated string / comment / [identifier

        public int End => Start + Text.Length;
        public string Upper => Type == TokType.Word ? Text.ToUpperInvariant() : null;
        public bool Is(string keyword) => Type == TokType.Word && string.Equals(Text, keyword, StringComparison.OrdinalIgnoreCase);
        public bool IsP(string punct) => Type == TokType.Punct && Text == punct;
        public bool IsKeyword => Type == TokType.Word && Keywords.Reserved.Contains(Text);
        /// <summary>A word or quoted identifier that can name a table, column or alias.</summary>
        public bool IsName => Type == TokType.Quoted || (Type == TokType.Word && !IsKeyword && Text[0] != '@');
        /// <summary>The identifier without its [] or "" quoting.</summary>
        public string Name
        {
            get
            {
                if (Type != TokType.Quoted) return Text;
                var inner = Text.Substring(1, Math.Max(0, Text.Length - (Closed ? 2 : 1)));
                return Text[0] == '[' ? inner.Replace("]]", "]") : inner.Replace("\"\"", "\"");
            }
        }
        public override string ToString() => Type + ":" + Text;
    }

    public static class Tokenizer
    {
        static readonly string[] Ops = { "<>", "<=", ">=", "!=", "!<", "!>", "+=", "-=", "*=", "/=", "%=", "::" };

        static bool IsWordStart(char c) => char.IsLetter(c) || c == '_' || c == '@' || c == '#';
        static bool IsWordPart(char c) => char.IsLetterOrDigit(c) || c == '_' || c == '@' || c == '#' || c == '$';

        public static List<Tok> Scan(string s)
        {
            var list = new List<Tok>();
            int i = 0, n = s.Length;
            while (i < n)
            {
                int start = i;
                char c = s[i];
                var type = TokType.Punct;
                bool closed = true;

                if (char.IsWhiteSpace(c))
                {
                    while (i < n && char.IsWhiteSpace(s[i])) i++;
                    type = TokType.Ws;
                }
                else if (c == '-' && i + 1 < n && s[i + 1] == '-')
                {
                    while (i < n && s[i] != '\r' && s[i] != '\n') i++;
                    type = TokType.Comment;
                }
                else if (c == '/' && i + 1 < n && s[i + 1] == '*')
                {
                    int depth = 0; // T-SQL block comments nest
                    closed = false;
                    while (i < n)
                    {
                        if (s[i] == '/' && i + 1 < n && s[i + 1] == '*') { depth++; i += 2; }
                        else if (s[i] == '*' && i + 1 < n && s[i + 1] == '/') { depth--; i += 2; if (depth == 0) { closed = true; break; } }
                        else i++;
                    }
                    type = TokType.Comment;
                }
                else if (c == '\'' || ((c == 'N' || c == 'n') && i + 1 < n && s[i + 1] == '\''))
                {
                    i += c == '\'' ? 1 : 2;
                    closed = ScanQuoted(s, ref i, '\'');
                    type = TokType.String;
                }
                else if (c == '[' || c == '"')
                {
                    i++;
                    closed = ScanQuoted(s, ref i, c == '[' ? ']' : '"');
                    type = TokType.Quoted;
                }
                else if (char.IsDigit(c) || (c == '.' && i + 1 < n && char.IsDigit(s[i + 1])))
                {
                    if (c == '0' && i + 1 < n && (s[i + 1] == 'x' || s[i + 1] == 'X'))
                    {
                        i += 2;
                        while (i < n && Uri.IsHexDigit(s[i])) i++;
                    }
                    else
                    {
                        while (i < n && char.IsDigit(s[i])) i++;
                        if (i < n && s[i] == '.') { i++; while (i < n && char.IsDigit(s[i])) i++; }
                        if (i < n && (s[i] == 'e' || s[i] == 'E'))
                        {
                            int j = i + 1;
                            if (j < n && (s[j] == '+' || s[j] == '-')) j++;
                            if (j < n && char.IsDigit(s[j])) { i = j; while (i < n && char.IsDigit(s[i])) i++; }
                        }
                    }
                    type = TokType.Number;
                }
                else if (IsWordStart(c))
                {
                    while (i < n && IsWordPart(s[i])) i++;
                    type = TokType.Word;
                }
                else
                {
                    i++;
                    foreach (var op in Ops)
                        if (string.CompareOrdinal(s, start, op, 0, op.Length) == 0) { i = start + op.Length; break; }
                }

                list.Add(new Tok { Type = type, Start = start, Text = s.Substring(start, i - start), Closed = closed });
            }
            return list;
        }

        /// <summary>Advances past the closing quote (a doubled quote is an escape). False if unterminated.</summary>
        static bool ScanQuoted(string s, ref int i, char close)
        {
            while (i < s.Length)
            {
                if (s[i] == close)
                {
                    if (i + 1 < s.Length && s[i + 1] == close) { i += 2; continue; }
                    i++;
                    return true;
                }
                i++;
            }
            return false;
        }
    }
}
