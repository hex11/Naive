﻿using Naive.HttpSvr;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace NaiveTemplate
{
    public class Engine
    {
        public static readonly Engine Instance = new Engine();

        public string Run(string template, IDictionary<string, object> data)
        {
            return Run(new Template(template), new TemplaterData(data));
        }

        public string Run(string template, ITemplateData data)
        {
            return Run(new Template(template), data);
        }

        public string Run(Template template, ITemplateData data)
        {
            var sw = new StringWriter();
            Run(template, data, sw);
            return sw.ToString();
        }

        public void Run(Template template, ITemplateData data, TextWriter writer)
        {
            Run(template, (object)data, writer);
        }

        public void Run(Template template, Func<string, object> data, TextWriter writer)
        {
            Run(template, (object)data, writer);
        }

        private int Run(Template template, object data, TextWriter writer, int offset = 0)
        {
            List<object> dataChain = data as List<object>;
            for (int i = offset; i < template.nodes.Count;) {
                var node = template.nodes[i++];
                if (node.Type == Template.NodeType.Text) {
                    writer.Write(node.Str);
                } else if (node.Type == Template.NodeType.VarTag) {
                    var v = GetVal(data, node.Str);
                    if (v is Action<TextWriter> write) {
                        write(writer);
                    } else if (v is Func<TextWriter, Task> writeAsync) {
                        writeAsync(writer).RunSync();
                    } else {
                        writer.Write(v);
                    }
                } else if (node.IsSectionBegin) {
                    var v = GetVal(data, node.Str);
                    var isFalse = v == null || (v is bool b && b == false);
                    bool isInversed = node.Type == Template.NodeType.SectionBeginInverse;
                    if (isFalse ^ isInversed) {
                        SkipSection(template, ref i);
                    } else {
                        if (dataChain == null) {
                            dataChain = new List<object>();
                            dataChain.Add(data);
                        }
                        var chainPos = dataChain.Count;
                        dataChain.Add(null);
                        if (v is IEnumerable enu && !(v is string)) {
                            int end = 0;
                            foreach (var item in enu) {
                                dataChain[chainPos] = item;
                                end = Run(template, dataChain, writer, i);
                            }
                            if (end > 0) {
                                i = end;
                            } else {
                                if (isInversed) {
                                    i = Run(template, data, writer, i);
                                } else {
                                    SkipSection(template, ref i);
                                }
                            }
                        } else {
                            dataChain[chainPos] = v;
                            i = Run(template, dataChain, writer, i);
                        }
                        dataChain.RemoveAt(chainPos);
                    }
                } else if (node.Type == Template.NodeType.SectionEnd) {
                    return i;
                }
            }
            return -1;
        }

        public Task RunAsync(Template template, ITemplateData data, TextWriter writer)
        {
            return RunAsync(template, (object)data, writer);
        }

        public Task RunAsync(Template template, Func<string, object> data, TextWriter writer)
        {
            return RunAsync(template, (object)data, writer);
        }

        private async Task<int> RunAsync(Template template, object data, TextWriter writer, int offset = 0)
        {
            List<object> dataChain = data as List<object>;
            for (int i = offset; i < template.nodes.Count;) {
                var node = template.nodes[i++];
                if (node.Type == Template.NodeType.Text) {
                    await writer.WriteAsync(node.Str);
                } else if (node.Type == Template.NodeType.VarTag) {
                    var v = GetVal(data, node.Str);
                    if (v is Action<TextWriter> write) {
                        await Task.Run(() => write(writer));
                    } else if (v is Func<TextWriter, Task> writeAsync) {
                        await writeAsync(writer);
                    } else {
                        await writer.WriteAsync(v?.ToString());
                    }
                } else if (node.IsSectionBegin) {
                    var v = GetVal(data, node.Str);
                    var isFalse = v == null || (v is bool b && b == false);
                    bool isInversed = node.Type == Template.NodeType.SectionBeginInverse;
                    if (isFalse ^ isInversed) {
                        SkipSection(template, ref i);
                    } else {
                        if (dataChain == null) {
                            dataChain = new List<object>();
                            dataChain.Add(data);
                        }
                        var chainPos = dataChain.Count;
                        dataChain.Add(null);
                        if (v is IEnumerable enu && !(v is string)) {
                            int end = 0;
                            foreach (var item in enu) {
                                dataChain[chainPos] = item;
                                end = await RunAsync(template, dataChain, writer, i);
                            }
                            if (end > 0) {
                                i = end;
                            } else {
                                if (isInversed) {
                                    i = await RunAsync(template, data, writer, i);
                                } else {
                                    SkipSection(template, ref i);
                                }
                            }
                        } else {
                            dataChain[chainPos] = v;
                            i = await RunAsync(template, dataChain, writer, i);
                        }
                        dataChain.RemoveAt(chainPos);
                    }
                } else if (node.Type == Template.NodeType.SectionEnd) {
                    return i;
                }
            }
            return -1;
        }

        private static void SkipSection(Template template, ref int i)
        {
            int unclosed = 1;
            while (unclosed > 0) {
                var n = template.nodes[i++];
                if (n.IsSectionBegin)
                    unclosed++;
                else if (n.IsSectionEnd)
                    unclosed--;
            }
        }

        private static object GetVal(object obj, string key)
        {
            if (obj is List<object> chain) {
                for (int i = chain.Count - 1; i >= 0; i--) {
                    var r = GetVal(chain[i], key);
                    if (r != null)
                        return r;
                }
            }
            if (obj is ITemplateData data) {
                return data.GetValue(key);
            } else if (obj is Func<string, object> fun) {
                return fun(key);
            } else if (obj is IDictionary<string, object> dict) {
                if (dict.TryGetValue(key, out var val))
                    return val;
            }
            return null;
        }
    }

    public interface ITemplateData
    {
        object GetValue(string key);
    }

    public class TemplaterData : ITemplateData
    {
        public IDictionary<string, object> Dict { get; }

        public TemplaterData() : this(new Dictionary<string, object>())
        {
        }

        public TemplaterData(IDictionary<string, object> dict)
        {
            this.Dict = dict;
        }

        private TemplaterData add(string key, object value)
        {
            Dict.Add(key, value);
            return this;
        }

        public void Add(string key, object obj) => add(key, obj);

        public void Add(string key, string value) => add(key, value);

        public void Add(string key, Action<TextWriter> writer) => add(key, writer);

        public void Add(string key, Func<TextWriter, Task> asyncWriter) => add(key, asyncWriter);

        public void Add(string key, Func<string, object> func) => add(key, func);

        public object GetValue(string key)
        {
            try {
                return Dict[key];
            } catch (Exception) {
                return null;
            }
        }
    }

    public class Template
    {
        public List<Node> nodes = new List<Node>();

        public Template(string template) //: this()
        {
            new Parser(this, template).Parse();
        }

        /// <summary>
        /// TrimExcess on the node list and returns itself.
        /// </summary>
        public Template TrimExcess()
        {
            nodes.TrimExcess();
            return this;
        }

        private struct Parser
        {
            private readonly Template t;
            private readonly string str;
            private Tokens tokens;

            public Parser(Template t, string str)
            {
                this.t = t;
                this.str = str;
                tokens = new Tokens(str);
            }

            public void Parse()
            {
                ParseSection();
            }

            // returns true if the section is closed.
            bool ParseSection(string sectionTag = null)
            {
                while (true) {
                    if (tokens.TryExpect(TokenType.Text)) {
                        addPlainText(tokens.Consume());
                    } else if (tokens.TryExpectAndConsume(TokenType.TagBegin)) {
                        if (tokens.TryExpect(TokenType.Hash) || tokens.TryExpect(TokenType.Caret)) {
                            var nodeType = tokens.Consume().Type == TokenType.Hash
                                ? NodeType.SectionBeginNormal : NodeType.SectionBeginInverse;
                            var tag = tokens.ExpectAndConsume(TokenType.Identifier);
                            var tagStr = tag.Str;
                            tokens.ExpectAndConsume(TokenType.TagEnd);
                            add(nodeType, tagStr);
                            if (!ParseSection(tagStr)) {
                                throw CreateException($"section '{tagStr}' is not closed", tag);
                            }
                        } else if (tokens.TryExpect(TokenType.Slash)) {
                            if (sectionTag == null) {
                                throw CreateUnexpectedTokenException();
                            } else {
                                tokens.Consume();
                                if (tokens.TryExpect(TokenType.Identifier)) {
                                    var t = tokens.Consume();
                                    if (sectionTag != t.Str)
                                        throw CreateException($"wrong tag, should be '{sectionTag}'", t);
                                } // the tag can be omited.
                                tokens.ExpectAndConsume(TokenType.TagEnd);
                                add(NodeType.SectionEnd, sectionTag);
                                return true;
                            }
                        } else if (tokens.TryExpectAndConsume(TokenType.Exclamation)) {
                            while (tokens.Consume().Type != TokenType.TagEnd) {
                                // skip to TagEnd
                            }
                        } else {
                            addVarTag(tokens.ExpectAndConsume(TokenType.Identifier));
                            tokens.ExpectAndConsume(TokenType.TagEnd);
                        }
                    } else if (tokens.TryExpectAndConsume(TokenType.EndOfFile)) {
                        break;
                    } else {
                        throw CreateUnexpectedTokenException();
                    }
                }
                return false;
            }


            private Exception CreateUnexpectedTokenException() => CreateUnexpectedTokenException(tokens.Peek());

            private Exception CreateUnexpectedTokenException(Token token)
            {
                return CreateException("Unexpected token Type " + token.Type, token);
            }

            private Exception CreateException(string message, Token token)
            {
                return new ParserException(message + " at " + token.Position) {
                    Token = token
                };
            }

            private void addPlainText(Token token)
            {
                t.nodes.Add(new Node() { Type = NodeType.Text, Str = token.Str });
            }

            private void addVarTag(Token token)
            {
                t.nodes.Add(new Node() { Type = NodeType.VarTag, Str = token.Str });
            }

            private void add(NodeType type, string str)
            {
                t.nodes.Add(new Node() { Type = type, Str = str });
            }
        }

        public struct Node
        {
            public NodeType Type;
            public string Str;

            public bool IsSectionBegin => Type == NodeType.SectionBeginNormal
                                        | Type == NodeType.SectionBeginInverse;

            public bool IsSectionEnd => Type == NodeType.SectionEnd;

            public override string ToString() => Type + " " + Str;
        }

        public enum NodeType
        {
            Text,
            VarTag,
            SectionBeginNormal,
            SectionBeginInverse,
            SectionEnd
        }

        public class Tokens
        {
            private Tokenizer tokenizer;
            private IEnumerator<Token> enumerator;

            private Token current;

            public Tokens(string input)
            {
                tokenizer.Init(input);
                enumerator = tokenizer.GetTokenIterator().GetEnumerator();
                Next();
            }

            void Next()
            {
                if (enumerator.MoveNext())
                    current = enumerator.Current;
                else
                    current = new Token { Type = TokenType.EndOfFile };
            }

            public Token Peek()
            {
                return current;
            }

            public bool TryExpect(TokenType tt)
            {
                return Peek().Type == tt;
            }

            public void Expect(TokenType tt)
            {
                if (!TryExpect(tt))
                    throw new ParserException($"expected token type {tt}, taken {Peek().TypeAndPosition}") {
                        Token = Peek()
                    };
            }

            public Token Consume()
            {
                var t = Peek();
                Next();
                return t;
            }

            public Token ExpectAndConsume(TokenType tt)
            {
                Expect(tt);
                return Consume();
            }

            public bool TryExpectAndConsume(TokenType tt)
            {
                var r = Peek().Type == tt;
                if (r) {
                    Consume();
                }
                return r;
            }

            private struct Tokenizer
            {
                private const string DelimiterBegin = "{{";
                private const string DelimiterEnd = "}}";

                public void Init(string input)
                {
                    this.Input = input;
                    this.len = input.Length;
                    this.curPosition = new TextPosition(1, 1);
                }

                public string Input;
                private string input => Input;

                private char curChar => input[cur];

                private int cur;
                private int len;

                TextPosition curPosition;

                public IEnumerable<Token> GetTokenIterator()
                {
                    while (cur < len) {
                        int textBegin = cur;
                        var nextBegin = Input.IndexOf(DelimiterBegin, cur);
                        int textLen = (nextBegin == -1) ? len - textBegin : nextBegin - textBegin;
                        if (textLen > 0) {
                            yield return CreateTextToken(Peek(textLen));
                            Consume(textLen);
                        }
                        if (nextBegin == -1) break;
                        yield return CreateExoressionBegin();
                        Consume(DelimiterBegin.Length);
                        while (cur < len) {
                            // in expression:
                            if (ConsumeAllSpaces())
                                continue;
                            var ch = curChar;
                            if (ch == '#') {
                                yield return CreateToken(TokenType.Hash, "#");
                                Consume();
                            } else if (ch == '/') {
                                yield return CreateToken(TokenType.Slash, "/");
                                Consume();
                            } else if (ch == '^') {
                                yield return CreateToken(TokenType.Caret, "^");
                                Consume();
                            } else if (ch == '!') {
                                yield return CreateToken(TokenType.Exclamation, "!");
                                Consume();
                            } else if (TryExpect(DelimiterEnd)) {
                                yield return CreateExoressionEnd();
                                Consume(DelimiterEnd.Length);
                                break;
                            } else {
                                var wordBeginPos = curPosition;
                                var word = ConsumeWord();
                                yield return CreateToken(TokenType.Identifier, word, wordBeginPos);
                            }
                        }
                    }
                }

                private string Peek(int length)
                {
                    return Input.Substring(cur, length);
                }

                private bool TryExpect(string pattern)
                {
                    return CompareString(input, cur, pattern);
                }

                private bool TryExpectAndConsume(string pattern)
                {
                    bool r;
                    if (r = TryExpect(pattern)) {
                        Consume(pattern.Length);
                    }
                    return r;
                }

                private char Consume()
                {
                    var ch = Input[cur++];
                    if (ch == '\n') {
                        curPosition.Line++;
                        curPosition.Column = 1;
                    } else {
                        curPosition.Column++;
                    }
                    return ch;
                }

                private void Consume(int count)
                {
                    int newlines = 0;
                    int lastNewlinePosition = 0;
                    for (int i = 0; i < count; i++, cur++) {
                        var ch = Input[cur];
                        if (ch == '\n') {
                            newlines++;
                            lastNewlinePosition = cur;
                        }
                    }
                    if (newlines == 0) {
                        curPosition.Column += count;
                    } else {
                        curPosition.Line += newlines;
                        curPosition.Column = cur - lastNewlinePosition;
                    }
                }

                private bool ConsumeAllSpaces()
                {
                    if (cur >= len)
                        return false;
                    if (!isSpace(curChar))
                        return false;
                    do {
                        Consume();
                    } while (isSpace(curChar));
                    return true;
                }

                public static bool CompareString(string str, int strOffset, string pattern)
                {
                    if (str.Length - strOffset < pattern.Length) return false;
                    for (int i = 0; i < pattern.Length; i++) {
                        if (str[i + strOffset] != pattern[i]) return false;
                    }
                    return true;
                }

                private string ConsumeWord()
                {
                    var begin = cur;
                    while (cur < input.Length) {
                        var ch = input[cur];
                        if (isSpace(ch)) {
                            if (cur > begin)
                                break;
                            else
                                begin++;
                        }
                        if (isSpecialChar(ch)) {
                            if (cur - begin == 0)
                                Consume();
                            break;
                        }
                        Consume();
                    }
                    var end = cur;
                    ConsumeAllSpaces();
                    return Input.Substring(begin, end - begin);
                }

                private static bool isSpace(char ch)
                {
                    return ch == ' ' | ch == '\t' | ch == '\r' | ch == '\n';
                }

                private static bool isSpecialChar(char ch)
                {
                    return ch == '}' | ch == '#' | ch == '/' | ch == '^' | ch == '!';
                }

                private Token CreateExoressionBegin()
                {
                    return CreateToken(TokenType.TagBegin, DelimiterBegin);
                }

                private Token CreateExoressionEnd()
                {
                    return CreateToken(TokenType.TagEnd, DelimiterEnd);
                }

                private Token CreateIdentifier(string str)
                {
                    return CreateToken(TokenType.Identifier, str);
                }

                private Token CreateTextToken(int begin)
                {
                    return CreateTextToken(Input.Substring(begin, cur - begin));
                }

                private Token CreateTextToken(string text)
                {
                    return CreateToken(TokenType.Text, text);
                }

                private Token CreateToken(TokenType tt, string str)
                {
                    return CreateToken(tt, str, curPosition);
                }

                private Token CreateToken(TokenType tt, string str, TextPosition pos)
                {
                    return new Token { Type = tt, Str = str, Position = pos };
                }
            }
        }

        public struct Token
        {
            public TokenType Type;
            public string Str;
            public TextPosition Position;

            public override string ToString()
            {
                return $"{Type}|{Str}";
            }

            public string TypeAndPosition
                => "Type " + Type.ToString() + " at " + Position.ToString();
        }

        public struct TextPosition
        {
            public int Line;
            public int Column;

            public TextPosition(int line, int column)
            {
                Line = line;
                Column = column;
            }

            public override string ToString()
            {
                return $"Line {Line} Column {Column}";
            }
        }

        public enum TokenType
        {
            Undefined,
            EndOfFile,
            Text,
            TagBegin,
            TagEnd,
            Identifier,
            Hash,
            Caret,
            Slash,
            Exclamation,
        }

        [Serializable]
        public class ParserException : Exception
        {
            public ParserException() { }
            public ParserException(string message) : base(message) { }
            public ParserException(string message, Exception inner) : base(message, inner) { }
            protected ParserException(
              System.Runtime.Serialization.SerializationInfo info,
              System.Runtime.Serialization.StreamingContext context) : base(info, context) { }

            public Token Token { get; internal set; }
        }
    }
}
