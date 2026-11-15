using System.Globalization;
using System.Text;

namespace MakoKaraGenerator.Rendering.Skia.Effects.Svg;

internal sealed partial class SvgStaticCompiler
{
    private enum CssKind { Ident, Number, Percentage, Hash, String, Space, Colon, Semi, Open, Close, Left, Right, Dot, Comma, Other }
    private sealed record CssToken(CssKind Kind, string Value, bool IdHash = false);
    private sealed record CssRule(CssKind SelectorKind, string Selector, int Specificity, Dictionary<string, string> Declarations);

    private void ReadStyles()
    {
        foreach (var node in _nodes.Where(n => n.Name == "style"))
        {
            _token.ThrowIfCancellationRequested(); Check(Encoding.UTF8.GetByteCount(node.Text) <= _limits.MaximumCssBytes,
                "单份 CSS 字节超过预算。", node, "PackageSvgBudgetExceeded");
            var tokens = TokenizeCss(node.Text, node); int position = 0;
            void White() { while (position < tokens.Count && tokens[position].Kind == CssKind.Space) position++; }
            while (true)
            {
                White(); if (position == tokens.Count) break;
                CssKind selectorKind = CssKind.Ident;
                if (tokens[position].Kind is CssKind.Dot or CssKind.Hash)
                {
                    selectorKind = tokens[position].Kind;
                    if (selectorKind == CssKind.Dot) position++;
                }
                Check(position < tokens.Count && tokens[position].Kind == (selectorKind == CssKind.Hash ? CssKind.Hash : CssKind.Ident),
                    "CSS 只允许单 type、ID 或 class selector。", node, "PackageSvgCssUnsupported");
                if (selectorKind == CssKind.Hash) Check(tokens[position].IdHash, "CSS ID selector 必须使用合法标识符或 escape。", node, "PackageSvgCssUnsupported");
                string selector = tokens[position++].Value; Check(ValidId(selector), "CSS selector 标识符无效。", node);
                if (selectorKind == CssKind.Ident) Check(ElementAttributes.ContainsKey(selector), "CSS type selector 不属于静态元素。", node);
                White(); Check(position < tokens.Count && tokens[position++].Kind == CssKind.Open,
                    "禁止 selector list、combinator、伪类和属性 selector。", node, "PackageSvgCssUnsupported");
                int start = position; while (position < tokens.Count && tokens[position].Kind != CssKind.Close) position++;
                Check(position < tokens.Count, "CSS rule 未闭合。", node);
                var declarations = ReadDeclarations(tokens.GetRange(start, position - start), node); position++;
                _rules.Add(new(selectorKind, selector, selectorKind == CssKind.Hash ? 100 : selectorKind == CssKind.Dot ? 10 : 1, declarations));
                Check(_rules.Count <= _limits.MaximumCssRules, "CSS rule 超过预算。", node, "PackageSvgBudgetExceeded");
            }
        }
    }

    private Dictionary<string, string> ReadDeclarations(List<CssToken> tokens, Node node)
    {
        Dictionary<string, string> result = new(StringComparer.Ordinal); int position = 0;
        while (position < tokens.Count)
        {
            while (position < tokens.Count && tokens[position].Kind is CssKind.Space or CssKind.Semi) position++;
            if (position == tokens.Count) break;
            Check(tokens[position].Kind == CssKind.Ident, "CSS 声明必须以属性标识符开始。", node, "PackageSvgCssUnsupported");
            string name = tokens[position++].Value.ToLowerInvariant();
            while (position < tokens.Count && tokens[position].Kind == CssKind.Space) position++;
            Check(position < tokens.Count && tokens[position++].Kind == CssKind.Colon, "CSS 声明缺少冒号。", node);
            int start = position, depth = 0;
            while (position < tokens.Count)
            {
                CssKind kind = tokens[position].Kind;
                if (kind == CssKind.Left) depth++;
                if (kind == CssKind.Right) { depth--; Check(depth >= 0, "CSS 括号无配对。", node); }
                if (kind == CssKind.Semi && depth == 0) break;
                Check(kind is not (CssKind.Open or CssKind.Close), "CSS 声明不允许嵌套。", node);
                position++;
            }
            Check(depth == 0, "CSS 函数未闭合。", node);
            string value = CanonicalCss(tokens.GetRange(start, position - start));
            result[name] = ValidateStyleValue(name, value, node); _cssDeclarations++;
            Check(_cssDeclarations <= _limits.MaximumCssDeclarations, "CSS declaration 超过预算。", node, "PackageSvgBudgetExceeded");
        }
        return result;
    }

    private void ApplyStyles(Node node)
    {
        // Native SVG inheritance remains intact, including the local use shadow tree.
        Dictionary<string, int> specificity = node.Styles.Keys.ToDictionary(k => k, _ => 0, StringComparer.Ordinal);
        foreach (var rule in _rules)
        {
            _token.ThrowIfCancellationRequested(); _selectorSteps++;
            Check(_selectorSteps <= _limits.MaximumSelectorSteps, "CSS selector 求值超过预算。", node, "PackageSvgBudgetExceeded");
            bool matches = rule.SelectorKind switch
            {
                CssKind.Hash => node.Get("id") == rule.Selector,
                CssKind.Dot => node.Get("class").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Contains(rule.Selector, StringComparer.Ordinal),
                _ => node.Name == rule.Selector,
            };
            if (!matches) continue;
            foreach (var declaration in rule.Declarations)
                if (rule.Specificity >= specificity.GetValueOrDefault(declaration.Key, -1))
                { node.Styles[declaration.Key] = declaration.Value; specificity[declaration.Key] = rule.Specificity; }
        }
        if (node.Has("style"))
        {
            string style = node.Get("style"); Check(Encoding.UTF8.GetByteCount(style) <= _limits.MaximumCssBytes, "style 字节超过预算。", node, "PackageSvgBudgetExceeded");
            foreach (var declaration in ReadDeclarations(TokenizeCss(style, node), node)) node.Styles[declaration.Key] = declaration.Value;
        }
        foreach (var child in node.Children) ApplyStyles(child);
        if (node.Parent is null) _usage.Css(_sourcePath, _rules.Count, _cssDeclarations, _selectorSteps);
    }

    private string ValidateStyleValue(string name, string text, Node node)
    {
        Check(Presentation.Contains(name), "CSS 属性未列入静态 profile：" + name, node, "PackageSvgCssUnsupported");
        var tokens = TokenizeCss(text, node).Where(t => t.Kind != CssKind.Space).ToList();
        Check(tokens.Count > 0, "CSS 声明不得为空。", node);
        string Scalar(double minimum = -MaximumNumber, double maximum = MaximumNumber, bool percent = false)
        {
            Check(tokens.Count == 1 && (tokens[0].Kind == CssKind.Number || percent && tokens[0].Kind == CssKind.Percentage), "CSS 数值带未知单位或尾串。", node);
            return F(Number(tokens[0].Value + (tokens[0].Kind == CssKind.Percentage ? "%" : ""), node, minimum, maximum, percent));
        }
        string Keyword(params string[] allowed)
        {
            Check(tokens.Count == 1 && tokens[0].Kind == CssKind.Ident && allowed.Contains(tokens[0].Value.ToLowerInvariant(), StringComparer.Ordinal), "CSS 静态枚举无效。", node);
            return tokens[0].Value.ToLowerInvariant();
        }
        switch (name)
        {
            case "fill": case "stroke":
                if (tokens[0].Kind == CssKind.Ident && tokens[0].Value.Equals("none", StringComparison.OrdinalIgnoreCase)) return Keyword("none");
                if (tokens[0].Kind == CssKind.Ident && tokens[0].Value.Equals("url", StringComparison.OrdinalIgnoreCase)) return "url(#" + ReadLocalUrl(tokens, node) + ")";
                return ReadColor(tokens, node);
            case "stop-color": return ReadColor(tokens, node);
            case "clip-path": case "filter":
                if (tokens.Count == 1 && tokens[0].Kind == CssKind.Ident) return Keyword("none");
                return "url(#" + ReadLocalUrl(tokens, node) + ")";
            case "fill-rule": case "clip-rule": return Keyword("nonzero", "evenodd");
            case "stroke-linecap": return Keyword("butt", "round", "square");
            case "stroke-linejoin": return Keyword("miter", "round", "bevel");
            case "display": return Keyword("inline", "none");
            case "visibility": return Keyword("visible", "hidden", "collapse");
            case "color-interpolation-filters":
                Check(tokens.Count == 1 && tokens[0].Kind == CssKind.Ident && tokens[0].Value.Equals("sRGB", StringComparison.OrdinalIgnoreCase), "filter 颜色空间固定为 sRGB。", node);
                return "sRGB";
            case "opacity": case "fill-opacity": case "stroke-opacity": case "stop-opacity": return Scalar(0, 1, true);
            case "stroke-width": return Scalar(0);
            case "stroke-miterlimit": return Scalar(1, 1000);
            case "stroke-dashoffset": return Scalar();
            case "stroke-dasharray":
                if (tokens.Count == 1 && tokens[0].Kind == CssKind.Ident) return Keyword("none");
                double[] values = Numbers(CanonicalCss(tokens), node, 1, 256);
                Check(values.All(v => v >= 0) && values.Any(v => v > 0), "dash 列表不得为负或全零。", node);
                return string.Join(' ', values.Select(F));
            default: throw Fail("PackageSvgCssUnsupported", "CSS 属性未实现。", node);
        }
    }

    private string ReadLocalUrl(List<CssToken> tokens, Node node)
    {
        Check(tokens.Count == 4 && tokens[0].Kind == CssKind.Ident && tokens[0].Value.Equals("url", StringComparison.OrdinalIgnoreCase)
            && tokens[1].Kind == CssKind.Left && tokens[3].Kind == CssKind.Right, "paint/reference 必须为单个完整 local url()，不支持 fallback。", node, "PackageSvgReferenceUnsafe");
        string id = tokens[2].Kind == CssKind.Hash ? tokens[2].Value
            : tokens[2].Kind == CssKind.String && tokens[2].Value.StartsWith('#') ? tokens[2].Value[1..] : "";
        Check(ValidId(id), "禁止外部、动态或歧义 URI。", node, "PackageSvgReferenceUnsafe"); return id;
    }
    private string ReadColor(List<CssToken> tokens, Node node)
    {
        if (tokens.Count == 1 && tokens[0].Kind == CssKind.Hash)
        {
            string hex = tokens[0].Value;
            Check(hex.Length is 3 or 6 && hex.All(Uri.IsHexDigit), "静态颜色只接受 RGB 的 3/6 位十六进制。", node);
            if (hex.Length == 3) hex = string.Concat(hex.SelectMany(c => new[] { c, c }));
            return "#" + hex.ToLowerInvariant();
        }
        if (tokens.Count == 1 && tokens[0].Kind == CssKind.Ident)
        {
            string value = tokens[0].Value.ToLowerInvariant();
            var named = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["black"] = "#000000", ["silver"] = "#c0c0c0", ["gray"] = "#808080", ["grey"] = "#808080",
                ["white"] = "#ffffff", ["maroon"] = "#800000", ["red"] = "#ff0000", ["purple"] = "#800080",
                ["fuchsia"] = "#ff00ff", ["green"] = "#008000", ["lime"] = "#00ff00", ["olive"] = "#808000",
                ["yellow"] = "#ffff00", ["navy"] = "#000080", ["blue"] = "#0000ff", ["teal"] = "#008080",
                ["aqua"] = "#00ffff", ["orange"] = "#ffa500", ["transparent"] = "rgba(0,0,0,0)",
            };
            Check(named.ContainsKey(value), "颜色名字不属于明确静态 sRGB 表；可使用 #RRGGBB。", node);
            return named[value];
        }
        Check(tokens.Count >= 3 && tokens[0].Kind == CssKind.Ident && tokens[1].Kind == CssKind.Left && tokens[^1].Kind == CssKind.Right,
            "颜色 grammar 无效。", node);
        string function = tokens[0].Value.ToLowerInvariant(); int expected = function == "rgb" ? 3 : function == "rgba" ? 4 : 0;
        Check(expected > 0 && tokens.Count == expected * 2 + 2, "只支持完整逗号 rgb()/rgba()。", node);
        double[] channels = new double[expected];
        for (int i = 0; i < expected; i++)
        {
            CssToken value = tokens[2 + i * 2]; Check(value.Kind is CssKind.Number or CssKind.Percentage, "颜色分量 grammar 无效。", node);
            bool alpha = i == 3;
            channels[i] = Number(value.Value + (value.Kind == CssKind.Percentage ? "%" : ""), node, 0,
                value.Kind == CssKind.Percentage || alpha ? 1 : 255, value.Kind == CssKind.Percentage);
            if (i < 3 && value.Kind == CssKind.Percentage) channels[i] *= 255;
            if (i + 1 < expected) Check(tokens[3 + i * 2].Kind == CssKind.Comma, "颜色分量必须以逗号分隔。", node);
        }
        return function + "(" + string.Join(',', channels.Select(F)) + ")";
    }

    private List<CssToken> TokenizeCss(string text, Node node)
    {
        _usage.Prepare(checked((long)text.Length * 64), _sourcePath);
        List<CssToken> result = []; int position = 0;
        bool NameStart(char c) => char.IsAsciiLetter(c) || c is '_' or '-' or '\\';
        bool NamePart(char c) => NameStart(c) || char.IsAsciiDigit(c);
        bool NumberStart(int p) => p < text.Length && (char.IsAsciiDigit(text[p]) || text[p] == '.' && p + 1 < text.Length && char.IsAsciiDigit(text[p + 1])
            || text[p] is '+' or '-' && p + 1 < text.Length && (char.IsAsciiDigit(text[p + 1]) || text[p + 1] == '.' && p + 2 < text.Length && char.IsAsciiDigit(text[p + 2])));
        string Escape()
        {
            Check(position < text.Length && text[position] == '\\', "CSS escape 无效。", node); position++;
            Check(position < text.Length && text[position] is not ('\r' or '\n' or '\f'), "CSS escape 不完整。", node);
            int start = position, digits = 0;
            while (position < text.Length && Uri.IsHexDigit(text[position]) && digits < 6) { position++; digits++; }
            if (digits == 0) return text[position++].ToString();
            int value = int.Parse(text.AsSpan(start, digits), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            Check(value > 0 && value <= 0x10ffff && value is not (>= 0xd800 and <= 0xdfff), "CSS escape codepoint 无效。", node);
            if (position < text.Length && IsWhite(text[position])) { char white = text[position++]; if (white == '\r' && position < text.Length && text[position] == '\n') position++; }
            return char.ConvertFromUtf32(value);
        }
        string Name()
        {
            StringBuilder name = new();
            while (position < text.Length && NamePart(text[position]))
            { if (text[position] == '\\') name.Append(Escape()); else name.Append(text[position++]); }
            return name.ToString();
        }
        while (position < text.Length)
        {
            _token.ThrowIfCancellationRequested(); char c = text[position];
            if (IsWhite(c)) { while (position < text.Length && IsWhite(text[position])) position++; result.Add(new(CssKind.Space, " ")); continue; }
            if (c == '/' && position + 1 < text.Length && text[position + 1] == '*')
            {
                position += 2; bool closed = false;
                while (position + 1 < text.Length) { if (text[position] == '*' && text[position + 1] == '/') { position += 2; closed = true; break; } position++; }
                Check(closed, "CSS comment 未闭合。", node); continue;
            }
            if (c is '\'' or '"')
            {
                char quote = c; position++; StringBuilder value = new(); bool closed = false;
                while (position < text.Length)
                {
                    c = text[position]; if (c == quote) { position++; closed = true; break; }
                    Check(c is not ('\n' or '\r' or '\f' or '\0'), "CSS string 中不允许换行或 NUL。", node);
                    if (c == '\\') value.Append(Escape()); else { value.Append(c); position++; }
                }
                Check(closed, "CSS string 未闭合。", node); result.Add(new(CssKind.String, value.ToString())); continue;
            }
            if (NumberStart(position))
            {
                int start = position; if (text[position] is '+' or '-') position++;
                while (position < text.Length && char.IsAsciiDigit(text[position])) position++;
                if (position < text.Length && text[position] == '.') { position++; while (position < text.Length && char.IsAsciiDigit(text[position])) position++; }
                if (position < text.Length && text[position] is 'e' or 'E')
                { position++; if (position < text.Length && text[position] is '+' or '-') position++; int digits = position; while (position < text.Length && char.IsAsciiDigit(text[position])) position++; Check(position > digits, "CSS 数值指数无效。", node); }
                string value = text[start..position]; bool percent = position < text.Length && text[position] == '%'; if (percent) position++;
                result.Add(new(percent ? CssKind.Percentage : CssKind.Number, value)); continue;
            }
            if (c == '#') { position++; bool idHash = position < text.Length && NameStart(text[position]); string value = Name(); Check(value.Length > 0, "CSS hash 为空。", node); result.Add(new(CssKind.Hash, value, idHash)); continue; }
            if (NameStart(c)) { result.Add(new(CssKind.Ident, Name())); continue; }
            position++;
            CssKind kind = c switch { ':' => CssKind.Colon, ';' => CssKind.Semi, '{' => CssKind.Open, '}' => CssKind.Close,
                '(' => CssKind.Left, ')' => CssKind.Right, '.' => CssKind.Dot, ',' => CssKind.Comma, _ => CssKind.Other };
            Check(kind != CssKind.Other, "CSS token 未列入静态 profile（包括 at-rule/!important/动态语义）。", node, "PackageSvgCssUnsupported");
            result.Add(new(kind, c.ToString()));
        }
        return result;
    }
    private static string CanonicalCss(IEnumerable<CssToken> tokens)
        => string.Join(' ', tokens.Where(t => t.Kind != CssKind.Space).Select(t => t.Kind switch
        { CssKind.Hash => "#" + t.Value, CssKind.String => "\"" + t.Value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"", CssKind.Percentage => t.Value + "%", _ => t.Value }));
}
