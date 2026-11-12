using System;
using System.Collections.Generic;

namespace Kzm.Core
{
    /// <summary>Plan coordinates in drawing units (1 unit = 1 m, WCS).</summary>
    public struct XY : IEquatable<XY>
    {
        public XY(double x, double y) { X = x; Y = y; }
        public double X { get; }
        public double Y { get; }

        public double DistanceTo(XY other)
        {
            double dx = X - other.X, dy = Y - other.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        public bool Equals(XY other) => X.Equals(other.X) && Y.Equals(other.Y);
        public override bool Equals(object obj) => obj is XY other && Equals(other);
        public override int GetHashCode() => unchecked(X.GetHashCode() * 397 ^ Y.GetHashCode());
        public override string ToString() => ElevationFormat.Format(X, 3) + "; " + ElevationFormat.Format(Y, 3);
    }

    public enum SurfaceKind
    {
        Fact = 0,
        Project = 1,
    }

    public static class SurfaceKinds
    {
        public static string Title(SurfaceKind kind) => kind == SurfaceKind.Project ? "проектная" : "фактическая";
        public static string Code(SurfaceKind kind) => kind == SurfaceKind.Project ? "PROJECT" : "FACT";

        public static SurfaceKind Parse(string code)
        {
            if (string.Equals(code, "PROJECT", StringComparison.Ordinal)) return SurfaceKind.Project;
            if (string.Equals(code, "FACT", StringComparison.Ordinal)) return SurfaceKind.Fact;
            throw new KzmException("неизвестный тип поверхности: " + code);
        }
    }

    /// <summary>Named surface stored in the drawing; its points are «КЗМ_ТОЧКА» blocks.</summary>
    public sealed class SurfaceInfo
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public SurfaceKind Kind { get; set; }
        /// <summary>Layer that receives new points of this surface.</summary>
        public string Layer { get; set; }
        public string Created { get; set; }
        /// <summary>Increases on every change of the surface data (used later to mark cartograms outdated).</summary>
        public int Revision { get; set; }

        public string Title => "«" + Name + "» (" + SurfaceKinds.Title(Kind) + ", слой " + Layer + ")";

        public static string DefaultLayer(SurfaceKind kind) => kind == SurfaceKind.Project ? "КЗМ_ТОЧКИ_ПРОЕКТ" : "КЗМ_ТОЧКИ_ФАКТ";
    }

    /// <summary>Where the elevation value of a point came from.</summary>
    public enum SourceKind
    {
        Text = 0,
        MText = 1,
        Manual = 2,
    }

    public static class SourceKinds
    {
        public static string Code(SourceKind kind)
        {
            switch (kind)
            {
                case SourceKind.Text: return "TEXT";
                case SourceKind.MText: return "MTEXT";
                default: return "MANUAL";
            }
        }

        public static SourceKind Parse(string code)
        {
            switch (code)
            {
                case "TEXT": return SourceKind.Text;
                case "MTEXT": return SourceKind.MText;
                case "MANUAL": return SourceKind.Manual;
                default: throw new KzmException("неизвестный источник точки: " + code);
            }
        }
    }

    /// <summary>How the plan position of an imported point is taken.</summary>
    public enum XyMode
    {
        /// <summary>(а) insertion point of the text.</summary>
        Insertion = 0,
        /// <summary>(б) nearest similar marker (POINT / CIRCLE / block) within radius R.</summary>
        Marker = 1,
        /// <summary>(в) picked by the user.</summary>
        Manual = 2,
    }

    public static class XyModes
    {
        public static string Code(XyMode mode)
        {
            switch (mode)
            {
                case XyMode.Insertion: return "INSERTION";
                case XyMode.Marker: return "MARKER";
                default: return "MANUAL";
            }
        }

        public static XyMode Parse(string code)
        {
            switch (code)
            {
                case "INSERTION": return XyMode.Insertion;
                case "MARKER": return XyMode.Marker;
                case "MANUAL": return XyMode.Manual;
                default: throw new KzmException("неизвестный способ получения XY: " + code);
            }
        }

        public static string Title(XyMode mode)
        {
            switch (mode)
            {
                case XyMode.Insertion: return "точка вставки текста";
                case XyMode.Marker: return "маркер";
                default: return "вручную";
            }
        }
    }

    public sealed class KzmException : Exception
    {
        public KzmException(string message) : base(message) { }
        public KzmException(string message, Exception inner) : base(message, inner) { }
    }

    /// <summary>Text taken from the drawing for import (already stripped of formatting).</summary>
    public sealed class SourceText
    {
        public SourceText(string handle, SourceKind kind, string raw, string plain, XY anchor)
        {
            Handle = handle;
            Kind = kind;
            Raw = raw ?? string.Empty;
            Plain = plain ?? string.Empty;
            Anchor = anchor;
        }

        public string Handle { get; }
        public SourceKind Kind { get; }
        /// <summary>Text as stored in the drawing (TEXT string or MTEXT contents).</summary>
        public string Raw { get; }
        /// <summary>Text after removal of formatting codes.</summary>
        public string Plain { get; }
        /// <summary>Insertion point of the text.</summary>
        public XY Anchor { get; }
    }

    public sealed class MarkerCandidate
    {
        public MarkerCandidate(string handle, XY position)
        {
            Handle = handle;
            Position = position;
        }

        public string Handle { get; }
        public XY Position { get; }
    }

    /// <summary>A point that already belongs to the target surface.</summary>
    public sealed class ExistingPoint
    {
        public ExistingPoint(string handle, XY position, double elevation, string sourceHandle)
        {
            Handle = handle;
            Position = position;
            Elevation = elevation;
            SourceHandle = sourceHandle;
        }

        public string Handle { get; }
        public XY Position { get; }
        public double Elevation { get; }
        public string SourceHandle { get; }
    }

    internal static class Guard
    {
        internal static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

        internal static void Positive(double value, string name)
        {
            if (!Finite(value) || value <= 0) throw new KzmException(name + " должен быть положительным числом.");
        }

        internal static IReadOnlyList<T> List<T>(IReadOnlyList<T> value, string name)
        {
            if (value == null) throw new ArgumentNullException(name);
            return value;
        }
    }
}
