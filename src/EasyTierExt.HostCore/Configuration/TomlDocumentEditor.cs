using System.Globalization;
using System.Text;

namespace EasyTierExt.HostCore.Configuration;

/// <summary>
/// 轻量 TOML 文本编辑器：只定位表头与键值的文本区间并原地改写，
/// 从而保留用户手写的注释、排版以及本客户端不认识的字段。
/// 完整的语法校验由 easytier-core --check-config 负责。
/// </summary>
internal sealed class TomlDocumentEditor
{
    private string _text;
    private List<Section> _sections = [];

    public TomlDocumentEditor(string? text)
    {
        _text = Normalize(text);
        Parse();
    }

    public string Text => _text;

    public static string Normalize(string? text) =>
        (text ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n');

    public static string Quote(string value)
    {
        var sb = new StringBuilder(value.Length + 2).Append('"');
        foreach (var c in value)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20 || c == 0x7F) sb.Append("\\u").Append(((int)c).ToString("X4"));
                    else sb.Append(c);
                    break;
            }
        }
        return sb.Append('"').ToString();
    }

    public string? GetString(string section, string key)
    {
        var entry = Find(section, key);
        return entry is null ? null : ReadString(entry);
    }

    public bool GetBool(string section, string key, bool defaultValue = false)
    {
        var entry = Find(section, key);
        if (entry is null) return defaultValue;
        return _text[entry.ValueStart..entry.ValueEnd] switch
        {
            "true" => true,
            "false" => false,
            _ => throw Error(entry.ValueStart, $"{key} 应为 true 或 false")
        };
    }

    public bool Contains(string section, string key) => Find(section, key) is not null;

    /// <summary>读取字符串数组（如 listeners）；键不存在时返回 null。只提取数组中的字符串字面量。</summary>
    public List<string>? GetStringArray(string section, string key)
    {
        var entry = Find(section, key);
        if (entry is null) return null;
        return System.Text.RegularExpressions.Regex.Matches(_text[entry.ValueStart..entry.ValueEnd], @"""((?:[^""\\]|\\.)*)""|'([^']*)'")
            .Select(m => m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value)
            .ToList();
    }

    /// <summary>写入键值；section 为空串表示顶层。</summary>
    public void Set(string section, string key, string rawValue)
    {
        var existing = Find(section, key);
        if (existing is not null)
        {
            Replace(existing.ValueStart, existing.ValueEnd, rawValue);
            return;
        }

        var line = $"{key} = {rawValue}\n";
        var target = _sections.FirstOrDefault(s => s.Name == section && !s.IsArray);
        if (target is not null)
        {
            InsertLine(target.ContentEnd, line);
        }
        else
        {
            AppendBlock($"[{section}]\n{line}");
        }
    }

    public void Remove(string section, string key)
    {
        var entry = Find(section, key);
        if (entry is not null) Replace(entry.LineStart, entry.LineEnd, string.Empty);
    }

    public List<string> GetArrayTableStrings(string section, string key) =>
        _sections.Where(s => s.IsArray && s.Name == section)
            .Select(s => ReadArrayTableString(s, key))
            .Where(v => v.Length > 0)
            .ToList();

    /// <summary>使全部 [[section]] 的 key 取值与 desired 一致；仍需保留的表原样不动。</summary>
    public void SyncArrayTableStrings(string section, string key, IReadOnlyCollection<string> desired)
    {
        // 从后往前删除，前面区块的下标不受影响（每次改写都会重新解析）。
        for (int i = _sections.Count - 1; i >= 0; i--)
        {
            var s = _sections[i];
            if (!s.IsArray || s.Name != section || desired.Contains(ReadArrayTableString(s, key))) continue;

            int end = s.ContentEnd;
            while (end < _text.Length && _text[end] == '\n') end++;
            Replace(s.HeaderStart, end, string.Empty);
        }

        var existing = GetArrayTableStrings(section, key);
        foreach (var item in desired.Where(d => !existing.Contains(d)).ToList())
        {
            var block = $"[[{section}]]\n{key} = {Quote(item)}\n";
            var last = _sections.LastOrDefault(s => s.IsArray && s.Name == section);
            if (last is not null)
            {
                InsertLine(last.ContentEnd, "\n" + block);
            }
            else
            {
                AppendBlock(block);
            }
        }
    }

    private string ReadArrayTableString(Section s, string key)
    {
        var entry = s.Entries.FirstOrDefault(e => e.Key == key);
        return entry is null ? string.Empty : ReadString(entry).Trim();
    }

    private string ReadString(Entry entry)
    {
        var raw = _text[entry.ValueStart..entry.ValueEnd];
        if (raw[0] is not ('"' or '\''))
        {
            throw Error(entry.ValueStart, $"{entry.Key} 应为带引号的字符串");
        }
        return DecodeString(raw, entry.ValueStart);
    }

    private Entry? Find(string section, string key)
    {
        var full = section.Length == 0 ? key : $"{section}.{key}";
        foreach (var s in _sections.Where(s => !s.IsArray))
        {
            foreach (var e in s.Entries)
            {
                if ((s.Name.Length == 0 ? e.Key : $"{s.Name}.{e.Key}") == full) return e;
            }
        }
        return null;
    }

    private void InsertLine(int position, string line)
    {
        if (position > 0 && _text[position - 1] != '\n') line = "\n" + line;
        Replace(position, position, line);
    }

    private void AppendBlock(string block)
    {
        var prefix = _text.Length == 0 || _text.EndsWith("\n\n", StringComparison.Ordinal)
            ? string.Empty
            : _text.EndsWith('\n') ? "\n" : "\n\n";
        Replace(_text.Length, _text.Length, prefix + block);
    }

    private void Replace(int start, int end, string value)
    {
        _text = string.Concat(_text.AsSpan(0, start), value, _text.AsSpan(end));
        Parse();
    }

    #region Scanner

    private void Parse()
    {
        var current = new Section();
        var sections = new List<Section> { current };

        int pos = 0;
        while (true)
        {
            while (pos < _text.Length && _text[pos] is ' ' or '\t' or '\n') pos++;
            if (pos >= _text.Length) break;
            if (_text[pos] == '#')
            {
                pos = EndOfLine(pos);
                continue;
            }

            int lineStart = pos == 0 ? 0 : _text.LastIndexOf('\n', pos - 1) + 1;
            if (_text[pos] == '[')
            {
                bool isArray = pos + 1 < _text.Length && _text[pos + 1] == '[';
                pos += isArray ? 2 : 1;
                var name = ParseKeyPath(ref pos);
                Expect(ref pos, ']');
                if (isArray) Expect(ref pos, ']');
                pos = FinishLine(pos);

                // [[a]] 的子表 [a.b] 可以合法地重复出现
                if (!isArray && sections.Any(s => !s.IsArray && s.Name == name) &&
                    !sections.Any(s => s.IsArray && name.StartsWith(s.Name + ".", StringComparison.Ordinal)))
                {
                    throw Error(lineStart, $"重复的表 [{name}]");
                }
                current = new Section { Name = name, IsArray = isArray, HeaderStart = lineStart, ContentEnd = pos };
                sections.Add(current);
                continue;
            }

            var key = ParseKeyPath(ref pos);
            Expect(ref pos, '=');
            SkipInlineSpace(ref pos);
            int valueStart = pos;
            SkipValue(ref pos);
            int valueEnd = pos;
            pos = FinishLine(pos);

            if (current.Entries.Any(e => e.Key == key))
            {
                throw Error(lineStart, $"重复的键 {key}");
            }
            if (current.Name.Length == 0 && key is "network_identity" or "flags" or "peer")
            {
                var header = key == "peer" ? "[[peer]]" : $"[{key}]";
                throw Error(lineStart, $"请改用 {header} 表写法，不支持内联写法");
            }
            current.Entries.Add(new Entry(key, lineStart, pos, valueStart, valueEnd));
            current.ContentEnd = pos;
        }

        _sections = sections;
    }

    private string ParseKeyPath(ref int pos)
    {
        var parts = new List<string>();
        while (true)
        {
            SkipInlineSpace(ref pos);
            int start = pos;
            if (pos < _text.Length && _text[pos] is '"' or '\'')
            {
                SkipString(ref pos);
                parts.Add(DecodeString(_text[start..pos], start));
            }
            else
            {
                while (pos < _text.Length && (char.IsAsciiLetterOrDigit(_text[pos]) || _text[pos] is '_' or '-')) pos++;
                if (pos == start) throw Error(pos, "键名无效");
                parts.Add(_text[start..pos]);
            }
            SkipInlineSpace(ref pos);
            if (pos >= _text.Length || _text[pos] != '.') return string.Join('.', parts);
            pos++;
        }
    }

    private void SkipValue(ref int pos)
    {
        if (pos >= _text.Length || _text[pos] is '\n' or '#') throw Error(pos, "缺少值");
        if (_text[pos] is '"' or '\'')
        {
            SkipString(ref pos);
            return;
        }

        int start = pos;
        if (_text[pos] is '[' or '{')
        {
            char close = _text[pos] == '[' ? ']' : '}';
            pos++;
            while (true)
            {
                if (pos >= _text.Length) throw Error(start, $"缺少 '{close}'");
                char c = _text[pos];
                if (c == close)
                {
                    pos++;
                    return;
                }
                if (c is '"' or '\'') SkipString(ref pos);
                else if (c is '[' or '{') SkipValue(ref pos);
                else if (c is ']' or '}') throw Error(pos, "括号不匹配");
                else if (c == '#') pos = EndOfLine(pos);
                else pos++;
            }
        }

        while (pos < _text.Length && _text[pos] is not ('\n' or '#')) pos++;
        while (pos > start && _text[pos - 1] is ' ' or '\t') pos--;
    }

    private void SkipString(ref int pos)
    {
        int start = pos;
        char q = _text[pos];
        bool multi = pos + 2 < _text.Length && _text[pos + 1] == q && _text[pos + 2] == q;
        pos += multi ? 3 : 1;
        while (pos < _text.Length)
        {
            char c = _text[pos];
            if (c == '\n' && !multi) break;
            if (c == '\\' && q == '"')
            {
                pos += 2;
                continue;
            }
            if (c != q)
            {
                pos++;
                continue;
            }
            if (!multi)
            {
                pos++;
                return;
            }
            int run = 0;
            while (pos + run < _text.Length && _text[pos + run] == q) run++;
            pos += run;
            if (run >= 3) return;
        }
        throw Error(start, "字符串未闭合");
    }

    private string DecodeString(string raw, int position)
    {
        char q = raw[0];
        bool multi = raw.Length >= 6 && raw[1] == q && raw[2] == q;
        var body = multi ? raw[3..^3] : raw[1..^1];
        if (multi && body.StartsWith('\n')) body = body[1..];
        if (q == '\'') return body;

        var sb = new StringBuilder(body.Length);
        for (int i = 0; i < body.Length; i++)
        {
            if (body[i] != '\\' || i + 1 >= body.Length)
            {
                sb.Append(body[i]);
                continue;
            }
            char e = body[++i];
            switch (e)
            {
                case 'n': sb.Append('\n'); break;
                case 't': sb.Append('\t'); break;
                case 'r': sb.Append('\r'); break;
                case 'b': sb.Append('\b'); break;
                case 'f': sb.Append('\f'); break;
                case '"': sb.Append('"'); break;
                case '\\': sb.Append('\\'); break;
                case 'u' or 'U':
                    int len = e == 'u' ? 4 : 8;
                    if (i + len >= body.Length ||
                        !int.TryParse(body.AsSpan(i + 1, len), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out int cp) ||
                        cp is < 0 or > 0x10FFFF or (>= 0xD800 and <= 0xDFFF))
                    {
                        throw Error(position, "字符串包含无效的 Unicode 转义");
                    }
                    sb.Append(char.ConvertFromUtf32(cp));
                    i += len;
                    break;
                case '\n' or ' ' or '\t' when multi:
                    while (i + 1 < body.Length && char.IsWhiteSpace(body[i + 1])) i++;
                    break;
                default:
                    throw Error(position, $"字符串包含无效的转义 \\{e}（反斜杠请写成 \\\\）");
            }
        }
        return sb.ToString();
    }

    private void Expect(ref int pos, char c)
    {
        SkipInlineSpace(ref pos);
        if (pos >= _text.Length || _text[pos] != c) throw Error(pos, $"缺少 '{c}'");
        pos++;
    }

    private int FinishLine(int pos)
    {
        SkipInlineSpace(ref pos);
        if (pos < _text.Length && _text[pos] == '#') pos = EndOfLine(pos);
        if (pos >= _text.Length) return pos;
        if (_text[pos] != '\n') throw Error(pos, "行尾有多余内容");
        return pos + 1;
    }

    private void SkipInlineSpace(ref int pos)
    {
        while (pos < _text.Length && _text[pos] is ' ' or '\t') pos++;
    }

    private int EndOfLine(int pos)
    {
        int idx = _text.IndexOf('\n', pos);
        return idx < 0 ? _text.Length : idx;
    }

    private FormatException Error(int pos, string message)
    {
        int line = _text.AsSpan(0, Math.Min(pos, _text.Length)).Count('\n') + 1;
        return new FormatException($"第 {line} 行：{message}");
    }

    #endregion

    private sealed record Entry(string Key, int LineStart, int LineEnd, int ValueStart, int ValueEnd);

    private sealed class Section
    {
        public string Name { get; init; } = string.Empty;
        public bool IsArray { get; init; }
        public int HeaderStart { get; init; }
        public int ContentEnd { get; set; }
        public List<Entry> Entries { get; } = [];
    }
}
