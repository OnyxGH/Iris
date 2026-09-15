using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Iris.Ai.Utils;

/// <summary>JSON repair and streaming (partial) JSON parsing.</summary>
public static class JsonParse
{
    private static readonly JsonDocumentOptions DocOptions = new() { AllowTrailingCommas = false };

    /// <summary>
    /// Repairs malformed JSON string literals by escaping raw control characters inside strings and
    /// doubling backslashes before invalid escape characters.
    /// </summary>
    public static string RepairJson(string json)
    {
        var repaired = new StringBuilder(json.Length + 16);
        var inString = false;

        for (var index = 0; index < json.Length; index++)
        {
            var ch = json[index];

            if (!inString)
            {
                repaired.Append(ch);
                if (ch == '"') inString = true;
                continue;
            }

            if (ch == '"')
            {
                repaired.Append(ch);
                inString = false;
                continue;
            }

            if (ch == '\\')
            {
                if (index + 1 >= json.Length)
                {
                    repaired.Append("\\\\");
                    continue;
                }
                var next = json[index + 1];
                if (next == 'u' && index + 6 <= json.Length && IsHex4(json.AsSpan(index + 2, 4)))
                {
                    repaired.Append("\\u").Append(json, index + 2, 4);
                    index += 5;
                    continue;
                }
                if (next is '"' or '\\' or '/' or 'b' or 'f' or 'n' or 'r' or 't' or 'u')
                {
                    // "u" without four hex digits falls through here in JS too (VALID_JSON_ESCAPES contains "u").
                    repaired.Append('\\').Append(next);
                    index += 1;
                    continue;
                }
                repaired.Append("\\\\");
                continue;
            }

            if (ch <= 0x1f)
            {
                repaired.Append(ch switch
                {
                    '\b' => "\\b",
                    '\f' => "\\f",
                    '\n' => "\\n",
                    '\r' => "\\r",
                    '\t' => "\\t",
                    _ => "\\u" + ((int)ch).ToString("x4", CultureInfo.InvariantCulture),
                });
            }
            else
            {
                repaired.Append(ch);
            }
        }

        return repaired.ToString();
    }

    private static bool IsHex4(ReadOnlySpan<char> span)
    {
        foreach (var c in span)
        {
            if (!char.IsAsciiHexDigit(c)) return false;
        }
        return true;
    }

    public static JsonNode? ParseJsonWithRepair(string json)
    {
        try
        {
            return JsonNode.Parse(json, documentOptions: DocOptions);
        }
        catch (JsonException)
        {
            var repaired = RepairJson(json);
            if (repaired != json) return JsonNode.Parse(repaired, documentOptions: DocOptions);
            throw;
        }
    }

    /// <summary>
    /// Attempts to parse potentially incomplete JSON during streaming. Always returns an object
    /// (empty when nothing can be recovered or the value is not an object).
    /// </summary>
    public static JsonObject ParseStreamingJson(string? partialJson)
    {
        if (string.IsNullOrWhiteSpace(partialJson)) return new JsonObject();

        try
        {
            return AsObject(ParseJsonWithRepair(partialJson));
        }
        catch (Exception)
        {
            try
            {
                return AsObject(PartialJson.Parse(partialJson));
            }
            catch (Exception)
            {
                try
                {
                    return AsObject(PartialJson.Parse(RepairJson(partialJson)));
                }
                catch (Exception)
                {
                    return new JsonObject();
                }
            }
        }
    }

    private static JsonObject AsObject(JsonNode? node) => node as JsonObject ?? new JsonObject();
}

/// <summary>
/// Lenient parser for truncated JSON (all partial value kinds allowed):
/// unterminated strings, arrays and objects are closed; trailing incomplete tokens are dropped.
/// </summary>
public static class PartialJson
{
    public static JsonNode? Parse(string json)
    {
        var parser = new Parser(json);
        parser.SkipWhitespace();
        var value = parser.ParseValue();
        return value;
    }

    private sealed class Parser(string s)
    {
        private int _i;

        public void SkipWhitespace()
        {
            while (_i < s.Length && char.IsWhiteSpace(s[_i])) _i++;
        }

        private bool AtEnd => _i >= s.Length;

        public JsonNode? ParseValue()
        {
            SkipWhitespace();
            if (AtEnd) throw new FormatException("Unexpected end of input");
            var c = s[_i];
            switch (c)
            {
                case '"':
                    return JsonValue.Create(ParseString());
                case '{':
                    return ParseObject();
                case '[':
                    return ParseArray();
                case 't':
                    return ParseLiteral("true", JsonValue.Create(true));
                case 'f':
                    return ParseLiteral("false", JsonValue.Create(false));
                case 'n':
                    return ParseLiteral("null", null);
                default:
                    if (c == '-' || char.IsAsciiDigit(c)) return ParseNumber();
                    throw new FormatException($"Unexpected token '{c}' at {_i}");
            }
        }

        private JsonNode? ParseLiteral(string literal, JsonNode? value)
        {
            var remaining = s.Length - _i;
            if (remaining >= literal.Length)
            {
                if (string.CompareOrdinal(s, _i, literal, 0, literal.Length) != 0)
                    throw new FormatException($"Unexpected token at {_i}");
                _i += literal.Length;
                return value;
            }
            // Partial literal at end of input.
            if (string.CompareOrdinal(s, _i, literal, 0, remaining) == 0)
            {
                _i = s.Length;
                return value;
            }
            throw new FormatException($"Unexpected token at {_i}");
        }

        private JsonNode ParseNumber()
        {
            var start = _i;
            if (s[_i] == '-') _i++;
            while (_i < s.Length && (char.IsAsciiDigit(s[_i]) || s[_i] is '.' or 'e' or 'E' or '+' or '-')) _i++;
            var text = s[start.._i];
            // Trim incomplete tails like "1." or "1e" or "-".
            while (text.Length > 0)
            {
                if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                {
                    if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l))
                        return JsonValue.Create(l);
                    return JsonValue.Create(d);
                }
                text = text[..^1];
            }
            if (AtEnd) throw new PartialEndException();
            throw new FormatException($"Invalid number at {start}");
        }

        private string ParseString()
        {
            _i++; // opening quote
            var sb = new StringBuilder();
            while (_i < s.Length)
            {
                var c = s[_i];
                if (c == '"')
                {
                    _i++;
                    return sb.ToString();
                }
                if (c == '\\')
                {
                    if (_i + 1 >= s.Length)
                    {
                        _i = s.Length;
                        return sb.ToString();
                    }
                    var e = s[_i + 1];
                    switch (e)
                    {
                        case '"': sb.Append('"'); _i += 2; break;
                        case '\\': sb.Append('\\'); _i += 2; break;
                        case '/': sb.Append('/'); _i += 2; break;
                        case 'b': sb.Append('\b'); _i += 2; break;
                        case 'f': sb.Append('\f'); _i += 2; break;
                        case 'n': sb.Append('\n'); _i += 2; break;
                        case 'r': sb.Append('\r'); _i += 2; break;
                        case 't': sb.Append('\t'); _i += 2; break;
                        case 'u':
                            if (_i + 6 <= s.Length)
                            {
                                var hex = s.Substring(_i + 2, 4);
                                if (!int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code))
                                    throw new FormatException("Invalid unicode escape");
                                sb.Append((char)code);
                                _i += 6;
                            }
                            else
                            {
                                _i = s.Length;
                                return sb.ToString();
                            }
                            break;
                        default:
                            throw new FormatException("Invalid escape");
                    }
                    continue;
                }
                sb.Append(c);
                _i++;
            }
            return sb.ToString();
        }

        private JsonArray ParseArray()
        {
            _i++; // [
            var array = new JsonArray();
            while (true)
            {
                SkipWhitespace();
                if (AtEnd) return array;
                if (s[_i] == ']')
                {
                    _i++;
                    return array;
                }
                try
                {
                    array.Add(ParseValue());
                }
                catch (PartialEndException)
                {
                    return array;
                }
                catch (FormatException) when (AtEnd)
                {
                    return array;
                }
                SkipWhitespace();
                if (AtEnd) return array;
                if (s[_i] == ',')
                {
                    _i++;
                    continue;
                }
                if (s[_i] == ']')
                {
                    _i++;
                    return array;
                }
                throw new FormatException($"Expected ',' or ']' at {_i}");
            }
        }

        private JsonObject ParseObject()
        {
            _i++; // {
            var obj = new JsonObject();
            while (true)
            {
                SkipWhitespace();
                if (AtEnd) return obj;
                if (s[_i] == '}')
                {
                    _i++;
                    return obj;
                }
                if (s[_i] != '"') throw new FormatException($"Expected string key at {_i}");
                var keyStart = _i;
                var key = ParseString();
                // Key string was truncated: drop it.
                if (AtEnd && (s[^1] != '"' || _i - keyStart < 2)) return obj;
                SkipWhitespace();
                if (AtEnd) return obj;
                if (s[_i] != ':') throw new FormatException($"Expected ':' at {_i}");
                _i++;
                SkipWhitespace();
                if (AtEnd) return obj;
                try
                {
                    var value = ParseValue();
                    obj[key] = value;
                }
                catch (PartialEndException)
                {
                    return obj;
                }
                catch (FormatException) when (AtEnd)
                {
                    return obj;
                }
                SkipWhitespace();
                if (AtEnd) return obj;
                if (s[_i] == ',')
                {
                    _i++;
                    continue;
                }
                if (s[_i] == '}')
                {
                    _i++;
                    return obj;
                }
                throw new FormatException($"Expected ',' or '}}' at {_i}");
            }
        }
    }

    private sealed class PartialEndException : FormatException;
}
