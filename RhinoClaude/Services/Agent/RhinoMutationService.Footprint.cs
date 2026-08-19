using System;
using System.Collections.Generic;
using System.Linq;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;
using RhinoClaude.Semantic;

namespace RhinoClaude.Services.Agent
{
    /// <summary>
    /// <c>extract_footprint_from_curves</c>: the CAD-linework case.
    ///
    /// An imported floor plan arrives as hundreds or thousands of separate curves — wall
    /// lines, door swings, dimension strings, hatch — and the building outline is a subset of
    /// them that happens to close. The failure this tool exists to prevent is the agent
    /// giving up and extruding the selection's axis-aligned bounding box, which turns any
    /// non-rectangular plan into a rectangle without ever saying it did.
    ///
    /// Two passes. Rhino's own <c>Curve.JoinCurves</c> first, because it handles arcs,
    /// splines and polycurves properly; then, only if nothing closed, a tolerant re-chaining
    /// of the tessellated points in <see cref="FootprintExtractor"/>, which bridges the small
    /// gaps that imported linework is full of. Both passes hand the choice of which closed
    /// loop is the outer boundary to the same largest-area rule.
    /// </summary>
    public sealed partial class RhinoMutationService
    {
        public object ExtractFootprintFromCurves(List<string> ids)
        {
            if (ids == null || ids.Count == 0)
                throw new ArgumentException(
                    "ids must contain at least one curve id. Call get_selection first if the user " +
                    "pointed at something rather than naming it.");

            var curves = new List<Curve>();
            var skipped = new List<string>();
            ObjectAttributes sourceAttributes = null;

            // One bad id in a 1,900-object selection should not lose the other 1,899, so
            // everything that will not resolve to a curve becomes a diagnostic rather than a throw.
            foreach (var idText in ids)
            {
                RhinoObject obj;
                try
                {
                    obj = _query.RequireObject(idText);
                }
                catch (ArgumentException ex)
                {
                    skipped.Add(Describe(idText, ex.Message));
                    continue;
                }

                var curve = obj.Geometry as Curve;
                if (curve == null)
                {
                    skipped.Add(Describe(idText, "a " + obj.ObjectType + ", not a curve"));
                    continue;
                }

                if (sourceAttributes == null) sourceAttributes = obj.Attributes.Duplicate();
                curves.Add(curve.DuplicateCurve());
            }

            if (curves.Count == 0)
                throw new ArgumentException(
                    "None of the " + ids.Count + " ids supplied resolved to a curve, so there is no " +
                    "linework to join. " + SkipSummary(skipped));

            return InUndoRecord("extract_footprint_from_curves", doc =>
            {
                double tolerance = doc.ModelAbsoluteTolerance;

                var joined = Curve.JoinCurves(curves, tolerance) ?? new Curve[0];
                var closed = joined.Where(c => c != null && c.IsClosed).ToList();

                Curve outer;
                string notes;

                if (closed.Count > 0)
                {
                    var areas = closed.Select(c => PlanarArea(c, tolerance)).ToList();
                    outer = closed[FootprintExtractor.ChooseOuterIndex(areas)];
                    notes = FootprintExtractor.BuildNotes(
                        curves.Count, skipped.Count, closed.Count,
                        openChainCount: joined.Length - closed.Count, viaFallback: false);
                }
                else
                {
                    var extraction = FootprintExtractor.Assemble(
                        curves.Select(c => Tessellate(c, tolerance)).Where(p => p.Count >= 2), tolerance);

                    if (!extraction.Success)
                        throw new InvalidOperationException(
                            extraction.Error + " " + SkipSummary(skipped));

                    outer = new PolylineCurve(
                        extraction.Outer.Vertices
                            .Select(v => new Point3d(v.X, v.Y, v.Z))
                            .Concat(new[] { new Point3d(extraction.Outer.Vertices[0].X,
                                                        extraction.Outer.Vertices[0].Y,
                                                        extraction.Outer.Vertices[0].Z) }));

                    notes = FootprintExtractor.BuildNotes(
                        curves.Count, skipped.Count,
                        extraction.Inner.Count + 1, extraction.OpenChainCount, viaFallback: true);
                }

                var attributes = sourceAttributes ?? doc.CreateDefaultAttributes();
                var footprintId = doc.Objects.AddCurve(outer, attributes);
                if (footprintId == Guid.Empty)
                    throw new InvalidOperationException(
                        "Rhino refused to add the extracted footprint to the document.");

                var box = outer.GetBoundingBox(true);
                if (box.IsValid && box.Max.Z - box.Min.Z > tolerance)
                    notes += " The linework is not flat — it spans " +
                             RhinoQueryService.Round(box.Max.Z - box.Min.Z) +
                             " in Z, so the footprint carries that spread. Flatten it before " +
                             "extruding if a level base matters.";

                return (object)new Dictionary<string, object>
                {
                    { "footprintId", footprintId.ToString() },
                    { "bounds", RhinoQueryService.Bbox(box) },
                    { "vertexCount", VertexCount(outer) },
                    { "perimeter", RhinoQueryService.Round(outer.GetLength()) },
                    { "area", RhinoQueryService.Round(PlanarArea(outer, tolerance)) },
                    { "notes", notes }
                };
            });
        }

        // ── helpers ───────────────────────────────────────────────────

        /// <summary>
        /// Enclosed area of a closed planar curve. Falls back to the shoelace over the curve's
        /// own tessellation when Rhino declines — a polycurve that closes but is fractionally
        /// off-plane still has a footprint worth reporting.
        /// </summary>
        private static double PlanarArea(Curve curve, double tolerance)
        {
            if (curve == null) return 0;

            if (curve.IsClosed)
            {
                var properties = AreaMassProperties.Compute(curve);
                if (properties != null && properties.Area > 0) return properties.Area;
            }

            return FootprintExtractor.RingArea(Tessellate(curve, tolerance));
        }

        /// <summary>
        /// The curve as points. Polylines come back exactly; anything with real curvature is
        /// tessellated, because the fallback assembler only knows about straight runs.
        /// </summary>
        private static List<Vec3> Tessellate(Curve curve, double tolerance)
        {
            var points = new List<Vec3>();
            if (curve == null) return points;

            Polyline polyline;
            if (!curve.TryGetPolyline(out polyline))
            {
                var approximation = curve.ToPolyline(
                    tolerance, RhinoMath.DefaultAngleTolerance, 0, 0);
                if (approximation != null) polyline = approximation.ToPolyline();
            }

            if (polyline != null)
            {
                foreach (var p in polyline) points.Add(new Vec3(p.X, p.Y, p.Z));
                return points;
            }

            // Last resort for a curve Rhino will not tessellate: its own end points.
            points.Add(ToVec(curve.PointAtStart));
            points.Add(ToVec(curve.PointAtEnd));
            return points;
        }

        private static Vec3 ToVec(Point3d p) => new Vec3(p.X, p.Y, p.Z);

        /// <summary>
        /// Corners for a polyline footprint, spans for anything curved. The distinction matters
        /// to the caller: a vertexCount of 4 means a rectangle, 4 spans could be a rounded square.
        /// </summary>
        private static int VertexCount(Curve curve)
        {
            Polyline polyline;
            if (curve.TryGetPolyline(out polyline))
                return Math.Max(0, polyline.Count - (polyline.IsClosed ? 1 : 0));
            return curve.SpanCount;
        }

        private static string Describe(string idText, string reason) =>
            RhinoClaude.Agent.ToolJson.Safe(idText) + " (" + reason + ")";

        private static string SkipSummary(List<string> skipped)
        {
            if (skipped.Count == 0) return string.Empty;

            const int shown = 3;
            var summary = "Skipped " + skipped.Count + " object" + (skipped.Count == 1 ? "" : "s") +
                          ": " + string.Join(", ", skipped.Take(shown));
            if (skipped.Count > shown) summary += ", and " + (skipped.Count - shown) + " more";
            return summary + ".";
        }
    }
}
