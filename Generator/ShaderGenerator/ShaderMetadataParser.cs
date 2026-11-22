using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Apotheosis.Generator.AssetPathGenerator;

namespace Apotheosis.Generator.ShaderGenerator;

/// <summary>
/// 面向 <c>netstandard2.0</c> 增量生成器的零外部依赖轻量 JSON 解析器，
/// 用于解析构建期工具产出的 <c>*.shader.meta.json</c> 着色器元数据。
/// </summary>
internal static class ShaderMetadataParser
{
    public static ShaderMetadataRecord? TryParse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            var reader = new JsonReader(json!);
            if (reader.ReadValue() is not Dictionary<string, object?> root)
                return null;

            string modAssetPath = GetString(root, "ModAssetPath");
            if (string.IsNullOrWhiteSpace(modAssetPath))
                return null;

            var parametersBuilder = ImmutableArray.CreateBuilder<ShaderParamDescriptor>();
            if (root.TryGetValue("Parameters", out object? paramsObj) && paramsObj is List<object?> paramList)
            {
                for (int i = 0; i < paramList.Count; i++)
                {
                    if (paramList[i] is not Dictionary<string, object?> p)
                        continue;

                    string name = GetString(p, "Name");
                    if (string.IsNullOrWhiteSpace(name))
                        continue;

                    parametersBuilder.Add(new ShaderParamDescriptor(
                        name: name,
                        semantic: GetString(p, "Semantic"),
                        paramType: GetUInt32(p, "ParamType"),
                        paramClass: GetUInt32(p, "ParamClass"),
                        rows: GetUInt32(p, "Rows"),
                        columns: GetUInt32(p, "Columns"),
                        elements: GetUInt32(p, "Elements")));
                }
            }

            var techsBuilder = ImmutableArray.CreateBuilder<ShaderTechniqueDescriptor>();
            if (root.TryGetValue("Techniques", out object? techsObj) && techsObj is List<object?> techList)
            {
                for (int i = 0; i < techList.Count; i++)
                {
                    if (techList[i] is not Dictionary<string, object?> t)
                        continue;

                    string techName = GetString(t, "Name");
                    var passesBuilder = ImmutableArray.CreateBuilder<string>();
                    if (t.TryGetValue("Passes", out object? passesObj) && passesObj is List<object?> passList)
                    {
                        for (int p = 0; p < passList.Count; p++)
                        {
                            if (passList[p] is string passName)
                                passesBuilder.Add(passName);
                        }
                    }

                    techsBuilder.Add(new ShaderTechniqueDescriptor(
                        name: techName,
                        passes: new EquatableArray<string>(passesBuilder.ToImmutable())));
                }
            }

            return new ShaderMetadataRecord(
                modAssetPath: modAssetPath,
                parameters: new EquatableArray<ShaderParamDescriptor>(parametersBuilder.ToImmutable()),
                techniques: new EquatableArray<ShaderTechniqueDescriptor>(techsBuilder.ToImmutable()));
        }
        catch
        {
            return null;
        }
    }

    private static string GetString(Dictionary<string, object?> dict, string key)
    {
        return dict.TryGetValue(key, out object? val) && val is string s ? s : string.Empty;
    }

    private static uint GetUInt32(Dictionary<string, object?> dict, string key)
    {
        if (!dict.TryGetValue(key, out object? val) || val is null)
            return 0u;

        return val switch
        {
            long l when l >= 0 => (uint)l,
            double d when d >= 0 => (uint)d,
            _ => 0u,
        };
    }

    private struct JsonReader
    {
        private readonly string _text;
        private int _pos;

        public JsonReader(string text)
        {
            _text = text;
            _pos = 0;
        }

        public object? ReadValue()
        {
            SkipWhitespace();
            if (_pos >= _text.Length)
                return null;

            char c = _text[_pos];
            return c switch
            {
                '{' => ReadObject(),
                '[' => ReadArray(),
                '"' => ReadString(),
                't' => ReadLiteral("true", true),
                'f' => ReadLiteral("false", false),
                'n' => ReadLiteral("null", null),
                _ => ReadNumber(),
            };
        }

        private Dictionary<string, object?> ReadObject()
        {
            _pos++; // '{'
            var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
            SkipWhitespace();

            if (_pos < _text.Length && _text[_pos] == '}')
            {
                _pos++;
                return dict;
            }

            while (_pos < _text.Length)
            {
                SkipWhitespace();
                if (_pos >= _text.Length || _text[_pos] != '"')
                    throw new FormatException($"Expected JSON object key at position {_pos}.");

                string key = ReadString();
                SkipWhitespace();
                if (_pos >= _text.Length || _text[_pos] != ':')
                    throw new FormatException($"Expected ':' after JSON key at position {_pos}.");
                _pos++;

                object? value = ReadValue();
                dict[key] = value;

                SkipWhitespace();
                if (_pos < _text.Length && _text[_pos] == ',')
                {
                    _pos++;
                    continue;
                }

                if (_pos < _text.Length && _text[_pos] == '}')
                {
                    _pos++;
                    break;
                }

                throw new FormatException($"Expected ',' or '}}' in JSON object at position {_pos}.");
            }

            return dict;
        }

        private List<object?> ReadArray()
        {
            _pos++; // '['
            var list = new List<object?>();
            SkipWhitespace();

            if (_pos < _text.Length && _text[_pos] == ']')
            {
                _pos++;
                return list;
            }

            while (_pos < _text.Length)
            {
                list.Add(ReadValue());
                SkipWhitespace();
                if (_pos < _text.Length && _text[_pos] == ',')
                {
                    _pos++;
                    continue;
                }

                if (_pos < _text.Length && _text[_pos] == ']')
                {
                    _pos++;
                    break;
                }

                break;
            }

            return list;
        }

        private string ReadString()
        {
            _pos++; // '"'
            var sb = new StringBuilder();
            while (_pos < _text.Length)
            {
                char c = _text[_pos++];
                if (c == '"')
                    break;

                if (c == '\\' && _pos < _text.Length)
                {
                    char esc = _text[_pos++];
                    switch (esc)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'u' when _pos + 4 <= _text.Length:
                            string hex = _text.Substring(_pos, 4);
                            _pos += 4;
                            if (ushort.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ushort code))
                                sb.Append((char)code);
                            break;
                        default:
                            sb.Append(esc);
                            break;
                    }
                }
                else
                {
                    sb.Append(c);
                }
            }

            return sb.ToString();
        }

        private object? ReadLiteral(string literal, object? value)
        {
            if (_pos + literal.Length <= _text.Length
                && string.CompareOrdinal(_text, _pos, literal, 0, literal.Length) == 0)
            {
                _pos += literal.Length;
                return value;
            }

            throw new FormatException($"Invalid JSON literal at position {_pos}.");
        }

        private object ReadNumber()
        {
            int start = _pos;
            bool isFloat = false;
            while (_pos < _text.Length)
            {
                char c = _text[_pos];
                if ((c >= '0' && c <= '9') || c == '-' || c == '+')
                {
                    _pos++;
                }
                else if (c == '.' || c == 'e' || c == 'E')
                {
                    isFloat = true;
                    _pos++;
                }
                else
                {
                    break;
                }
            }

            if (_pos == start)
                throw new FormatException($"Expected JSON number at position {_pos}.");

            string token = _text.Substring(start, _pos - start);
            if (!isFloat && long.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out long l))
                return l;

            if (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
                return d;

            throw new FormatException($"Invalid JSON number '{token}' at position {start}.");
        }

        private void SkipWhitespace()
        {
            while (_pos < _text.Length && char.IsWhiteSpace(_text[_pos]))
                _pos++;
        }
    }
}
