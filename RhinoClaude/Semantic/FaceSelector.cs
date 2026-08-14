using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace RhinoClaude.Semantic
{
    /// <summary>
    /// The plan's <c>FaceSelector</c> union (§4, preamble): a face named by id, by index, by
    /// orientation, by role, or by orientation and role together. Same shape as phase 1's
    /// <c>CameraShot</c> union in capture_views.
    ///
    /// Rev 2 adds one field the plan uses in §11 example 15 but does not spell out in the
    /// union: <c>elevationRange</c>, so "the south face between 0 and 12 feet" resolves to the
    /// ground-floor band of a stepped facade rather than the whole wall.
    /// </summary>
    public sealed class FaceSelector
    {
        public string FaceId { get; set; }
        public int? FaceIndex { get; set; }
        public string Orientation { get; set; }
        public string Role { get; set; }
        /// <summary>[zMin, zMax] in model units; a face qualifies when it overlaps the band.</summary>
        public double[] ElevationRange { get; set; }

        public bool IsEmpty =>
            string.IsNullOrWhiteSpace(FaceId) && FaceIndex == null
            && string.IsNullOrWhiteSpace(Orientation) && string.IsNullOrWhiteSpace(Role)
            && ElevationRange == null;

        public override string ToString()
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(FaceId)) parts.Add("faceId=" + FaceId);
            if (FaceIndex != null) parts.Add("faceIndex=" + FaceIndex);
            if (!string.IsNullOrWhiteSpace(Role)) parts.Add("role=" + Role);
            if (!string.IsNullOrWhiteSpace(Orientation)) parts.Add("orientation=" + Orientation);
            if (ElevationRange != null && ElevationRange.Length == 2)
                parts.Add("elevation=[" + ElevationRange[0] + "," + ElevationRange[1] + "]");
            return parts.Count == 0 ? "(empty)" : string.Join(", ", parts);
        }

        /// <summary>Parse the union out of a tool's JSON input. Unknown keys are ignored.</summary>
        public static FaceSelector Parse(JsonElement element)
        {
            var selector = new FaceSelector();
            if (element.ValueKind != JsonValueKind.Object) return selector;

            if (element.TryGetProperty("faceId", out var faceId) && faceId.ValueKind == JsonValueKind.String)
                selector.FaceId = faceId.GetString();

            if (element.TryGetProperty("faceIndex", out var faceIndex) && faceIndex.ValueKind == JsonValueKind.Number
                && faceIndex.TryGetInt32(out int index))
                selector.FaceIndex = index;

            if (element.TryGetProperty("orientation", out var orientation) && orientation.ValueKind == JsonValueKind.String)
                selector.Orientation = SemanticVocabulary.Normalize(
                    orientation.GetString(), SemanticVocabulary.Orientations);

            if (element.TryGetProperty("role", out var role) && role.ValueKind == JsonValueKind.String)
                selector.Role = SemanticVocabulary.Normalize(role.GetString(), SemanticVocabulary.FaceRoles);

            if (element.TryGetProperty("elevationRange", out var range) && range.ValueKind == JsonValueKind.Array
                && range.GetArrayLength() == 2)
            {
                var values = range.EnumerateArray()
                                  .Where(e => e.ValueKind == JsonValueKind.Number)
                                  .Select(e => e.GetDouble())
                                  .ToArray();
                if (values.Length == 2)
                    selector.ElevationRange = new[] { Math.Min(values[0], values[1]), Math.Max(values[0], values[1]) };
            }

            return selector;
        }
    }

    /// <summary>The outcome of resolving a selector — one face, or a reason there isn't one.</summary>
    public sealed class FaceResolution
    {
        public FaceView Face { get; set; }
        public List<FaceView> Candidates { get; } = new List<FaceView>();
        public string Error { get; set; }
        public string Note { get; set; }

        public bool Resolved => Face != null;
    }

    /// <summary>
    /// Resolves a <see cref="FaceSelector"/> against a Mass's geometry view. Entirely pure —
    /// <see cref="MassGeometryView"/> already holds everything a selector can filter on, so
    /// every write tool's face selection is testable without Rhino.
    /// </summary>
    public static class FaceSelectorResolver
    {
        /// <summary>
        /// Resolve to exactly one face. When several match, the largest wins — asked for "the
        /// north facade" of a stepped mass with three north-facing faces, the architect means
        /// the big one. The runner-up count goes into <c>Note</c> so the agent can see it
        /// made a choice on their behalf.
        /// </summary>
        public static FaceResolution Resolve(MassGeometryView geometry, FaceSelector selector)
        {
            var result = new FaceResolution();

            if (geometry == null)
            {
                result.Error = "No geometry is available for that mass.";
                return result;
            }

            if (selector == null || selector.IsEmpty)
            {
                result.Error = "A faceSelector is required. Give one of: {faceId}, {faceIndex}, " +
                               "{orientation}, {role}, or {role, orientation}.";
                return result;
            }

            if (!string.IsNullOrWhiteSpace(selector.FaceId))
            {
                var byId = geometry.FindFace(selector.FaceId);
                if (byId == null)
                {
                    result.Error = "No face with id '" + selector.FaceId + "' on this mass. " +
                                   "Call get_mass_faces for the current face ids — they change when the Brep does.";
                    return result;
                }
                result.Face = byId;
                return result;
            }

            if (selector.FaceIndex != null)
            {
                var byIndex = geometry.FaceAt(selector.FaceIndex.Value);
                if (byIndex == null)
                {
                    result.Error = "Face index " + selector.FaceIndex + " is out of range — this mass has " +
                                   geometry.Faces.Count + " faces (valid indices 0.." + (geometry.Faces.Count - 1) + ").";
                    return result;
                }
                result.Face = byIndex;
                return result;
            }

            var matches = Filter(geometry.Faces, selector).ToList();
            result.Candidates.AddRange(matches);

            if (matches.Count == 0)
            {
                result.Error = "No face on this mass matches " + selector + ". " + DescribeAvailable(geometry);
                return result;
            }

            result.Face = matches.OrderByDescending(f => f.Area).First();

            if (matches.Count > 1)
            {
                result.Note = matches.Count + " faces matched " + selector +
                              "; the largest (" + Math.Round(result.Face.Area, 1) + " area, index " +
                              result.Face.FaceIndex + ") was used. Pass a faceId to pick a different one.";
            }

            return result;
        }

        /// <summary>Every face matching the selector, unordered — the multi-match read path.</summary>
        public static IEnumerable<FaceView> Filter(IEnumerable<FaceView> faces, FaceSelector selector)
        {
            if (faces == null) yield break;

            foreach (var face in faces)
            {
                if (!string.IsNullOrWhiteSpace(selector.FaceId)
                    && !string.Equals(face.FaceId, selector.FaceId, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (selector.FaceIndex != null && face.FaceIndex != selector.FaceIndex.Value) continue;

                if (!string.IsNullOrWhiteSpace(selector.Orientation)
                    && !string.Equals(face.Orientation, selector.Orientation, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!string.IsNullOrWhiteSpace(selector.Role) && !face.HasRole(selector.Role)) continue;

                if (selector.ElevationRange != null && selector.ElevationRange.Length == 2)
                {
                    // Overlap, not containment: a facade band from 0 to 12 should catch a face
                    // spanning 0 to 40 as well as one spanning 2 to 10.
                    if (face.ElevationMax < selector.ElevationRange[0]) continue;
                    if (face.ElevationMin > selector.ElevationRange[1]) continue;
                }

                yield return face;
            }
        }

        private static string DescribeAvailable(MassGeometryView geometry)
        {
            var orientations = geometry.Faces.Select(f => f.Orientation).Distinct().OrderBy(o => o, StringComparer.Ordinal);
            var roles = geometry.Faces.SelectMany(f => f.Roles).Distinct().OrderBy(r => r, StringComparer.Ordinal);
            return "This mass has faces oriented [" + string.Join(", ", orientations) +
                   "] with roles [" + string.Join(", ", roles) + "].";
        }
    }

    /// <summary>The plan's <c>EdgeSelector</c> for fillet_edges: by id, by index, or by role.</summary>
    public sealed class EdgeSelector
    {
        public string EdgeId { get; set; }
        public int? EdgeIndex { get; set; }
        public string Role { get; set; }

        public bool IsEmpty => string.IsNullOrWhiteSpace(EdgeId) && EdgeIndex == null
                               && string.IsNullOrWhiteSpace(Role);

        public static EdgeSelector Parse(JsonElement element)
        {
            var selector = new EdgeSelector();
            if (element.ValueKind != JsonValueKind.Object) return selector;

            if (element.TryGetProperty("edgeId", out var edgeId) && edgeId.ValueKind == JsonValueKind.String)
                selector.EdgeId = edgeId.GetString();

            if (element.TryGetProperty("edgeIndex", out var edgeIndex) && edgeIndex.ValueKind == JsonValueKind.Number
                && edgeIndex.TryGetInt32(out int index))
                selector.EdgeIndex = index;

            if (element.TryGetProperty("role", out var role) && role.ValueKind == JsonValueKind.String)
                selector.Role = SemanticVocabulary.Normalize(role.GetString(), SemanticVocabulary.EdgeRoles);

            return selector;
        }

        public override string ToString()
        {
            if (!string.IsNullOrWhiteSpace(EdgeId)) return "edgeId=" + EdgeId;
            if (EdgeIndex != null) return "edgeIndex=" + EdgeIndex;
            if (!string.IsNullOrWhiteSpace(Role)) return "role=" + Role;
            return "(empty)";
        }

        /// <summary>
        /// Every edge matching any of the selectors, deduplicated. "Fillet all outside corners"
        /// is one selector value covering an arbitrary number of edges — plan §10.2 question 10.
        /// </summary>
        public static List<EdgeView> ResolveAll(MassGeometryView geometry, IEnumerable<EdgeSelector> selectors)
        {
            var resolved = new List<EdgeView>();
            if (geometry == null || selectors == null) return resolved;

            var seen = new HashSet<int>();
            foreach (var selector in selectors)
            {
                if (selector == null || selector.IsEmpty) continue;

                foreach (var edge in geometry.Edges)
                {
                    if (!string.IsNullOrWhiteSpace(selector.EdgeId)
                        && !string.Equals(edge.EdgeId, selector.EdgeId, StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (selector.EdgeIndex != null && edge.EdgeIndex != selector.EdgeIndex.Value) continue;
                    if (!string.IsNullOrWhiteSpace(selector.Role)
                        && !string.Equals(edge.Role, selector.Role, StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (seen.Add(edge.EdgeIndex)) resolved.Add(edge);
                }
            }

            return resolved;
        }
    }
}
