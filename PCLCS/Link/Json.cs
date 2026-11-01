// Copyright (c) 2026 Xocio. Licensed under the GNU General Public License v3.0.
// Based on Campfire Link implementation.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace PCLCS.Link
{
    /// <summary>
    /// Minimal JSON reader and writer for the small messages multiplayer exchanges.
    /// </summary>
    public static class Json
    {
        private const int MaxDepth = 64;

        /// <summary>Parses <paramref name="text"/>; throws <see cref="FormatException"/> on malformed input.</summary>
        public static object Parse(string text)
        {
            int position = 0;
            object value = ReadValue(text, ref position, 0);
            SkipWhitespace(text, ref position);
            if (position != text.Length)
                throw new FormatException("Unexpected text after JSON value.");
            return value;
        }

        /// <summary>Parses <paramref name="text"/>, or returns <c>null</c> when it is not valid JSON.</summary>
        public static object? TryParse(string? text)
        {
            try
            {
                return text == null ? null : Parse(text);
            }
            catch (FormatException)
            {
                return null;
            }
        }

        public static string Serialize(object? value)
        {
            var builder = new StringBuilder();
            Write(builder, value);
            return builder.ToString();
        }

        // ======================== Reading ========================

        private static object ReadValue(string text, ref int position, int depth)
        {
            if (depth > MaxDepth)
                throw new FormatException("JSON is nested too deeply.");

            SkipWhitespace(text, ref position);
            if (position >= text.Length)
                throw new FormatException("Unexpected end of JSON.");

            char c = text[position];
            switch (c)
            {
                case '{':
                    return ReadObject(text, ref position, depth);
                case '[':
                    return ReadArray(text, ref position, depth);
                case '"':
                    return ReadString(text, ref position);
                case 't':
                    Expect(text, ref position, "true");
                    return true;
                case 'f':
                    Expect(text, ref position, "false");
                    return false;
                case 'n':
                    Expect(text, ref position, "null");
                    return null!;
                default:
                    if (c == '-' || (c >= '0' && c <= '9'))
                        return ReadNumber(text, ref position);
                    throw new FormatException("Unexpected character '" + c + "' in JSON.");
            }
        }

        private static Dictionary<string, object> ReadObject(string text, ref int position, int depth)
        {
            var result = new Dictionary<string, object>(StringComparer.Ordinal);
            position++;
            SkipWhitespace(text, ref position);
            if (Peek(text, position) == '}')
            {
                position++;
                return result;
            }

            while (true)
            {
                SkipWhitespace(text, ref position);
                string key = ReadString(text, ref position);
                SkipWhitespace(text, ref position);
                ExpectChar(text, ref position, ':');
                object value = ReadValue(text, ref position, depth + 1);
                result[key] = value;

                SkipWhitespace(text, ref position);
                char next = Peek(text, position);
                if (next == '}')
                {
                    position++;
                    return result;
                }
                ExpectChar(text, ref position, ',');
            }
        }

        private static object[] ReadArray(string text, ref int position, int depth)
        {
            var items = new List<object>();
            position++;
            SkipWhitespace(text, ref position);
            if (Peek(text, position) == ']')
            {
                position++;
                return items.ToArray();
            }

            while (true)
            {
                items.Add(ReadValue(text, ref position, depth + 1));
                SkipWhitespace(text, ref position);
                char next = Peek(text, position);
                if (next == ']')
                {
                    position++;
                    return items.ToArray();
                }
                ExpectChar(text, ref position, ',');
            }
        }

        private static string ReadString(string text, ref int position)
        {
            ExpectChar(text, ref position, '"');
            var builder = new StringBuilder();
            while (position < text.Length)
            {
                char c = text[position++];
                if (c == '"')
                    return builder.ToString();
                if (c == '\\')
                {
                    if (position >= text.Length)
                        throw new FormatException("Incomplete escape sequence in JSON string.");
                    char escape = text[position++];
                    switch (escape)
                    {
                        case '"':
                        case '\\':
                        case '/':
                            builder.Append(escape);
                            break;
                        case 'b':
                            builder.Append('\b');
                            break;
                        case 'f':
                            builder.Append('\f');
                            break;
                        case 'n':
                            builder.Append('\n');
                            break;
                        case 'r':
                            builder.Append('\r');
                            break;
                        case 't':
                            builder.Append('\t');
                            break;
                        case 'u':
                            if (position + 4 > text.Length)
                                throw new FormatException("Incomplete \\u escape in JSON string.");
                            string hex = text.Substring(position, 4);
                            position += 4;
                            builder.Append((char)int.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                            break;
                        default:
                            throw new FormatException("Unknown escape sequence '\\" + escape + "' in JSON string.");
                    }
                }
                else
                {
                    builder.Append(c);
                }
            }
            throw new FormatException("Unterminated JSON string.");
        }

        private static double ReadNumber(string text, ref int position)
        {
            int start = position;
            if (text[position] == '-')
                position++;
            while (position < text.Length && text[position] >= '0' && text[position] <= '9')
                position++;
            if (position < text.Length && text[position] == '.')
            {
                position++;
                while (position < text.Length && text[position] >= '0' && text[position] <= '9')
                    position++;
            }
            if (position < text.Length && (text[position] == 'e' || text[position] == 'E'))
            {
                position++;
                if (position < text.Length && (text[position] == '+' || text[position] == '-'))
                    position++;
                while (position < text.Length && text[position] >= '0' && text[position] <= '9')
                    position++;
            }
            string slice = text.Substring(start, position - start);
            return double.Parse(slice, CultureInfo.InvariantCulture);
        }

        private static void SkipWhitespace(string text, ref int position)
        {
            while (position < text.Length)
            {
                char c = text[position];
                if (c == ' ' || c == '\t' || c == '\r' || c == '\n')
                    position++;
                else
                    break;
            }
        }

        private static char Peek(string text, int position)
        {
            return position < text.Length ? text[position] : '\0';
        }

        private static void Expect(string text, ref int position, string expected)
        {
            if (position + expected.Length > text.Length || text.Substring(position, expected.Length) != expected)
                throw new FormatException("Expected '" + expected + "' at position " + position + ".");
            position += expected.Length;
        }

        private static void ExpectChar(string text, ref int position, char expected)
        {
            if (position >= text.Length || text[position] != expected)
                throw new FormatException("Expected '" + expected + "' at position " + position + ".");
            position++;
        }

        // ======================== Writing ========================

        private static void Write(StringBuilder builder, object? value)
        {
            if (value == null)
            {
                builder.Append("null");
            }
            else if (value is bool b)
            {
                builder.Append(b ? "true" : "false");
            }
            else if (value is string s)
            {
                WriteString(builder, s);
            }
            else if (value is IDictionary dict)
            {
                builder.Append('{');
                bool first = true;
                foreach (DictionaryEntry entry in dict)
                {
                    if (!first)
                        builder.Append(',');
                    first = false;
                    WriteString(builder, entry.Key.ToString());
                    builder.Append(':');
                    Write(builder, entry.Value);
                }
                builder.Append('}');
            }
            else if (value is IEnumerable items)
            {
                builder.Append('[');
                bool first = true;
                foreach (object item in items)
                {
                    if (!first)
                        builder.Append(',');
                    first = false;
                    Write(builder, item);
                }
                builder.Append(']');
            }
            else if (value is double d)
            {
                builder.Append(d.ToString("R", CultureInfo.InvariantCulture));
            }
            else if (value is float f)
            {
                builder.Append(f.ToString("R", CultureInfo.InvariantCulture));
            }
            else if (value is IFormattable formattable)
            {
                builder.Append(formattable.ToString(null, CultureInfo.InvariantCulture));
            }
            else
            {
                builder.Append(value.ToString());
            }
        }

        private static void WriteString(StringBuilder builder, string value)
        {
            builder.Append('"');
            foreach (char c in value)
            {
                switch (c)
                {
                    case '"':
                        builder.Append("\\\"");
                        break;
                    case '\\':
                        builder.Append("\\\\");
                        break;
                    case '\b':
                        builder.Append("\\b");
                        break;
                    case '\f':
                        builder.Append("\\f");
                        break;
                    case '\n':
                        builder.Append("\\n");
                        break;
                    case '\r':
                        builder.Append("\\r");
                        break;
                    case '\t':
                        builder.Append("\\t");
                        break;
                    default:
                        if (c < ' ')
                            builder.AppendFormat(CultureInfo.InvariantCulture, "\\u{0:x4}", (int)c);
                        else
                            builder.Append(c);
                        break;
                }
            }
            builder.Append('"');
        }
    }
}
