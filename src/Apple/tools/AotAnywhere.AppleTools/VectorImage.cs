using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace AotAnywhere.AppleTools;

/// <summary>
/// Renders the vector images of an asset catalog (PDF and SVG) to bitmaps, as actool does: UIKit
/// finds an image by its bitmap renditions, so a vector image needs 1x, 2x and 3x bitmaps beside
/// its vector data. Covers what icon artwork uses - filled and stroked paths and shapes, transforms,
/// colors and opacity; gradients, text, embedded images and clipping are reported as errors rather
/// than rendered wrongly.
/// </summary>
public static class VectorImage
{
    public sealed class RenderError(string message) : Exception(message);

    /// <summary>A parsed drawing: its size in points, and its painting operations in point space (y down).</summary>
    public sealed class Drawing(double width, double height)
    {
        public double Width { get; } = width;
        public double Height { get; } = height;
        public List<(List<List<(double X, double Y)>> Path, bool EvenOdd, (double R, double G, double B, double A) Color)> Fills { get; } = [];
    }

    public static Drawing Load(byte[] data, string extension) => extension.ToLowerInvariant() switch
    {
        ".svg" => Svg.Parse(data),
        ".pdf" => Pdf.Parse(data),
        var e => throw new RenderError($"'{e}' is not a vector image format"),
    };

    /// <summary>Renders at <paramref name="scale"/> pixels per point.</summary>
    public static Png Render(Drawing drawing, double scale)
    {
        int w = Math.Max(1, (int)Math.Round(drawing.Width * scale)), h = Math.Max(1, (int)Math.Round(drawing.Height * scale));
        var buffer = new double[w * h * 4]; // premultiplied RGBA, 0..1
        var coverage = new double[w * h];
        foreach (var (path, evenOdd, (r, g, b, a)) in drawing.Fills)
        {
            Array.Clear(coverage);
            Rasterize(path.Select(sp => sp.Select(p => (p.X * scale, p.Y * scale)).ToList()).ToList(), evenOdd, w, h, coverage);
            for (int i = 0; i < coverage.Length; i++)
            {
                double c = coverage[i] * a;
                if (c <= 0)
                    continue;
                int o = i * 4;
                buffer[o] = r * c + buffer[o] * (1 - c);
                buffer[o + 1] = g * c + buffer[o + 1] * (1 - c);
                buffer[o + 2] = b * c + buffer[o + 2] * (1 - c);
                buffer[o + 3] = c + buffer[o + 3] * (1 - c);
            }
        }
        var rgba = new byte[w * h * 4];
        for (int i = 0; i < w * h; i++)
        {
            double alpha = buffer[i * 4 + 3];
            for (int k = 0; k < 3; k++)
                rgba[i * 4 + k] = alpha > 0 ? (byte)Math.Clamp(Math.Round(buffer[i * 4 + k] / alpha * 255), 0, 255) : (byte)0;
            rgba[i * 4 + 3] = (byte)Math.Clamp(Math.Round(alpha * 255), 0, 255);
        }
        return Png.FromRgba(w, h, rgba);
    }

    // Scanline coverage: 4 sub-rows per pixel row, exact horizontal coverage of each span.
    const int SubRows = 4;

    static void Rasterize(List<List<(double X, double Y)>> path, bool evenOdd, int w, int h, double[] coverage)
    {
        var edges = new List<(double X0, double Y0, double X1, double Y1, int Dir)>();
        foreach (var sub in path)
            for (int i = 0; i < sub.Count; i++)
            {
                var (x0, y0) = sub[i];
                var (x1, y1) = sub[(i + 1) % sub.Count];
                if (y0 == y1)
                    continue;
                edges.Add(y0 < y1 ? (x0, y0, x1, y1, 1) : (x1, y1, x0, y0, -1));
            }
        if (edges.Count == 0)
            return;
        var crossings = new List<(double X, int Dir)>();
        var row = new double[w + 1];
        for (int py = 0; py < h; py++)
        {
            Array.Clear(row);
            for (int s = 0; s < SubRows; s++)
            {
                double y = py + (s + 0.5) / SubRows;
                crossings.Clear();
                foreach (var (x0, y0, x1, y1, dir) in edges)
                    if (y >= y0 && y < y1)
                        crossings.Add((x0 + (y - y0) / (y1 - y0) * (x1 - x0), dir));
                crossings.Sort((a, b) => a.X.CompareTo(b.X));
                int winding = 0;
                for (int i = 0; i + 1 < crossings.Count; i++)
                {
                    winding += crossings[i].Dir;
                    bool inside = evenOdd ? (i + 1) % 2 == 1 : winding != 0;
                    if (inside)
                        Span(row, crossings[i].X, crossings[i + 1].X, w);
                }
            }
            for (int px = 0; px < w; px++)
                coverage[py * w + px] = Math.Min(1, coverage[py * w + px] + row[px] / SubRows);
        }
    }

    static void Span(double[] row, double x0, double x1, int w)
    {
        x0 = Math.Clamp(x0, 0, w);
        x1 = Math.Clamp(x1, 0, w);
        if (x1 <= x0)
            return;
        int a = (int)x0, b = (int)x1;
        if (a == b)
        {
            row[a] += x1 - x0;
            return;
        }
        row[a] += a + 1 - x0;
        for (int i = a + 1; i < b; i++)
            row[i] += 1;
        if (b < w)
            row[b] += x1 - b;
    }

    // --- geometry shared by the parsers ------------------------------------------------------

    /// <summary>A 2D affine transform (a b c d e f, as in SVG and PDF).</summary>
    public readonly record struct Matrix(double A, double B, double C, double D, double E, double F)
    {
        public static readonly Matrix Identity = new(1, 0, 0, 1, 0, 0);
        public (double X, double Y) Apply(double x, double y) => (A * x + C * y + E, B * x + D * y + F);
        /// <summary>This transform applied after <paramref name="inner"/>.</summary>
        public Matrix Times(Matrix inner) => new(
            A * inner.A + C * inner.B, B * inner.A + D * inner.B,
            A * inner.C + C * inner.D, B * inner.C + D * inner.D,
            A * inner.E + C * inner.F + E, B * inner.E + D * inner.F + F);
        public double Scale => Math.Sqrt(Math.Abs(A * D - B * C));
    }

    /// <summary>Builds subpaths, flattening curves.</summary>
    sealed class PathBuilder(Matrix m)
    {
        public readonly List<List<(double X, double Y)>> Subpaths = [];
        public readonly List<bool> Closed = [];
        List<(double X, double Y)>? current;
        public (double X, double Y) Point, Start;

        public void MoveTo(double x, double y)
        {
            current = [m.Apply(x, y)];
            Subpaths.Add(current);
            Closed.Add(false);
            Point = Start = (x, y);
        }

        public void LineTo(double x, double y)
        {
            if (current is null)
                MoveTo(Point.X, Point.Y);
            current!.Add(m.Apply(x, y));
            Point = (x, y);
        }

        public void CubicTo(double x1, double y1, double x2, double y2, double x, double y)
        {
            var (x0, y0) = Point;
            for (int i = 1; i <= 16; i++)
            {
                double t = i / 16.0, u = 1 - t;
                LineTo(u * u * u * x0 + 3 * u * u * t * x1 + 3 * u * t * t * x2 + t * t * t * x,
                       u * u * u * y0 + 3 * u * u * t * y1 + 3 * u * t * t * y2 + t * t * t * y);
            }
        }

        public void QuadTo(double x1, double y1, double x, double y)
        {
            var (x0, y0) = Point;
            CubicTo(x0 + 2.0 / 3 * (x1 - x0), y0 + 2.0 / 3 * (y1 - y0), x + 2.0 / 3 * (x1 - x), y + 2.0 / 3 * (y1 - y), x, y);
        }

        public void Close()
        {
            if (current is { Count: > 0 })
            {
                Closed[^1] = true;
                Point = Start;
            }
            current = null;
        }

        // SVG elliptical arc, endpoint parameterization (SVG 1.1 appendix F.6).
        public void ArcTo(double rx, double ry, double phiDeg, bool large, bool sweep, double x, double y)
        {
            var (x1, y1) = Point;
            if (rx == 0 || ry == 0) { LineTo(x, y); return; }
            rx = Math.Abs(rx); ry = Math.Abs(ry);
            double phi = phiDeg * Math.PI / 180, cos = Math.Cos(phi), sin = Math.Sin(phi);
            double dx = (x1 - x) / 2, dy = (y1 - y) / 2;
            double x1p = cos * dx + sin * dy, y1p = -sin * dx + cos * dy;
            double lambda = x1p * x1p / (rx * rx) + y1p * y1p / (ry * ry);
            if (lambda > 1) { rx *= Math.Sqrt(lambda); ry *= Math.Sqrt(lambda); }
            double num = rx * rx * ry * ry - rx * rx * y1p * y1p - ry * ry * x1p * x1p;
            double coef = Math.Sqrt(Math.Max(0, num / (rx * rx * y1p * y1p + ry * ry * x1p * x1p))) * (large == sweep ? -1 : 1);
            double cxp = coef * rx * y1p / ry, cyp = -coef * ry * x1p / rx;
            double cx = cos * cxp - sin * cyp + (x1 + x) / 2, cy = sin * cxp + cos * cyp + (y1 + y) / 2;
            double Angle(double ux, double uy, double vx, double vy) => Math.Atan2(ux * vy - uy * vx, ux * vx + uy * vy);
            double theta = Angle(1, 0, (x1p - cxp) / rx, (y1p - cyp) / ry);
            double delta = Angle((x1p - cxp) / rx, (y1p - cyp) / ry, (-x1p - cxp) / rx, (-y1p - cyp) / ry);
            if (!sweep && delta > 0) delta -= 2 * Math.PI;
            else if (sweep && delta < 0) delta += 2 * Math.PI;
            int n = Math.Max(4, (int)Math.Ceiling(Math.Abs(delta) / (Math.PI / 16)));
            for (int i = 1; i <= n; i++)
            {
                double t = theta + delta * i / n;
                LineTo(cx + rx * Math.Cos(t) * cos - ry * Math.Sin(t) * sin, cy + rx * Math.Cos(t) * sin + ry * Math.Sin(t) * cos);
            }
            Point = (x, y);
        }
    }

    /// <summary>Outlines a stroke as one polygon per segment plus round joins, for nonzero filling.</summary>
    static List<List<(double X, double Y)>> Stroke(PathBuilder path, double width, bool roundCaps)
    {
        var result = new List<List<(double X, double Y)>>();
        double r = width / 2;
        void Disc((double X, double Y) c) =>
            result.Add(Enumerable.Range(0, 16).Select(i => (c.X + r * Math.Cos(i * Math.PI / 8), c.Y + r * Math.Sin(i * Math.PI / 8))).ToList());
        for (int s = 0; s < path.Subpaths.Count; s++)
        {
            var pts = path.Subpaths[s];
            bool closed = path.Closed[s];
            int count = closed ? pts.Count : pts.Count - 1;
            for (int i = 0; i < count; i++)
            {
                var a = pts[i];
                var b = pts[(i + 1) % pts.Count];
                double dx = b.X - a.X, dy = b.Y - a.Y, len = Math.Sqrt(dx * dx + dy * dy);
                if (len == 0)
                    continue;
                double nx = -dy / len * r, ny = dx / len * r;
                // Same orientation for every quad, so overlaps add up under nonzero filling.
                var quad = new List<(double, double)> { (a.X + nx, a.Y + ny), (b.X + nx, b.Y + ny), (b.X - nx, b.Y - ny), (a.X - nx, a.Y - ny) };
                if ((b.X - a.X) * ny - (b.Y - a.Y) * nx < 0)
                    quad.Reverse();
                result.Add(quad);
                if (i > 0 || closed || roundCaps)
                    Disc(a);
            }
            if (!closed && roundCaps && pts.Count > 0)
                Disc(pts[^1]);
        }
        // Normalize every polygon to one orientation, so nonzero filling unions them.
        foreach (var poly in result)
        {
            double area = 0;
            for (int i = 0; i < poly.Count; i++)
                area += poly[i].X * poly[(i + 1) % poly.Count].Y - poly[(i + 1) % poly.Count].X * poly[i].Y;
            if (area < 0)
                poly.Reverse();
        }
        return result;
    }

    // --- SVG ---------------------------------------------------------------------------------

    static class Svg
    {
        public static Drawing Parse(byte[] data)
        {
            var root = XDocument.Parse(Encoding.UTF8.GetString(data)).Root ?? throw new RenderError("empty SVG");
            var ns = root.Name.Namespace;
            double[]? viewBox = (string?)root.Attribute("viewBox") is { } vb ? Numbers(vb).ToArray() : null;
            double width = Length((string?)root.Attribute("width")) ?? viewBox?[2] ?? throw new RenderError("SVG has no size");
            double height = Length((string?)root.Attribute("height")) ?? viewBox?[3] ?? throw new RenderError("SVG has no size");
            var m = Matrix.Identity;
            if (viewBox is { Length: 4 })
                m = new Matrix(width / viewBox[2], 0, 0, height / viewBox[3], -viewBox[0] * width / viewBox[2], -viewBox[1] * height / viewBox[3]);
            var drawing = new Drawing(width, height);
            Walk(root, m, new Style(), drawing);
            return drawing;
        }

        sealed record Style(string Fill = "#000", string Stroke = "none", double StrokeWidth = 1, double FillOpacity = 1,
            double StrokeOpacity = 1, double Opacity = 1, bool EvenOdd = false, bool RoundCaps = false);

        static Style Apply(XElement e, Style parent)
        {
            var props = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var a in e.Attributes())
                props[a.Name.LocalName] = a.Value;
            if ((string?)e.Attribute("style") is { } css)
                foreach (var decl in css.Split(';', StringSplitOptions.RemoveEmptyEntries))
                    if (decl.Split(':', 2) is [var k, var v])
                        props[k.Trim()] = v.Trim();
            string? P(string k) => props.TryGetValue(k, out var v) ? v : null;
            return parent with
            {
                Fill = P("fill") ?? parent.Fill,
                Stroke = P("stroke") ?? parent.Stroke,
                StrokeWidth = P("stroke-width") is { } sw ? Length(sw) ?? 1 : parent.StrokeWidth,
                FillOpacity = P("fill-opacity") is { } fo ? Number(fo) : parent.FillOpacity,
                StrokeOpacity = P("stroke-opacity") is { } so ? Number(so) : parent.StrokeOpacity,
                Opacity = P("opacity") is { } o ? parent.Opacity * Number(o) : parent.Opacity,
                EvenOdd = P("fill-rule") is { } fr ? fr == "evenodd" : parent.EvenOdd,
                RoundCaps = P("stroke-linecap") is { } lc ? lc == "round" : parent.RoundCaps,
            };
        }

        static void Walk(XElement e, Matrix m, Style style, Drawing drawing)
        {
            foreach (var child in e.Elements())
            {
                var s = Apply(child, style);
                var cm = (string?)child.Attribute("transform") is { } t ? m.Times(Transform(t)) : m;
                switch (child.Name.LocalName)
                {
                    case "g" or "svg":
                        Walk(child, cm, s, drawing);
                        break;
                    case "path":
                        Paint(PathData((string?)child.Attribute("d") ?? "", cm), s, cm, drawing);
                        break;
                    case "rect":
                        Paint(Rect(child, cm), s, cm, drawing);
                        break;
                    case "circle" or "ellipse":
                    {
                        double cx = Number((string?)child.Attribute("cx") ?? "0"), cy = Number((string?)child.Attribute("cy") ?? "0");
                        double rx = Number((string?)child.Attribute(child.Name.LocalName == "circle" ? "r" : "rx") ?? "0");
                        double ry = child.Name.LocalName == "circle" ? rx : Number((string?)child.Attribute("ry") ?? "0");
                        var p = new PathBuilder(cm);
                        p.MoveTo(cx + rx, cy);
                        p.ArcTo(rx, ry, 0, false, true, cx - rx, cy);
                        p.ArcTo(rx, ry, 0, false, true, cx + rx, cy);
                        p.Close();
                        Paint(p, s, cm, drawing);
                        break;
                    }
                    case "line":
                    {
                        var p = new PathBuilder(cm);
                        p.MoveTo(Number((string?)child.Attribute("x1") ?? "0"), Number((string?)child.Attribute("y1") ?? "0"));
                        p.LineTo(Number((string?)child.Attribute("x2") ?? "0"), Number((string?)child.Attribute("y2") ?? "0"));
                        Paint(p, s with { Fill = "none" }, cm, drawing);
                        break;
                    }
                    case "polyline" or "polygon":
                    {
                        var n = Numbers((string?)child.Attribute("points") ?? "").ToList();
                        var p = new PathBuilder(cm);
                        for (int i = 0; i + 1 < n.Count; i += 2)
                            if (i == 0) p.MoveTo(n[i], n[i + 1]); else p.LineTo(n[i], n[i + 1]);
                        if (child.Name.LocalName == "polygon")
                            p.Close();
                        Paint(p, s, cm, drawing);
                        break;
                    }
                    case "title" or "desc" or "metadata":
                        break;
                    case "defs" when !child.Elements().Any():
                        break;
                    default:
                        throw new RenderError($"SVG <{child.Name.LocalName}> is not supported (supported: paths, shapes, groups)");
                }
            }
        }

        static PathBuilder Rect(XElement e, Matrix m)
        {
            double x = Number((string?)e.Attribute("x") ?? "0"), y = Number((string?)e.Attribute("y") ?? "0");
            double w = Number((string?)e.Attribute("width") ?? "0"), h = Number((string?)e.Attribute("height") ?? "0");
            double rx = (string?)e.Attribute("rx") is { } a ? Number(a) : (string?)e.Attribute("ry") is { } b ? Number(b) : 0;
            double ry = (string?)e.Attribute("ry") is { } c ? Number(c) : rx;
            rx = Math.Min(rx, w / 2); ry = Math.Min(ry, h / 2);
            var p = new PathBuilder(m);
            if (rx <= 0 || ry <= 0)
            {
                p.MoveTo(x, y); p.LineTo(x + w, y); p.LineTo(x + w, y + h); p.LineTo(x, y + h);
            }
            else
            {
                p.MoveTo(x + rx, y); p.LineTo(x + w - rx, y); p.ArcTo(rx, ry, 0, false, true, x + w, y + ry);
                p.LineTo(x + w, y + h - ry); p.ArcTo(rx, ry, 0, false, true, x + w - rx, y + h);
                p.LineTo(x + rx, y + h); p.ArcTo(rx, ry, 0, false, true, x, y + h - ry);
                p.LineTo(x, y + ry); p.ArcTo(rx, ry, 0, false, true, x + rx, y);
            }
            p.Close();
            return p;
        }

        static void Paint(PathBuilder p, Style s, Matrix m, Drawing drawing)
        {
            if (Color(s.Fill) is { } fill)
                drawing.Fills.Add((p.Subpaths, s.EvenOdd, (fill.R, fill.G, fill.B, fill.A * s.FillOpacity * s.Opacity)));
            if (Color(s.Stroke) is { } stroke && s.StrokeWidth > 0)
                drawing.Fills.Add((Stroke(p, s.StrokeWidth * m.Scale, s.RoundCaps), false, (stroke.R, stroke.G, stroke.B, stroke.A * s.StrokeOpacity * s.Opacity)));
        }

        static readonly Dictionary<string, string> Named = new(StringComparer.OrdinalIgnoreCase)
        {
            ["black"] = "#000000", ["white"] = "#ffffff", ["red"] = "#ff0000", ["green"] = "#008000", ["blue"] = "#0000ff",
            ["yellow"] = "#ffff00", ["gray"] = "#808080", ["grey"] = "#808080", ["orange"] = "#ffa500", ["purple"] = "#800080",
        };

        static (double R, double G, double B, double A)? Color(string value)
        {
            value = value.Trim();
            if (value is "none" or "transparent")
                return null;
            if (value.StartsWith("url(", StringComparison.Ordinal))
                throw new RenderError("SVG gradients and patterns are not supported (only solid colors)");
            if (value == "currentColor")
                return (0, 0, 0, 1);
            if (Named.TryGetValue(value, out var hex))
                value = hex;
            if (value.StartsWith('#'))
            {
                string h = value[1..];
                if (h.Length == 3)
                    h = string.Concat(h.Select(c => $"{c}{c}"));
                if (h.Length != 6)
                    throw new RenderError($"SVG color '{value}' is not supported");
                return (Convert.ToInt32(h[0..2], 16) / 255.0, Convert.ToInt32(h[2..4], 16) / 255.0, Convert.ToInt32(h[4..6], 16) / 255.0, 1);
            }
            var m = Regex.Match(value, @"^rgba?\(([^)]*)\)$");
            if (m.Success)
            {
                var parts = m.Groups[1].Value.Split(',').Select(p => p.Trim()).ToArray();
                double C(string p) => p.EndsWith('%') ? Number(p[..^1]) / 100 : Number(p) / 255;
                return (C(parts[0]), C(parts[1]), C(parts[2]), parts.Length > 3 ? Number(parts[3]) : 1);
            }
            throw new RenderError($"SVG color '{value}' is not supported");
        }

        static Matrix Transform(string t)
        {
            var m = Matrix.Identity;
            foreach (Match f in Regex.Matches(t, @"(\w+)\s*\(([^)]*)\)"))
            {
                var a = Numbers(f.Groups[2].Value).ToArray();
                Matrix step = f.Groups[1].Value switch
                {
                    "matrix" => new Matrix(a[0], a[1], a[2], a[3], a[4], a[5]),
                    "translate" => new Matrix(1, 0, 0, 1, a[0], a.Length > 1 ? a[1] : 0),
                    "scale" => new Matrix(a[0], 0, 0, a.Length > 1 ? a[1] : a[0], 0, 0),
                    "rotate" => Rotate(a),
                    "skewX" => new Matrix(1, 0, Math.Tan(a[0] * Math.PI / 180), 1, 0, 0),
                    "skewY" => new Matrix(1, Math.Tan(a[0] * Math.PI / 180), 0, 1, 0, 0),
                    var other => throw new RenderError($"SVG transform '{other}' is not supported"),
                };
                m = m.Times(step);
            }
            return m;
        }

        static Matrix Rotate(double[] a)
        {
            double r = a[0] * Math.PI / 180, c = Math.Cos(r), s = Math.Sin(r);
            var rot = new Matrix(c, s, -s, c, 0, 0);
            return a.Length < 3 ? rot : new Matrix(1, 0, 0, 1, a[1], a[2]).Times(rot).Times(new Matrix(1, 0, 0, 1, -a[1], -a[2]));
        }

        static PathBuilder PathData(string d, Matrix m)
        {
            var p = new PathBuilder(m);
            var tokens = Regex.Matches(d, @"[A-Za-z]|[-+]?(?:\d+\.?\d*|\.\d+)(?:[eE][-+]?\d+)?").Select(t => t.Value).ToList();
            int i = 0;
            char cmd = ' ';
            (double X, double Y) lastControl = (0, 0);
            char lastCmd = ' ';
            double N() => double.Parse(tokens[i++], CultureInfo.InvariantCulture);
            bool Flag()
            {
                // Arc flags may be written without separators ("a1 1 0 01 1 1").
                string t = tokens[i];
                if (t.Length > 1 && (t[0] == '0' || t[0] == '1') && !t.Contains('.'))
                {
                    tokens[i] = t[1..];
                    return t[0] == '1';
                }
                i++;
                return t != "0";
            }
            while (i < tokens.Count)
            {
                if (char.IsLetter(tokens[i][0]))
                    cmd = tokens[i++][0];
                else if (cmd == ' ')
                    throw new RenderError("SVG path data must start with a command");
                bool rel = char.IsLower(cmd);
                double ox = rel ? p.Point.X : 0, oy = rel ? p.Point.Y : 0;
                switch (char.ToUpperInvariant(cmd))
                {
                    case 'M':
                        p.MoveTo(ox + N(), oy + N());
                        cmd = rel ? 'l' : 'L'; // following pairs are line-tos
                        break;
                    case 'L': p.LineTo(ox + N(), oy + N()); break;
                    case 'H': p.LineTo(ox + N(), p.Point.Y); break;
                    case 'V': p.LineTo(p.Point.X, oy + N()); break;
                    case 'C':
                    {
                        double x1 = ox + N(), y1 = oy + N(), x2 = ox + N(), y2 = oy + N(), x = ox + N(), y = oy + N();
                        p.CubicTo(x1, y1, x2, y2, x, y);
                        lastControl = (x2, y2);
                        break;
                    }
                    case 'S':
                    {
                        var (x1, y1) = "CcSs".Contains(lastCmd) ? (2 * p.Point.X - lastControl.X, 2 * p.Point.Y - lastControl.Y) : p.Point;
                        double x2 = ox + N(), y2 = oy + N(), x = ox + N(), y = oy + N();
                        p.CubicTo(x1, y1, x2, y2, x, y);
                        lastControl = (x2, y2);
                        break;
                    }
                    case 'Q':
                    {
                        double x1 = ox + N(), y1 = oy + N(), x = ox + N(), y = oy + N();
                        p.QuadTo(x1, y1, x, y);
                        lastControl = (x1, y1);
                        break;
                    }
                    case 'T':
                    {
                        var (x1, y1) = "QqTt".Contains(lastCmd) ? (2 * p.Point.X - lastControl.X, 2 * p.Point.Y - lastControl.Y) : p.Point;
                        double x = ox + N(), y = oy + N();
                        p.QuadTo(x1, y1, x, y);
                        lastControl = (x1, y1);
                        break;
                    }
                    case 'A':
                    {
                        double rx = N(), ry = N(), phi = N();
                        bool large = Flag(), sweep = Flag();
                        double x = ox + N(), y = oy + N();
                        p.ArcTo(rx, ry, phi, large, sweep, x, y);
                        break;
                    }
                    case 'Z':
                        p.Close();
                        break;
                    default:
                        throw new RenderError($"SVG path command '{cmd}' is not supported");
                }
                lastCmd = cmd;
            }
            return p;
        }

        static IEnumerable<double> Numbers(string s) =>
            Regex.Matches(s, @"[-+]?(?:\d+\.?\d*|\.\d+)(?:[eE][-+]?\d+)?").Select(m => double.Parse(m.Value, CultureInfo.InvariantCulture));

        static double Number(string s) => double.Parse(s.Trim().TrimEnd('%'), CultureInfo.InvariantCulture);

        static double? Length(string? s)
        {
            if (s is null)
                return null;
            var m = Regex.Match(s.Trim(), @"^([-+]?(?:\d+\.?\d*|\.\d+))(px|pt)?$");
            return m.Success ? double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : null;
        }
    }

    // --- PDF ---------------------------------------------------------------------------------

    /// <summary>
    /// The first page of a PDF: its MediaBox and content stream (and form XObjects), interpreted for
    /// path construction and painting with device RGB/gray/CMYK colors and constant opacity.
    /// </summary>
    static class Pdf
    {
        public static Drawing Parse(byte[] data)
        {
            var objects = Objects(data);
            var catalog = objects.Values.FirstOrDefault(o => o is Dictionary<string, object> d && d.TryGetValue("Type", out var t) && t as string == "/Catalog") as Dictionary<string, object>
                ?? throw new RenderError("PDF has no catalog");
            var page = FirstPage(Resolve(catalog["Pages"], objects) as Dictionary<string, object> ?? throw new RenderError("PDF has no pages"), objects);
            var box = Array(Inherited(page, "MediaBox", objects) ?? throw new RenderError("PDF page has no MediaBox"), objects).Select(n => Num(n)).ToArray();
            double width = box[2] - box[0], height = box[3] - box[1];
            var drawing = new Drawing(width, height);
            // PDF space is y-up from the MediaBox origin; the drawing is y-down.
            var m = new Matrix(1, 0, 0, -1, -box[0], box[3]);
            var resources = Resolve(Inherited(page, "Resources", objects), objects) as Dictionary<string, object> ?? [];
            var contents = Resolve(page.GetValueOrDefault("Contents"), objects);
            var bytes = new MemoryStream();
            foreach (var c in contents is List<object> list ? list : contents is null ? [] : [contents])
                bytes.Write(StreamData(Resolve(c, objects), data));
            new Interpreter(objects, data, drawing).Run(bytes.ToArray(), m, resources);
            return drawing;
        }

        static object? Inherited(Dictionary<string, object> node, string key, Dictionary<int, object> objects)
        {
            for (var n = node; n is not null; n = Resolve(n.GetValueOrDefault("Parent"), objects) as Dictionary<string, object>)
                if (n.TryGetValue(key, out var v))
                    return v;
            return null;
        }

        static Dictionary<string, object> FirstPage(Dictionary<string, object> node, Dictionary<int, object> objects)
        {
            if (node.GetValueOrDefault("Type") as string == "/Page")
                return node;
            var kids = Array(node.GetValueOrDefault("Kids") ?? throw new RenderError("PDF page tree has no kids"), objects);
            return FirstPage(Resolve(kids[0], objects) as Dictionary<string, object> ?? throw new RenderError("bad PDF page tree"), objects);
        }

        sealed record Ref(int Number);
        sealed record Stream(Dictionary<string, object> Dict, int Offset, int Length);

        static object? Resolve(object? o, Dictionary<int, object> objects) =>
            o is Ref r ? objects.GetValueOrDefault(r.Number) : o;

        static List<object> Array(object o, Dictionary<int, object> objects) =>
            Resolve(o, objects) as List<object> ?? throw new RenderError("expected a PDF array");

        static double Num(object? o) => o is double d ? d : throw new RenderError("expected a PDF number");

        // Every "N G obj ... endobj" in the file (a scan, so no xref is needed), plus objects in object streams.
        static Dictionary<int, object> Objects(byte[] data)
        {
            var objects = new Dictionary<int, object>();
            string text = Encoding.Latin1.GetString(data);
            foreach (Match m in Regex.Matches(text, @"(?<![0-9])(\d+)\s+(\d+)\s+obj\b"))
            {
                var lexer = new Lexer(data, m.Index + m.Length);
                var value = lexer.Value();
                if (value is Dictionary<string, object> dict && lexer.PeekKeyword("stream"))
                {
                    int start = lexer.StreamStart();
                    int length = dict.GetValueOrDefault("Length") is double l ? (int)l : -1;
                    if (length < 0)
                        length = text.IndexOf("endstream", start, StringComparison.Ordinal) - start;
                    value = new Stream(dict, start, length);
                }
                objects[int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)] = value!;
            }
            // Resolve indirect /Length values, then unpack object streams.
            foreach (var (n, o) in objects.ToList())
                if (o is Stream s && s.Dict.GetValueOrDefault("Length") is Ref lr && objects.GetValueOrDefault(lr.Number) is double len)
                    objects[n] = s with { Length = (int)len };
            foreach (var o in objects.Values.ToList())
                if (o is Stream s && s.Dict.GetValueOrDefault("Type") as string == "/ObjStm")
                {
                    var body = StreamData(s, data);
                    int count = (int)Num(s.Dict["N"]), first = (int)Num(s.Dict["First"]);
                    var header = new Lexer(body, 0);
                    var entries = Enumerable.Range(0, count).Select(_ => ((int)Num(header.Value()), (int)Num(header.Value()))).ToList();
                    foreach (var (num, offset) in entries)
                        objects.TryAdd(num, new Lexer(body, first + offset).Value()!);
                }
            return objects;
        }

        static byte[] StreamData(object? o, byte[] file)
        {
            if (o is not Stream s)
                throw new RenderError("expected a PDF stream");
            var raw = file.AsSpan(s.Offset, Math.Min(s.Length, file.Length - s.Offset)).ToArray();
            var filter = s.Dict.GetValueOrDefault("Filter");
            var filters = filter is List<object> fl ? fl.Cast<string>().ToList() : filter is string f ? [f] : [];
            foreach (var name in filters)
            {
                if (name != "/FlateDecode")
                    throw new RenderError($"PDF stream filter {name} is not supported");
                using var z = new ZLibStream(new MemoryStream(raw), CompressionMode.Decompress);
                var outStream = new MemoryStream();
                z.CopyTo(outStream);
                raw = outStream.ToArray();
            }
            return raw;
        }

        sealed class Lexer(byte[] data, int pos)
        {
            int p = pos;

            void SkipSpace()
            {
                while (p < data.Length)
                {
                    if (data[p] == '%')
                        while (p < data.Length && data[p] != '\n' && data[p] != '\r') p++;
                    else if (data[p] is (byte)' ' or (byte)'\n' or (byte)'\r' or (byte)'\t' or 0 or 12) p++;
                    else break;
                }
            }

            public bool AtEnd { get { SkipSpace(); return p >= data.Length; } }

            public bool PeekKeyword(string k)
            {
                SkipSpace();
                return p + k.Length <= data.Length && Encoding.Latin1.GetString(data, p, k.Length) == k;
            }

            public int StreamStart()
            {
                p += "stream".Length;
                if (p < data.Length && data[p] == '\r') p++;
                if (p < data.Length && data[p] == '\n') p++;
                return p;
            }

            static bool Delimiter(byte b) => b is (byte)'(' or (byte)')' or (byte)'<' or (byte)'>' or (byte)'[' or (byte)']' or (byte)'{' or (byte)'}' or (byte)'/' or (byte)'%';
            static bool Space(byte b) => b is (byte)' ' or (byte)'\n' or (byte)'\r' or (byte)'\t' or 0 or 12;

            /// <summary>The next object, or an operator token (as <see cref="Operator"/>).</summary>
            public object? Value()
            {
                SkipSpace();
                if (p >= data.Length)
                    return null;
                byte c = data[p];
                if (c == '/')
                {
                    int s = ++p;
                    while (p < data.Length && !Space(data[p]) && !Delimiter(data[p])) p++;
                    return "/" + Encoding.Latin1.GetString(data, s, p - s);
                }
                if (c == '[')
                {
                    p++;
                    var list = new List<object>();
                    while (true)
                    {
                        SkipSpace();
                        if (p < data.Length && data[p] == ']') { p++; return list; }
                        var v = Value() ?? throw new RenderError("unterminated PDF array");
                        list.Add(v);
                    }
                }
                if (c == '<' && p + 1 < data.Length && data[p + 1] == '<')
                {
                    p += 2;
                    var dict = new Dictionary<string, object>(StringComparer.Ordinal);
                    while (true)
                    {
                        SkipSpace();
                        if (p + 1 < data.Length && data[p] == '>' && data[p + 1] == '>') { p += 2; return dict; }
                        var key = Value() as string ?? throw new RenderError("bad PDF dictionary");
                        dict[key.TrimStart('/')] = Value() ?? throw new RenderError("bad PDF dictionary");
                    }
                }
                if (c == '<')
                {
                    int e = System.Array.IndexOf(data, (byte)'>', p);
                    p = e + 1;
                    return "<hex>";
                }
                if (c == '(')
                {
                    int depth = 0;
                    do
                    {
                        if (data[p] == '\\') p++;
                        else if (data[p] == '(') depth++;
                        else if (data[p] == ')') depth--;
                        p++;
                    } while (depth > 0 && p < data.Length);
                    return "(string)";
                }
                int start = p;
                while (p < data.Length && !Space(data[p]) && !Delimiter(data[p])) p++;
                string token = Encoding.Latin1.GetString(data, start, Math.Max(1, p - start));
                if (p == start) p++;
                if (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out double n))
                {
                    // "N G R" is a reference.
                    int save = p;
                    SkipSpace();
                    int s2 = p;
                    while (p < data.Length && data[p] >= '0' && data[p] <= '9') p++;
                    if (p > s2)
                    {
                        SkipSpace();
                        if (p < data.Length && data[p] == 'R' && (p + 1 >= data.Length || Space(data[p + 1]) || Delimiter(data[p + 1])))
                        {
                            p++;
                            return new Ref((int)n);
                        }
                    }
                    p = save;
                    return n;
                }
                return token switch { "true" => true, "false" => false, "null" => null, _ => new Operator(token) };
            }
        }

        sealed record Operator(string Name);

        sealed class Interpreter(Dictionary<int, object> objects, byte[] file, Drawing drawing)
        {
            sealed record State(Matrix M, (double R, double G, double B) Fill, (double R, double G, double B) Stroke, double LineWidth, double FillAlpha, double StrokeAlpha);

            public void Run(byte[] content, Matrix m, Dictionary<string, object> resources, int depth = 0)
            {
                if (depth > 8)
                    throw new RenderError("PDF forms nest too deeply");
                var state = new State(m, (0, 0, 0), (0, 0, 0), 1, 1, 1);
                var stack = new Stack<State>();
                PathBuilder? path = null;
                var operands = new List<object?>();
                var lexer = new Lexer(content, 0);
                double N(int i) => Num(operands[i]);
                while (!lexer.AtEnd)
                {
                    var v = lexer.Value();
                    if (v is not Operator op)
                    {
                        operands.Add(v);
                        continue;
                    }
                    path ??= new PathBuilder(state.M);
                    switch (op.Name)
                    {
                        case "q": stack.Push(state); break;
                        case "Q": state = stack.Count > 0 ? stack.Pop() : state; break;
                        case "cm": state = state with { M = state.M.Times(new Matrix(N(0), N(1), N(2), N(3), N(4), N(5))) }; path = null; break;
                        case "w": state = state with { LineWidth = N(0) }; break;
                        case "J" or "j" or "M" or "d" or "ri" or "i": break; // caps, joins, dashes: approximated
                        case "rg": state = state with { Fill = (N(0), N(1), N(2)) }; break;
                        case "RG": state = state with { Stroke = (N(0), N(1), N(2)) }; break;
                        case "g": state = state with { Fill = (N(0), N(0), N(0)) }; break;
                        case "G": state = state with { Stroke = (N(0), N(0), N(0)) }; break;
                        case "k": state = state with { Fill = Cmyk(N(0), N(1), N(2), N(3)) }; break;
                        case "K": state = state with { Stroke = Cmyk(N(0), N(1), N(2), N(3)) }; break;
                        case "cs" or "CS": break; // device color spaces: the sc/scn operands decide
                        case "sc" or "scn" or "SC" or "SCN":
                        {
                            if (operands.Any(o => o is string))
                                throw new RenderError("PDF patterns and shadings are not supported (only solid colors)");
                            var c = operands.Count switch
                            {
                                1 => (N(0), N(0), N(0)),
                                3 => (N(0), N(1), N(2)),
                                4 => Cmyk(N(0), N(1), N(2), N(3)),
                                _ => throw new RenderError("unsupported PDF color"),
                            };
                            state = op.Name is "sc" or "scn" ? state with { Fill = c } : state with { Stroke = c };
                            break;
                        }
                        case "gs":
                        {
                            var gs = Lookup(resources, "ExtGState", (string)operands[0]!);
                            if (gs.GetValueOrDefault("ca") is double ca) state = state with { FillAlpha = ca };
                            if (gs.GetValueOrDefault("CA") is double sa) state = state with { StrokeAlpha = sa };
                            if (gs.GetValueOrDefault("SMask") is Dictionary<string, object>)
                                throw new RenderError("PDF soft masks are not supported");
                            break;
                        }
                        case "m": path.MoveTo(N(0), N(1)); break;
                        case "l": path.LineTo(N(0), N(1)); break;
                        case "c": path.CubicTo(N(0), N(1), N(2), N(3), N(4), N(5)); break;
                        case "v": path.CubicTo(path.Point.X, path.Point.Y, N(0), N(1), N(2), N(3)); break;
                        case "y": path.CubicTo(N(0), N(1), N(2), N(3), N(2), N(3)); break;
                        case "h": path.Close(); break;
                        case "re":
                            path.MoveTo(N(0), N(1)); path.LineTo(N(0) + N(2), N(1)); path.LineTo(N(0) + N(2), N(1) + N(3)); path.LineTo(N(0), N(1) + N(3)); path.Close();
                            break;
                        case "f" or "F" or "f*":
                            Fill(path, op.Name == "f*", state);
                            path = null;
                            break;
                        case "S" or "s":
                            if (op.Name == "s") path.Close();
                            StrokePath(path, state);
                            path = null;
                            break;
                        case "B" or "B*" or "b" or "b*":
                            if (op.Name.StartsWith('b')) path.Close();
                            Fill(path, op.Name.EndsWith('*'), state);
                            StrokePath(path, state);
                            path = null;
                            break;
                        case "n": path = null; break;
                        case "W" or "W*":
                            throw new RenderError("PDF clipping paths are not supported");
                        case "Do":
                        {
                            var xobject = Resolve(Entry(resources, "XObject", (string)operands[0]!), objects) as Stream ?? throw new RenderError("bad PDF XObject");
                            if (xobject.Dict.GetValueOrDefault("Subtype") as string != "/Form")
                                throw new RenderError("images inside PDF artwork are not supported");
                            var fm = xobject.Dict.GetValueOrDefault("Matrix") is List<object> mm ? new Matrix(Num(mm[0]), Num(mm[1]), Num(mm[2]), Num(mm[3]), Num(mm[4]), Num(mm[5])) : Matrix.Identity;
                            var formResources = Resolve(xobject.Dict.GetValueOrDefault("Resources"), objects) as Dictionary<string, object> ?? resources;
                            Run(StreamData(xobject, file), state.M.Times(fm), formResources, depth + 1);
                            break;
                        }
                        case "BT":
                            throw new RenderError("text in PDF artwork is not supported (convert it to outlines)");
                        case "sh" or "BI":
                            throw new RenderError("PDF shadings and inline images are not supported");
                        case "BMC" or "BDC" or "EMC" or "MP" or "DP" or "BX" or "EX": break;
                        default:
                            throw new RenderError($"PDF operator '{op.Name}' is not supported");
                    }
                    operands.Clear();
                }
            }

            Dictionary<string, object> Lookup(Dictionary<string, object> resources, string category, string name) =>
                Resolve(Entry(resources, category, name), objects) as Dictionary<string, object> ?? throw new RenderError($"PDF resource {name} is not a dictionary");

            object Entry(Dictionary<string, object> resources, string category, string name)
            {
                var group = Resolve(resources.GetValueOrDefault(category), objects) as Dictionary<string, object> ?? throw new RenderError($"PDF has no {category} resources");
                return group.GetValueOrDefault(name.TrimStart('/')) ?? throw new RenderError($"PDF resource {name} is missing");
            }

            void Fill(PathBuilder path, bool evenOdd, State s) =>
                drawing.Fills.Add((path.Subpaths, evenOdd, (s.Fill.R, s.Fill.G, s.Fill.B, s.FillAlpha)));

            void StrokePath(PathBuilder path, State s) =>
                drawing.Fills.Add((Stroke(path, Math.Max(s.LineWidth, 0.5) * s.M.Scale, false), false, (s.Stroke.R, s.Stroke.G, s.Stroke.B, s.StrokeAlpha)));

            static (double, double, double) Cmyk(double c, double m, double y, double k) => ((1 - c) * (1 - k), (1 - m) * (1 - k), (1 - y) * (1 - k));
        }
    }
}
