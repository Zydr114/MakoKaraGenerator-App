using System.Globalization;

namespace MakoKaraGenerator.Rendering.Skia.Effects.Svg;

internal sealed partial class SvgStaticCompiler
{
    private void ReadPath(Node node)
    {
        string data = node.Get("d"); Check(data.Length > 0, "path 必须包含非空 d。", node);
        var cursor = new NumberCursor(this, node, data);
        char command = '\0'; bool first = true; double x = 0, y = 0, startX = 0, startY = 0;
        char previous = '\0'; double cubicX = 0, cubicY = 0, quadraticX = 0, quadraticY = 0;
        while (!cursor.End)
        {
            _token.ThrowIfCancellationRequested();
            if (cursor.IsLetter)
            {
                command = cursor.Letter();
                Check("MmLlHhVvCcSsQqTtAaZz".Contains(command), "未知 path command。", node);
                Check(!first || command is 'M' or 'm', "path 必须从 moveto 开始。", node);
                first = false;
                if (command is 'Z' or 'z')
                {
                    CountPath(node); x = startX; y = startY; command = '\0'; previous = 'Z'; continue;
                }
            }
            else Check(command != '\0', "path 缺少 command 或 closepath 后隐式追加数值。", node);
            bool relative = char.IsLower(command); char kind = char.ToUpperInvariant(command);
            int count = kind switch { 'M' or 'L' or 'T' => 2, 'H' or 'V' => 1, 'C' => 6, 'S' or 'Q' => 4, 'A' => 7, _ => 0 };
            Check(count > 0, "path command 的参数无效。", node);
            bool repeated = false;
            do
            {
                double[] values = new double[count];
                for (int i = 0; i < count; i++)
                    values[i] = kind == 'A' && i is 3 or 4
                        ? cursor.Flag(i > 0 || repeated) : cursor.Number(i > 0 || repeated);
                CountPath(node); double oldX = x, oldY = y;
                if (kind == 'H') x = (relative ? x : 0) + values[0];
                else if (kind == 'V') y = (relative ? y : 0) + values[0];
                else
                {
                    if (kind == 'A')
                    {
                        Check(values[0] >= 0 && values[1] >= 0, "arc 半径不得为负。", node);
                        x = values[5] + (relative ? oldX : 0); y = values[6] + (relative ? oldY : 0);
                        // SVG elliptical arcs may scale radii to reach their endpoint. This finite box
                        // reserves a conservative bound for both scaled radii and any axis rotation.
                        double reach = Math.Sqrt((x - oldX) * (x - oldX) + (y - oldY) * (y - oldY)) + values[0] + values[1];
                        node.Geometry = node.Geometry.Include(oldX - reach, oldY - reach).Include(oldX + reach, oldY + reach)
                            .Include(x - reach, y - reach).Include(x + reach, y + reach);
                    }
                    else
                    {
                        if (kind == 'S' && previous is 'C' or 'S')
                            node.Geometry = node.Geometry.Include(2 * oldX - cubicX, 2 * oldY - cubicY);
                        if (kind == 'T' && previous is 'Q' or 'T')
                            node.Geometry = node.Geometry.Include(2 * oldX - quadraticX, 2 * oldY - quadraticY);
                        for (int i = 0; i < values.Length; i += 2)
                            node.Geometry = node.Geometry.Include(values[i] + (relative ? oldX : 0), values[i + 1] + (relative ? oldY : 0));
                        x = values[^2] + (relative ? oldX : 0); y = values[^1] + (relative ? oldY : 0);
                    }
                }
                if (kind is 'C' or 'S')
                { cubicX = values[^4] + (relative ? oldX : 0); cubicY = values[^3] + (relative ? oldY : 0); }
                if (kind == 'Q') { quadraticX = values[0] + (relative ? oldX : 0); quadraticY = values[1] + (relative ? oldY : 0); }
                if (kind == 'T')
                { quadraticX = previous is 'Q' or 'T' ? 2 * oldX - quadraticX : oldX; quadraticY = previous is 'Q' or 'T' ? 2 * oldY - quadraticY : oldY; }
                CheckFinitePoint(x, y, node); node.Geometry = node.Geometry.Include(x, y);
                previous = kind;
                if (kind == 'M') { startX = x; startY = y; kind = 'L'; command = relative ? 'l' : 'L'; }
                repeated = true;
            } while (cursor.HasNumber);
            Check(cursor.End || cursor.IsLetter, "path 存在未消费的参数或尾串。", node);
        }
        Check(!first, "path 必须至少有一个 moveto。", node);
    }

    private void CountPath(Node node)
    {
        node.PathCommands++; _rawCommands++;
        Check(_rawCommands <= _limits.MaximumPathCommands, "原始路径 command 超过预算。", node, "PackageSvgBudgetExceeded");
    }

    private double Number(string value, Node node, double minimum = -MaximumNumber, double maximum = MaximumNumber,
        bool percentage = false)
    {
        bool percent = percentage && value.TrimEnd().EndsWith('%');
        var cursor = new NumberCursor(this, node, percent ? value.TrimEnd()[..^1] : value);
        double result = cursor.Number(false); Check(cursor.End, "数值带有不支持的单位或尾串。", node);
        if (percent) result /= 100;
        Check(result >= minimum && result <= maximum && NativeNumber(result), "数值超出 profile 或 native float 的有限范围。", node);
        return result;
    }

    private double[] Numbers(string value, Node node, int minimum, int maximum)
    {
        var cursor = new NumberCursor(this, node, value); List<double> values = [];
        while (!cursor.End)
        {
            Check(values.Count < maximum, "数值列表超过明确长度。", node);
            values.Add(cursor.Number(values.Count > 0));
        }
        Check(values.Count >= minimum, "数值列表数量不足。", node); return [.. values];
    }

    private Matrix ReadTransform(string text, Node node)
    {
        var cursor = new NumberCursor(this, node, text); Matrix matrix = Matrix.Identity; int transforms = 0;
        while (!cursor.End)
        {
            Check(++transforms <= 64, "单元素 transform 超过 64 项。", node, "PackageSvgBudgetExceeded");
            string name = cursor.Identifier(); cursor.Expect('('); List<double> values = [];
            while (!cursor.At(')'))
            { Check(values.Count < 6, "transform 参数数量无效。", node); values.Add(cursor.Number(values.Count > 0, values.Count > 0)); }
            cursor.Expect(')'); double[] v = [.. values];
            Matrix next = name switch
            {
                "matrix" when v.Length == 6 => new(v[0], v[1], v[2], v[3], v[4], v[5]),
                "translate" when v.Length is 1 or 2 => new(1, 0, 0, 1, v[0], v.Length == 2 ? v[1] : 0),
                "scale" when v.Length is 1 or 2 => new(v[0], 0, 0, v.Length == 2 ? v[1] : v[0], 0, 0),
                "rotate" when v.Length is 1 or 3 => Rotation(v),
                "skewX" when v.Length == 1 => new(1, 0, Tangent(v[0], node), 1, 0, 0),
                "skewY" when v.Length == 1 => new(1, Tangent(v[0], node), 0, 1, 0, 0),
                _ => throw Fail("PackageSvgSyntaxInvalid", "transform 名称或参数数量无效。", node),
            };
            matrix *= next; CheckMatrix(matrix, node);
            cursor.OptionalTransformComma();
        }
        return matrix;
    }

    private static Matrix Rotation(double[] values)
    {
        double angle = values[0] * Math.PI / 180, sin = Math.Sin(angle), cos = Math.Cos(angle);
        Matrix rotation = new(cos, sin, -sin, cos, 0, 0);
        return values.Length == 1 ? rotation
            : new Matrix(1, 0, 0, 1, values[1], values[2]) * rotation * new Matrix(1, 0, 0, 1, -values[1], -values[2]);
    }
    private double Tangent(double degrees, Node node)
    {
        Check(Math.Abs(Math.Cos(degrees * Math.PI / 180)) > 0.000001, "skew 角度产生无界变换。", node);
        return Math.Tan(degrees * Math.PI / 180);
    }
    private void CheckMatrix(Matrix value, Node node)
    {
        Check(new[] { value.A, value.B, value.C, value.D, value.E, value.F }.All(v => double.IsFinite(v) && Math.Abs(v) <= MaximumNumber && NativeNumber(v)),
            "累计 transform 超出有限或 native float 范围。", node);
    }
    private void CheckFinitePoint(double x, double y, Node node)
        => Check(double.IsFinite(x) && double.IsFinite(y) && Math.Abs(x) <= MaximumNumber && Math.Abs(y) <= MaximumNumber && NativeNumber(x) && NativeNumber(y),
            "坐标超出有限或 native float 范围。", node);
    private static bool NativeNumber(double value) => float.IsFinite((float)value) && (value == 0 || (float)value != 0);

    private sealed class NumberCursor(SvgStaticCompiler owner, Node node, string text)
    {
        private int _position;
        private void White() { while (_position < text.Length && IsWhite(text[_position])) _position++; }
        internal bool End { get { White(); return _position == text.Length; } }
        internal bool IsLetter { get { White(); return _position < text.Length && char.IsAsciiLetter(text[_position]); } }
        internal bool HasNumber
        {
            get
            {
                White(); if (_position == text.Length) return false;
                return text[_position] is '+' or '-' or '.' or ',' or >= '0' and <= '9';
            }
        }
        internal char Letter() { White(); return text[_position++]; }
        internal string Identifier()
        {
            White(); int start = _position;
            while (_position < text.Length && char.IsAsciiLetter(text[_position])) _position++;
            owner.Check(_position > start, "缺少变换名称。", node); return text[start.._position];
        }
        internal bool At(char value)
        {
            // Lookahead must preserve whitespace for Number's required separator check.
            int position = _position;
            while (position < text.Length && IsWhite(text[position])) position++;
            return position < text.Length && text[position] == value;
        }
        internal void Expect(char value)
        { White(); owner.Check(_position < text.Length && text[_position] == value, "语法缺少 " + value + "。", node); _position++; }
        private void Separator(bool comma, bool required = false)
        {
            int previous = _position;
            White();
            if (_position < text.Length && text[_position] == ',')
            { owner.Check(comma, "首个参数不得以逗号开始。", node); _position++; White(); }
            owner.Check(!required || _position > previous, "transform 数值间必须有空白或逗号。", node);
        }
        internal double Flag(bool comma)
        {
            Separator(comma); owner.Check(_position < text.Length && text[_position] is '0' or '1', "arc flag 必须为单个 0 或 1。", node);
            return text[_position++] - '0';
        }
        internal double Number(bool comma, bool separatorRequired = false)
        {
            Separator(comma, separatorRequired); int start = _position;
            if (_position < text.Length && text[_position] is '+' or '-') _position++;
            int digits = 0;
            while (_position < text.Length && char.IsAsciiDigit(text[_position])) { _position++; digits++; }
            if (_position < text.Length && text[_position] == '.')
            { _position++; while (_position < text.Length && char.IsAsciiDigit(text[_position])) { _position++; digits++; } }
            owner.Check(digits > 0, "缺少合法 SVG 数值。", node);
            if (_position < text.Length && text[_position] is 'e' or 'E')
            {
                _position++; if (_position < text.Length && text[_position] is '+' or '-') _position++;
                int exponent = _position;
                while (_position < text.Length && char.IsAsciiDigit(text[_position])) _position++;
                owner.Check(_position > exponent, "数值指数缺少数字。", node);
            }
            owner.Check(double.TryParse(text.AsSpan(start, _position - start), NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
                && double.IsFinite(value) && Math.Abs(value) <= MaximumNumber && NativeNumber(value), "数值溢出或超过有限坐标/native float 预算。", node);
            return value;
        }
        internal void OptionalTransformComma()
        {
            int previous = _position;
            White(); bool separated = _position > previous;
            if (_position < text.Length && text[_position] == ',')
            { _position++; White(); owner.Check(_position < text.Length && char.IsAsciiLetter(text[_position]), "transform 逗号后缺少函数。", node); }
            else owner.Check(_position == text.Length || separated, "transform 函数间必须有空白或逗号。", node);
        }
    }
    private static bool IsWhite(char value) => value is ' ' or '\t' or '\r' or '\n' or '\f';
    private readonly record struct Matrix(double A, double B, double C, double D, double E, double F)
    {
        internal static Matrix Identity => new(1, 0, 0, 1, 0, 0);
        public static Matrix operator *(Matrix x, Matrix y) => new(x.A * y.A + x.C * y.B, x.B * y.A + x.D * y.B,
            x.A * y.C + x.C * y.D, x.B * y.C + x.D * y.D, x.A * y.E + x.C * y.F + x.E, x.B * y.E + x.D * y.F + x.F);
        internal (double X, double Y) Map(double x, double y) => (A * x + C * y + E, B * x + D * y + F);
    }
    private readonly record struct Bounds(double Left, double Top, double Right, double Bottom)
    {
        internal static Bounds Empty => new(double.PositiveInfinity, double.PositiveInfinity, double.NegativeInfinity, double.NegativeInfinity);
        internal bool IsEmpty => Right < Left || Bottom < Top;
        internal Bounds Include(double x, double y) => new(Math.Min(Left, x), Math.Min(Top, y), Math.Max(Right, x), Math.Max(Bottom, y));
        internal Bounds Union(Bounds other) => other.IsEmpty ? this : Include(other.Left, other.Top).Include(other.Right, other.Bottom);
        internal Bounds Map(Matrix matrix)
        {
            if (IsEmpty) return this; Bounds result = Empty;
            foreach (var (x, y) in new[] { (Left, Top), (Right, Top), (Left, Bottom), (Right, Bottom) })
            { var point = matrix.Map(x, y); result = result.Include(point.X, point.Y); }
            return result;
        }
    }
}
