using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace CSharpIosPerfMonitor
{
    public sealed class SimpleJson
    {
        private readonly string _text;
        private int _index;

        private SimpleJson(string text)
        {
            _text = text ?? "";
        }

        public static object Parse(string text)
        {
            SimpleJson parser = new SimpleJson(text);
            object value = parser.ParseValue();
            parser.SkipWhite();
            return value;
        }

        private object ParseValue()
        {
            SkipWhite();
            if (_index >= _text.Length) return null;
            char ch = _text[_index];
            if (ch == '{') return ParseObject();
            if (ch == '[') return ParseArray();
            if (ch == '"') return ParseString();
            if (ch == 't' && Match("true")) return true;
            if (ch == 'f' && Match("false")) return false;
            if (ch == 'n' && Match("null")) return null;
            return ParseNumber();
        }

        private Dictionary<string, object> ParseObject()
        {
            Dictionary<string, object> result = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            _index++;
            SkipWhite();
            if (Peek('}'))
            {
                _index++;
                return result;
            }
            while (_index < _text.Length)
            {
                string key = ParseString();
                SkipWhite();
                Expect(':');
                result[key] = ParseValue();
                SkipWhite();
                if (Peek('}'))
                {
                    _index++;
                    break;
                }
                Expect(',');
            }
            return result;
        }

        private List<object> ParseArray()
        {
            List<object> result = new List<object>();
            _index++;
            SkipWhite();
            if (Peek(']'))
            {
                _index++;
                return result;
            }
            while (_index < _text.Length)
            {
                result.Add(ParseValue());
                SkipWhite();
                if (Peek(']'))
                {
                    _index++;
                    break;
                }
                Expect(',');
            }
            return result;
        }

        private string ParseString()
        {
            Expect('"');
            StringBuilder builder = new StringBuilder();
            while (_index < _text.Length)
            {
                char ch = _text[_index++];
                if (ch == '"') break;
                if (ch != '\\')
                {
                    builder.Append(ch);
                    continue;
                }
                if (_index >= _text.Length) break;
                char esc = _text[_index++];
                switch (esc)
                {
                    case '"': builder.Append('"'); break;
                    case '\\': builder.Append('\\'); break;
                    case '/': builder.Append('/'); break;
                    case 'b': builder.Append('\b'); break;
                    case 'f': builder.Append('\f'); break;
                    case 'n': builder.Append('\n'); break;
                    case 'r': builder.Append('\r'); break;
                    case 't': builder.Append('\t'); break;
                    case 'u':
                        if (_index + 4 <= _text.Length)
                        {
                            string hex = _text.Substring(_index, 4);
                            int code;
                            if (int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out code))
                            {
                                builder.Append((char)code);
                                _index += 4;
                            }
                        }
                        break;
                }
            }
            return builder.ToString();
        }

        private object ParseNumber()
        {
            int start = _index;
            while (_index < _text.Length && "-+0123456789.eE".IndexOf(_text[_index]) >= 0)
            {
                _index++;
            }
            string raw = _text.Substring(start, _index - start);
            double doubleValue;
            long longValue;
            if (raw.IndexOf('.') >= 0 || raw.IndexOf('e') >= 0 || raw.IndexOf('E') >= 0)
            {
                if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out doubleValue))
                {
                    return doubleValue;
                }
            }
            if (long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out longValue))
            {
                return longValue;
            }
            return 0L;
        }

        private bool Match(string expected)
        {
            if (_index + expected.Length > _text.Length) return false;
            if (string.Compare(_text, _index, expected, 0, expected.Length, StringComparison.Ordinal) != 0) return false;
            _index += expected.Length;
            return true;
        }

        private bool Peek(char ch)
        {
            SkipWhite();
            return _index < _text.Length && _text[_index] == ch;
        }

        private void Expect(char ch)
        {
            SkipWhite();
            if (_index >= _text.Length || _text[_index] != ch)
            {
                throw new FormatException("Expected '" + ch + "' at " + _index + ".");
            }
            _index++;
        }

        private void SkipWhite()
        {
            while (_index < _text.Length && char.IsWhiteSpace(_text[_index]))
            {
                _index++;
            }
        }
    }
}
