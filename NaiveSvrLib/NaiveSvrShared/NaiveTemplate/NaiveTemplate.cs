using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

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
            foreach (var item in template.nodes) {
                if (item.Type == Template.NodeType.Text) {
                    writer.Write(item.Str);
                } else if (item.Type == Template.NodeType.Expression) {
                    writer.Write(data.GetValue(item.Str));
                }
            }
        }
    }

    public interface ITemplateData
    {
        object GetValue(string key);
    }

    public class TemplaterData : ITemplateData
    {
        public IDictionary<string, object> Dict;

        public TemplaterData(IDictionary<string, object> dict)
        {
            this.Dict = dict;
        }

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

        private class Parser
        {
            private readonly Template t;
            private readonly string str;

            public Parser(Template t, string str)
            {
                this.t = t;
                this.str = str;
            }

            public void Parse()
            {
                var tokens = new Tokenizer(str).GetTokenIterator();
                bool inExpression = false;
                bool hasIdentifier = false;
                foreach (var token in tokens) {
                    if (inExpression) {
                        if (token.Type == TokenType.Identifier) {
                            if (hasIdentifier)
                                throw createException("wrong syntax.");
                            hasIdentifier = true;
                            addExpression(token);
                        } else if (token.Type == TokenType.ExpressionEnd) {
                            if (hasIdentifier == false)
                                throw createException("empty expression.");
                            inExpression = false;
                        }
                    } else {
                        if (token.Type == TokenType.ExpressionBegin) {
                            inExpression = true;
                            hasIdentifier = false;
                        } else if (token.Type == TokenType.Text) {
                            addTextNode(token);
                        }
                    }
                }
                if (inExpression)
                    throw createException("unexpected EOF.");
            }

            private void addTextNode(Token token)
            {
                t.nodes.Add(new Node() { Type = NodeType.Text, Str = token.Str });
            }

            private void addExpression(Token token)
            {
                t.nodes.Add(new Node() { Type = NodeType.Expression, Str = token.Str });
            }

            private Exception createException(string msg)
            {
                return new Exception(msg);
            }
        }

        public class Node
        {
            public NodeType Type;
            public string Str;
        }

        public enum NodeType
        {
            Text,
            Expression
        }

        public class Tokenizer
        {
            private const string ExpressionBegin = "{{";
            private const string ExpressionEnd = "}}";
            public Tokenizer(string input)
            {
                this.Input = input;
                this.len = input.Length;
            }

            public string Input { get; }
            private string input => Input;

            private int cur;
            private int len;
            private StringBuilder sb = new StringBuilder();

            public IEnumerable<Token> GetTokenIterator()
            {
                while (cur < len) {
                    int textBegin = cur;
                    var nextBegin = Input.IndexOf(ExpressionBegin, cur);
                    if (nextBegin == -1) {
                        cur = len;
                    } else {
                        cur = nextBegin;
                    }
                    yield return CreateTextToken(textBegin);
                    if (nextBegin == -1) break;
                    yield return CreateExoressionBegin();
                    cur += ExpressionBegin.Length;
                    while (cur < len) {
                        var ch = Input[cur];
                        if (char.IsWhiteSpace(ch)) {
                            cur++;
                            continue;
                        }
                        if (compareString(input, ExpressionEnd, cur)) {
                            cur += ExpressionEnd.Length;
                            yield return CreateExoressionEnd();
                            break;
                        }
                        var word = getNextWord();
                        yield return CreateIdentifier(word);
                    }
                }
                yield break;
            }

            public static bool compareString(string str, string str2, int begin)
            {
                if (str.Length - begin < str2.Length) return false;
                for (int i = 0; i < str2.Length; i++) {
                    if (str[i + begin] != str2[i]) return false;
                }
                return true;
            }

            private int min(int a, int b) => a > b ? b : a;

            private string getNextWord()
            {
                sb.Clear();
                while (true) {
                    if (cur >= input.Length)
                        break;
                    var ch = input[cur];
                    if (isSpace(ch) && sb.Length > 0) {
                        break;
                    }
                    cur++;
                    bool _isSpecialChar = isSpecialChar(ch);
                    if (_isSpecialChar) {
                        if (sb.Length == 0)
                            return ch.ToString();
                        cur--;
                        return sb.ToString();
                    }
                    sb.Append(ch);
                }
                return sb.ToString();
            }

            private static bool isSpace(char ch)
            {
                return ch == ' ' || ch == '\t' || ch == '\r' || ch == '\n';
            }

            private static bool isSpecialChar(char ch)
            {
                return ch == '}';
            }

            private static Token CreateExoressionBegin()
            {
                return new Token() { Type = TokenType.ExpressionBegin, Str = ExpressionBegin };
            }

            private static Token CreateExoressionEnd()
            {
                return new Token() { Type = TokenType.ExpressionEnd, Str = ExpressionEnd };
            }

            private static Token CreateIdentifier(string str)
            {
                return new Token() { Type = TokenType.Identifier, Str = str };
            }

            private Token CreateTextToken(int begin)
            {
                return CreateTextToken(Input.Substring(begin, cur - begin));
            }

            private Token CreateTextToken(string text)
            {
                return new Token() {
                    Str = text,
                    Type = TokenType.Text
                };
            }
        }

        public class Token
        {
            public TokenType Type;
            public string Str;
            public override string ToString()
            {
                return $"{Type}|{Str}";
            }
        }

        public enum TokenType
        {
            Text,
            ExpressionBegin,
            ExpressionEnd,
            Identifier,
        }
    }
}
