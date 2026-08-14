using System;
using System.Collections.Generic;
using System.Linq;

namespace RhinoClaude.Semantic
{
    /// <summary>
    /// Plan §3.10: the graph of how Masses relate — which sits on which, which abut, which
    /// read as one unioned form. Computed at query time from bounding boxes; nothing here is
    /// stored, and nothing here needs Rhino.
    ///
    /// Bounding boxes rather than exact Brep intersection is a deliberate trade. At SD massing
    /// scale the boxes are the form, the answers are the ones an architect would give, and the
    /// cost stays inside the plan's &lt;150 ms object-level budget on a doc with dozens of masses.
    /// </summary>
    public static class CompositionAnalyzer
    {
        /// <summary>Fill each Mass's <see cref="MassView.AdjacentMasses"/> in place.</summary>
        public static void FillAdjacencies(IReadOnlyList<MassView> masses, UnitContext units)
        {
            if (masses == null) return;
            units = units ?? UnitContext.Feet();

            foreach (var mass in masses) mass.AdjacentMasses.Clear();

            for (int i = 0; i < masses.Count; i++)
            {
                for (int j = i + 1; j < masses.Count; j++)
                {
                    var relation = Relate(masses[i], masses[j], units);
                    if (relation == null) continue;

                    masses[i].AdjacentMasses.Add(new MassAdjacency
                    {
                        MassId = masses[j].ElementId,
                        Relationship = relation,
                        Notes = null
                    });

                    masses[j].AdjacentMasses.Add(new MassAdjacency
                    {
                        MassId = masses[i].ElementId,
                        Relationship = Invert(relation),
                        Notes = null
                    });
                }
            }
        }

        /// <summary>
        /// How A stands to B, or null when they are nowhere near each other.
        ///
        /// sits-on when A's base meets B's top with plan overlap; unioned-with when the solids
        /// interpenetrate enough to read as one form; abuts when they touch side to side.
        /// </summary>
        public static string Relate(MassView a, MassView b, UnitContext units)
        {
            if (a?.Bbox == null || b?.Bbox == null || !a.Bbox.IsValid || !b.Bbox.IsValid) return null;

            double tolerance = units.AdjacencyTolerance;
            if (!a.Bbox.Intersects(b.Bbox, tolerance)) return null;

            double planOverlap = OverlapArea(a.Bbox, b.Bbox);
            bool sharesPlan = planOverlap > units.Area(1.0);

            if (sharesPlan && Math.Abs(a.Bbox.Min.Z - b.Bbox.Max.Z) <= tolerance)
                return SemanticVocabulary.RelSitsOn;
            if (sharesPlan && Math.Abs(b.Bbox.Min.Z - a.Bbox.Max.Z) <= tolerance)
                return SemanticVocabulary.RelSitsUnder;

            double overlapVolume = OverlapVolume(a.Bbox, b.Bbox);
            double smaller = Math.Min(Volume(a.Bbox), Volume(b.Bbox));

            // Interpenetrating by more than a hair: these read as one form even though they are
            // still two Breps. The architect has decided they are one building; the boolean
            // union may simply not have happened yet.
            if (smaller > 0 && overlapVolume / smaller > 0.02)
                return SemanticVocabulary.RelWasUnionedWith;

            return SemanticVocabulary.RelAbuts;
        }

        public static string Invert(string relationship)
        {
            switch (relationship)
            {
                case SemanticVocabulary.RelSitsOn: return SemanticVocabulary.RelSitsUnder;
                case SemanticVocabulary.RelSitsUnder: return SemanticVocabulary.RelSitsOn;
                default: return relationship;
            }
        }

        /// <summary>The composition graph as the tools report it, one edge per relationship.</summary>
        public static List<CompositionRelationship> Relationships(
            IReadOnlyList<MassView> masses, UnitContext units)
        {
            var edges = new List<CompositionRelationship>();
            if (masses == null) return edges;
            units = units ?? UnitContext.Feet();

            for (int i = 0; i < masses.Count; i++)
            {
                for (int j = i + 1; j < masses.Count; j++)
                {
                    var relation = Relate(masses[i], masses[j], units);
                    if (relation == null) continue;

                    edges.Add(new CompositionRelationship
                    {
                        From = masses[i].ElementId,
                        To = masses[j].ElementId,
                        Type = relation,
                        Notes = Describe(masses[i], masses[j], relation)
                    });
                }
            }

            return edges;
        }

        private static string Describe(MassView a, MassView b, string relation)
        {
            string an = a.Name ?? a.Function;
            string bn = b.Name ?? b.Function;
            switch (relation)
            {
                case SemanticVocabulary.RelSitsOn: return an + " sits atop " + bn + ".";
                case SemanticVocabulary.RelSitsUnder: return bn + " sits atop " + an + ".";
                case SemanticVocabulary.RelWasUnionedWith:
                    return an + " and " + bn + " interpenetrate and read as one form.";
                default: return an + " abuts " + bn + ".";
            }
        }

        // ── Grouping (plan §3.9) ──────────────────────────────────────

        /// <summary>
        /// Derive MassGroups. Explicit tags win; then Rhino Group membership; then a shared
        /// parent layer. Masses in no group are left ungrouped rather than swept into a
        /// catch-all — a group of one carries no information.
        /// </summary>
        public static List<MassGroupView> DeriveGroups(
            IReadOnlyList<MassView> masses,
            IReadOnlyDictionary<string, string> explicitGroupNames,
            IReadOnlyDictionary<string, List<string>> rhinoGroupMembership)
        {
            var groups = new List<MassGroupView>();
            if (masses == null || masses.Count == 0) return groups;

            var assigned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void Emit(string name, IEnumerable<MassView> members, string source)
            {
                var list = members.Where(m => !assigned.Contains(m.ElementId)).ToList();
                if (list.Count < 2) return;

                var group = new MassGroupView
                {
                    ElementId = "group:" + name,
                    Name = name,
                    ClassifiedBy = source
                };
                foreach (var mass in list)
                {
                    group.MassIds.Add(mass.ElementId);
                    assigned.Add(mass.ElementId);
                    mass.MassGroupId = group.ElementId;
                    group.Bbox = group.Bbox.Union(mass.Bbox);
                }
                group.CombinedVolume = list.Sum(m => m.Volume);
                group.CombinedFootprintArea = list.Sum(m => m.FootprintArea);
                group.DominantFunction = DominantFunction(list);
                groups.Add(group);
            }

            // 1. Explicit RhinoClaude:MassGroup tags.
            if (explicitGroupNames != null)
            {
                foreach (var byName in explicitGroupNames
                             .Where(kv => !string.IsNullOrWhiteSpace(kv.Value))
                             .GroupBy(kv => kv.Value, StringComparer.OrdinalIgnoreCase))
                {
                    var members = byName.Select(kv => masses.FirstOrDefault(m => m.ElementId == kv.Key))
                                        .Where(m => m != null);
                    Emit(byName.Key, members, SemanticVocabulary.ByUserData);
                }
            }

            // 2. Rhino Group membership.
            if (rhinoGroupMembership != null)
            {
                foreach (var pair in rhinoGroupMembership)
                {
                    var members = pair.Value.Select(id => masses.FirstOrDefault(m => m.ElementId == id))
                                            .Where(m => m != null);
                    Emit(pair.Key, members, SemanticVocabulary.ByUserData);
                }
            }

            // 3. Common parent layer.
            foreach (var byParent in masses.Where(m => !assigned.Contains(m.ElementId))
                                           .GroupBy(m => ParentLayer(m.Layer), StringComparer.OrdinalIgnoreCase))
            {
                if (string.IsNullOrEmpty(byParent.Key)) continue;
                Emit(byParent.Key, byParent, SemanticVocabulary.ByCanonical);
            }

            return groups;
        }

        public static string ParentLayer(string layerFullPath)
        {
            var segments = CanonicalConvention.Segments(layerFullPath);
            return segments.Length < 2
                ? null
                : string.Join(CanonicalConvention.PathSeparator, segments.Take(segments.Length - 1));
        }

        /// <summary>The function carrying the most volume — what the group "is", in one word.</summary>
        public static string DominantFunction(IEnumerable<MassView> masses)
        {
            var list = masses?.ToList();
            if (list == null || list.Count == 0) return SemanticVocabulary.FunctionOther;

            return list.GroupBy(m => m.Function ?? SemanticVocabulary.FunctionOther)
                       .OrderByDescending(g => g.Sum(m => m.Volume))
                       .ThenBy(g => g.Key, StringComparer.Ordinal)
                       .First().Key;
        }

        // ── Box maths ─────────────────────────────────────────────────

        public static double OverlapArea(BoxView a, BoxView b)
        {
            double dx = Math.Min(a.Max.X, b.Max.X) - Math.Max(a.Min.X, b.Min.X);
            double dy = Math.Min(a.Max.Y, b.Max.Y) - Math.Max(a.Min.Y, b.Min.Y);
            return dx <= 0 || dy <= 0 ? 0 : dx * dy;
        }

        public static double OverlapVolume(BoxView a, BoxView b)
        {
            double dz = Math.Min(a.Max.Z, b.Max.Z) - Math.Max(a.Min.Z, b.Min.Z);
            return dz <= 0 ? 0 : OverlapArea(a, b) * dz;
        }

        public static double Volume(BoxView box) =>
            !box.IsValid ? 0 : Math.Max(0, box.Size.X) * Math.Max(0, box.Size.Y) * Math.Max(0, box.Size.Z);
    }
}
