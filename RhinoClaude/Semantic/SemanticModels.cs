using System;
using System.Collections.Generic;
using System.Linq;

namespace RhinoClaude.Semantic
{
    /// <summary>
    /// A point or vector, free of RhinoCommon. The analyzers convert at the Rhino boundary;
    /// everything downstream — classification, selection, every analytical read tool — works
    /// on these, which is what keeps the rules testable outside Rhino.
    /// </summary>
    public struct Vec3
    {
        public double X, Y, Z;

        public Vec3(double x, double y, double z) { X = x; Y = y; Z = z; }

        public static readonly Vec3 Zero = new Vec3(0, 0, 0);

        public double Length => Math.Sqrt(X * X + Y * Y + Z * Z);

        public Vec3 Unit()
        {
            double len = Length;
            return len <= 1e-12 ? Zero : new Vec3(X / len, Y / len, Z / len);
        }

        public static Vec3 operator +(Vec3 a, Vec3 b) => new Vec3(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static Vec3 operator -(Vec3 a, Vec3 b) => new Vec3(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static Vec3 operator *(Vec3 a, double s) => new Vec3(a.X * s, a.Y * s, a.Z * s);

        public static double Dot(Vec3 a, Vec3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

        public static Vec3 Cross(Vec3 a, Vec3 b) => new Vec3(
            a.Y * b.Z - a.Z * b.Y,
            a.Z * b.X - a.X * b.Z,
            a.X * b.Y - a.Y * b.X);

        public double DistanceTo(Vec3 other) => (this - other).Length;

        public double[] ToArray() => new[] { Round(X), Round(Y), Round(Z) };

        public static double Round(double v) =>
            double.IsNaN(v) || double.IsInfinity(v) ? 0.0 : Math.Round(v, 6);

        public override string ToString() => $"({Round(X)}, {Round(Y)}, {Round(Z)})";
    }

    /// <summary>An axis-aligned bounding box, Rhino-free.</summary>
    public sealed class BoxView
    {
        public Vec3 Min { get; set; }
        public Vec3 Max { get; set; }
        public bool IsValid { get; set; }

        public Vec3 Size => new Vec3(Max.X - Min.X, Max.Y - Min.Y, Max.Z - Min.Z);
        public Vec3 Center => new Vec3((Min.X + Max.X) / 2, (Min.Y + Max.Y) / 2, (Min.Z + Max.Z) / 2);
        public double FootprintArea => Math.Max(0, Max.X - Min.X) * Math.Max(0, Max.Y - Min.Y);
        public double Height => Math.Max(0, Max.Z - Min.Z);

        public static BoxView Unset => new BoxView { IsValid = false, Min = Vec3.Zero, Max = Vec3.Zero };

        public static BoxView From(Vec3 min, Vec3 max) => new BoxView { Min = min, Max = max, IsValid = true };

        public BoxView Union(BoxView other)
        {
            if (other == null || !other.IsValid) return this;
            if (!IsValid) return other;
            return From(
                new Vec3(Math.Min(Min.X, other.Min.X), Math.Min(Min.Y, other.Min.Y), Math.Min(Min.Z, other.Min.Z)),
                new Vec3(Math.Max(Max.X, other.Max.X), Math.Max(Max.Y, other.Max.Y), Math.Max(Max.Z, other.Max.Z)));
        }

        public bool Intersects(BoxView other, double tolerance = 0)
        {
            if (other == null || !IsValid || !other.IsValid) return false;
            return Min.X - tolerance <= other.Max.X && Max.X + tolerance >= other.Min.X
                && Min.Y - tolerance <= other.Max.Y && Max.Y + tolerance >= other.Min.Y
                && Min.Z - tolerance <= other.Max.Z && Max.Z + tolerance >= other.Min.Z;
        }

        public bool Contains(Vec3 point, double tolerance = 0) =>
            IsValid
            && point.X >= Min.X - tolerance && point.X <= Max.X + tolerance
            && point.Y >= Min.Y - tolerance && point.Y <= Max.Y + tolerance
            && point.Z >= Min.Z - tolerance && point.Z <= Max.Z + tolerance;

        public object ToJson() => !IsValid
            ? null
            : (object)new Dictionary<string, object>
            {
                { "min", Min.ToArray() },
                { "max", Max.ToArray() },
                { "size", Size.ToArray() }
            };
    }

    /// <summary>The universal properties every semantic element carries (plan §3 preamble).</summary>
    public abstract class SemanticElement
    {
        public string ElementId { get; set; }
        public string Type { get; set; }
        public List<string> RhinoObjectIds { get; } = new List<string>();
        public string Layer { get; set; }
        public string Name { get; set; }
        public Dictionary<string, string> Tags { get; } = new Dictionary<string, string>(StringComparer.Ordinal);
        /// <summary>user-data | learned-convention | canonical | geometry-inference.</summary>
        public string ClassifiedBy { get; set; }
        public List<string> Notes { get; } = new List<string>();

        public string PrimaryObjectId => RhinoObjectIds.Count > 0 ? RhinoObjectIds[0] : null;
    }

    /// <summary>A solid Brep — the atom of the semantic layer (plan §3.1).</summary>
    public sealed class MassView : SemanticElement
    {
        public MassView() { Type = SemanticVocabulary.Mass; }

        /// <summary>Office | Residential | Retail | Institutional | Common | Other. A property,
        /// not a subtype — the same Mass type carries every function.</summary>
        public string Function { get; set; } = SemanticVocabulary.FunctionOther;

        public double Volume { get; set; }
        public double FootprintArea { get; set; }
        public double HeightAboveGrade { get; set; }
        public BoxView Bbox { get; set; } = BoxView.Unset;
        public Vec3 Centroid { get; set; }
        /// <summary>Three principal axes from a PCA over the Brep vertices, longest first.</summary>
        public Vec3[] PrincipalAxes { get; set; }
        public bool IsSolid { get; set; }
        public int FaceCount { get; set; }
        public int EdgeCount { get; set; }
        public string MassGroupId { get; set; }

        public List<MassAdjacency> AdjacentMasses { get; } = new List<MassAdjacency>();
    }

    public sealed class MassAdjacency
    {
        public string MassId { get; set; }
        /// <summary>abuts | sits-on | sits-under | unioned-with | coincident-face.</summary>
        public string Relationship { get; set; }
        public string Notes { get; set; }
    }

    /// <summary>A face of a Mass, labelled at query time (plan §3.2).</summary>
    public sealed class FaceView
    {
        public string FaceId { get; set; }
        public string MassId { get; set; }
        public int FaceIndex { get; set; }
        /// <summary>N|NE|E|SE|S|SW|W|NW|up|down|other.</summary>
        public string Orientation { get; set; } = SemanticVocabulary.OrientationOther;
        public List<string> Roles { get; } = new List<string>();
        public double Area { get; set; }
        public Vec3 Centroid { get; set; }
        public Vec3 Normal { get; set; }
        public bool IsPlanar { get; set; }
        public double ElevationMin { get; set; }
        public double ElevationMax { get; set; }
        /// <summary>The face's own extents. Unset on a hand-built view; <see cref="FaceFrame"/>
        /// falls back to area and elevation when it is.</summary>
        public BoxView Bbox { get; set; } = BoxView.Unset;
        /// <summary>planar | cylindrical | nurbs | …</summary>
        public string SurfaceType { get; set; } = "planar";
        public string ClassifiedBy { get; set; } = SemanticVocabulary.ByGeometryInference;

        public List<OpeningView> Openings { get; } = new List<OpeningView>();
        public List<int> BoundingEdgeIndices { get; } = new List<int>();
        public List<string> Notes { get; } = new List<string>();

        public double OpeningArea => Openings.Sum(o => o.Area);

        /// <summary>Only defined on facade-role faces — a WWR on a roof is meaningless.</summary>
        public double? WallWindowRatio =>
            Roles.Contains(SemanticVocabulary.RoleFacade) && Area > 1e-9
                ? (double?)(OpeningArea / Area)
                : null;

        /// <summary>Gross area, i.e. before openings are subtracted.</summary>
        public double SolidArea => Math.Max(0, Area - OpeningArea);

        public bool HasRole(string role) => Roles.Contains(role, StringComparer.Ordinal);
    }

    /// <summary>A significant edge of a Mass (plan §3.3).</summary>
    public sealed class EdgeView
    {
        public string EdgeId { get; set; }
        public string MassId { get; set; }
        public int EdgeIndex { get; set; }
        public string Role { get; set; } = SemanticVocabulary.EdgeOther;
        public double Length { get; set; }
        public Vec3 StartPoint { get; set; }
        public Vec3 EndPoint { get; set; }
        public bool IsLinear { get; set; }
        public List<int> AdjacentFaceIndices { get; } = new List<int>();
    }

    /// <summary>A hole in a Face (plan §3.4).</summary>
    public sealed class OpeningView : SemanticElement
    {
        public OpeningView() { Type = SemanticVocabulary.Opening; }

        public string MassId { get; set; }
        public string FaceId { get; set; }
        public int FaceIndex { get; set; } = -1;
        public string OpeningType { get; set; } = SemanticVocabulary.OpeningWindow;
        public double Width { get; set; }
        public double Height { get; set; }
        /// <summary>Above the Mass base, or the nearest Level when one is known.</summary>
        public double SillHeight { get; set; }
        public double Area { get; set; }
        public Vec3 Centroid { get; set; }
        /// <summary>[u, v] on the face's parameter-independent local frame: distance along the
        /// face's horizontal axis, then vertical from the face's bottom.</summary>
        public double[] CentroidOnFace { get; set; }
        public double? Depth { get; set; }
        /// <summary>subtracted | drawn-on-layer | explicit-tag.</summary>
        public string Origin { get; set; } = "subtracted";
        public bool IsEntry { get; set; }
        public string EntryType { get; set; }
    }

    /// <summary>A feature projecting past the Face below it (plan §3.5).</summary>
    public sealed class OverhangView : SemanticElement
    {
        public OverhangView() { Type = SemanticVocabulary.Overhang; }

        public string Subtype { get; set; } = "Other";
        public string AttachedToMassId { get; set; }
        public string AttachedToFaceId { get; set; }
        public double ProjectionDistance { get; set; }
        public double Width { get; set; }
        public double Thickness { get; set; }
        public double Area { get; set; }
        public Vec3 Centroid { get; set; }
        public BoxView Bbox { get; set; } = BoxView.Unset;
        /// <summary>separate-mass | extruded-edge | face-cantilever.</summary>
        public string Origin { get; set; } = SemanticVocabulary.OverhangFromSeparateMass;
    }

    /// <summary>The inward complement of an Overhang — a loggia or recessed entry (plan §3.6).</summary>
    public sealed class RecessView : SemanticElement
    {
        public RecessView() { Type = SemanticVocabulary.Recess; }

        public string MassId { get; set; }
        public double DepthIntoMass { get; set; }
        public string OpeningFaceId { get; set; }
        public List<string> InteriorFaceIds { get; } = new List<string>();
        public double Area { get; set; }
        public double Volume { get; set; }
        public BoxView Bbox { get; set; } = BoxView.Unset;
    }

    /// <summary>A room-sized or through-going void in a Mass (plan §3.7).</summary>
    public sealed class CutView : SemanticElement
    {
        public CutView() { Type = SemanticVocabulary.Cut; }

        public string MassId { get; set; }
        public double Volume { get; set; }
        public BoxView Bbox { get; set; } = BoxView.Unset;
        public bool TopOpen { get; set; }
        public bool BottomOpen { get; set; }
        public List<string> InteriorFaceIds { get; } = new List<string>();
        public Vec3 Centroid { get; set; }
    }

    /// <summary>Masses treated as one building or wing (plan §3.9).</summary>
    public sealed class MassGroupView : SemanticElement
    {
        public MassGroupView() { Type = SemanticVocabulary.MassGroup; }

        public List<string> MassIds { get; } = new List<string>();
        public double CombinedFootprintArea { get; set; }
        public double CombinedVolume { get; set; }
        public BoxView Bbox { get; set; } = BoxView.Unset;
        public string DominantFunction { get; set; }
    }

    /// <summary>A horizontal reference plane (plan §3.11). Usually inferred, not drawn.</summary>
    public sealed class LevelView : SemanticElement
    {
        public LevelView() { Type = SemanticVocabulary.Level; }

        public double Elevation { get; set; }
        public double? FloorToFloor { get; set; }
        public bool IsRoofLevel { get; set; }
        /// <summary>True when synthesized from a floor-to-floor default rather than drawn.</summary>
        public bool Inferred { get; set; }
    }

    /// <summary>Context the design responds to (plan §3.12).</summary>
    public sealed class SiteView : SemanticElement
    {
        public SiteView() { Type = SemanticVocabulary.Site; }

        public string SiteType { get; set; } = "Other";
        public BoxView Bbox { get; set; } = BoxView.Unset;
        public double? Area { get; set; }
        public double? Length { get; set; }
        public bool IsClosedCurve { get; set; }
        public Vec3 Centroid { get; set; }
    }

    /// <summary>One edge of the composition graph (plan §3.10).</summary>
    public sealed class CompositionRelationship
    {
        public string From { get; set; }
        public string To { get; set; }
        public string Type { get; set; }
        public string Notes { get; set; }

        public object ToJson() => new Dictionary<string, object>
        {
            { "from", From }, { "to", To }, { "type", Type }, { "notes", Notes }
        };
    }

    /// <summary>One operation read out of Rhino's history (plan §4.2's analyze_boolean_history).</summary>
    public sealed class BooleanOperationRecord
    {
        /// <summary>union | difference | intersection | push-pull | extrude | split | other.</summary>
        public string Kind { get; set; }
        public string CommandName { get; set; }
        public List<string> Inputs { get; } = new List<string>();
        public string ResultId { get; set; }
        public string Notes { get; set; }

        public object ToJson() => new Dictionary<string, object>
        {
            { "kind", Kind },
            { "commandName", CommandName },
            { "inputs", Inputs },
            { "resultId", ResultId },
            { "notes", Notes }
        };
    }

    /// <summary>
    /// Object-level classification output — the small, always-current tier of the two-tier
    /// cache (plan §6.2). Immutable once built: safe to read from any thread.
    /// </summary>
    public sealed class SemanticView
    {
        public List<MassView> Masses { get; } = new List<MassView>();
        /// <summary>Openings the architect drew as separate objects rather than boolean-cutting.</summary>
        public List<OpeningView> LooseOpenings { get; } = new List<OpeningView>();
        public List<OverhangView> LooseOverhangs { get; } = new List<OverhangView>();
        public List<MassGroupView> Groups { get; } = new List<MassGroupView>();
        public List<SiteView> SiteElements { get; } = new List<SiteView>();
        public List<LevelView> Levels { get; } = new List<LevelView>();

        public int UnclassifiedCount { get; set; }
        public List<string> UnclassifiedLayers { get; } = new List<string>();

        /// <summary>Firm or doc floor-to-floor default, 0 when unconfigured.</summary>
        public double FloorToFloorDefault { get; set; }
        public string UnitSystem { get; set; }
        /// <summary>Model-space tolerance, carried so pure code can do coincidence tests.</summary>
        public double Tolerance { get; set; } = 0.001;
        public long BuildMs { get; set; }
        public List<string> Notes { get; } = new List<string>();

        public MassView FindMass(string elementIdOrObjectId)
        {
            if (string.IsNullOrWhiteSpace(elementIdOrObjectId)) return null;
            return Masses.FirstOrDefault(m =>
                string.Equals(m.ElementId, elementIdOrObjectId, StringComparison.OrdinalIgnoreCase)
                || m.RhinoObjectIds.Any(id => string.Equals(id, elementIdOrObjectId, StringComparison.OrdinalIgnoreCase)));
        }

        public MassGroupView FindGroup(string elementId) =>
            Groups.FirstOrDefault(g => string.Equals(g.ElementId, elementId, StringComparison.OrdinalIgnoreCase)
                                       || string.Equals(g.Name, elementId, StringComparison.OrdinalIgnoreCase));

        public BoxView BuildingExtents()
        {
            var box = BoxView.Unset;
            foreach (var mass in Masses) box = box.Union(mass.Bbox);
            return box;
        }

        public double TotalVolume => Masses.Sum(m => m.Volume);
        public double TotalFootprintArea => Masses.Sum(m => m.FootprintArea);

        /// <summary>Lowest Mass base in the model — the grade datum every elevation is read against.</summary>
        public double GradeElevation
        {
            get
            {
                var solids = Masses.Where(m => m.Bbox != null && m.Bbox.IsValid).ToList();
                return solids.Count == 0 ? 0 : solids.Min(m => m.Bbox.Min.Z);
            }
        }
    }

    /// <summary>
    /// Geometry-level classification output for one Mass — the larger, on-demand tier of the
    /// two-tier cache (plan §5.1, §6.2).
    /// </summary>
    public sealed class MassGeometryView
    {
        public string MassId { get; set; }
        /// <summary>The Rhino object id the view was built from; a change to it invalidates the view.</summary>
        public string SourceObjectId { get; set; }

        public List<FaceView> Faces { get; } = new List<FaceView>();
        public List<EdgeView> Edges { get; } = new List<EdgeView>();
        public List<CutView> Cuts { get; } = new List<CutView>();
        public List<RecessView> Recesses { get; } = new List<RecessView>();
        public List<OverhangView> Overhangs { get; } = new List<OverhangView>();

        public bool HistoryAvailable { get; set; }
        public List<BooleanOperationRecord> History { get; } = new List<BooleanOperationRecord>();

        public long AnalyzeMs { get; set; }
        public List<string> Notes { get; } = new List<string>();

        public IEnumerable<OpeningView> AllOpenings => Faces.SelectMany(f => f.Openings);

        public FaceView FindFace(string faceId) =>
            Faces.FirstOrDefault(f => string.Equals(f.FaceId, faceId, StringComparison.OrdinalIgnoreCase));

        public FaceView FaceAt(int index) => Faces.FirstOrDefault(f => f.FaceIndex == index);

        public IEnumerable<FaceView> FacesWithRole(string role) => Faces.Where(f => f.HasRole(role));

        /// <summary>Area on faces the classifier could not label — reported so callers can say
        /// how much of the envelope their number excludes (plan §5.7).</summary>
        public double UnclassifiedFaceArea =>
            Faces.Where(f => f.HasRole(SemanticVocabulary.RoleUnclassified)).Sum(f => f.Area);
    }
}
