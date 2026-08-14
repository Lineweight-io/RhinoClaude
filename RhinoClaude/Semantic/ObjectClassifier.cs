using System;
using System.Collections.Generic;
using System.Linq;

namespace RhinoClaude.Semantic
{
    /// <summary>
    /// Everything the object-level classifier needs about one Rhino object, lifted out of
    /// RhinoCommon so the four-step resolution rule can be tested without a document.
    /// </summary>
    public sealed class ObjectFacts
    {
        public string ObjectId { get; set; }
        public string LayerFullPath { get; set; }
        public string Name { get; set; }

        /// <summary>User strings in the <c>RhinoClaude:</c> namespace, verbatim.</summary>
        public Dictionary<string, string> UserStrings { get; } =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public bool IsBrep { get; set; }
        public bool IsClosedSolid { get; set; }
        public bool IsCurve { get; set; }
        public bool IsClosedCurve { get; set; }
        public bool IsMesh { get; set; }
        public bool IsPlanarSurface { get; set; }

        public double Volume { get; set; }
        public double Area { get; set; }
        public BoxView Bbox { get; set; } = BoxView.Unset;

        /// <summary>Names of Rhino Groups the object belongs to — plan §3.9 heuristic 2.</summary>
        public List<string> GroupNames { get; } = new List<string>();

        public string UserString(string key) =>
            UserStrings.TryGetValue(key, out var value) ? value : null;
    }

    /// <summary>The classifier's verdict on one object.</summary>
    public sealed class ObjectClassification
    {
        /// <summary>Null when the object stays unclassified — a valid outcome, not an error.</summary>
        public string ElementType { get; set; }
        public string Subtype { get; set; }
        public string ClassifiedBy { get; set; }
        public double? Elevation { get; set; }
        public bool IsEntry { get; set; }
        public string EntryType { get; set; }
        public string MassGroupName { get; set; }
        public string Note { get; set; }

        public bool IsClassified => !string.IsNullOrEmpty(ElementType);

        public static ObjectClassification Unclassified(string note = null) =>
            new ObjectClassification { Note = note };
    }

    /// <summary>
    /// Plan §5.2's four-step resolution rule, object level:
    ///
    ///   1. explicit user-data tag  →  2. learned convention  →  3. shipped canonical
    ///   →  4. geometry inference
    ///
    /// Every verdict carries <c>classifiedBy</c> so the agent can tell a fact from a guess,
    /// and so a geometry-inferred Mass gets hedged rather than bulldozed.
    /// </summary>
    public static class ObjectClassifier
    {
        public static ObjectClassification Classify(
            ObjectFacts facts, ConventionResolver conventions, UnitContext units)
        {
            if (facts == null) return ObjectClassification.Unclassified();
            units = units ?? UnitContext.Feet();

            // ── Step 1: explicit tag. Trump card. ─────────────────────
            var tagged = FromUserData(facts);
            if (tagged != null) return tagged;

            // ── Steps 2 and 3: learned convention, then canonical. ────
            if (conventions != null)
            {
                var match = conventions.Resolve(facts.LayerFullPath);
                if (match.IsMatch)
                {
                    var fromLayer = FromConvention(facts, match);
                    if (fromLayer != null) return fromLayer;
                }
            }

            // Object-name prefix, plan §3.1 heuristic 3. Part of the shipped convention, so it
            // ranks with canonical rather than with geometry inference.
            var fromName = FromObjectName(facts);
            if (fromName != null) return fromName;

            // ── Step 4: geometry inference. ───────────────────────────
            return FromGeometry(facts, conventions, units);
        }

        // ── Step 1 ────────────────────────────────────────────────────

        /// <summary>
        /// Reads both spellings of the explicit tag: a bare
        /// <c>RhinoClaude:Element</c> = "Mass" pair, and the
        /// <c>RhinoClaude:Element:Mass</c> keyed form the plan uses for MassGroup
        /// (<c>RhinoClaude:Element:MassGroup:&lt;name&gt;</c>). Supporting both means a user
        /// who tagged by hand in either shape gets what they meant.
        /// </summary>
        public static ObjectClassification FromUserData(ObjectFacts facts)
        {
            string type = null;
            string payload = null;

            string bare = facts.UserString("RhinoClaude:Element");
            if (!string.IsNullOrWhiteSpace(bare))
            {
                var parts = bare.Split(':');
                type = SemanticVocabulary.Normalize(parts[0], SemanticVocabulary.AllTypes);
                if (parts.Length > 1) payload = string.Join(":", parts.Skip(1));
            }

            if (type == null)
            {
                foreach (var pair in facts.UserStrings)
                {
                    if (!pair.Key.StartsWith(SemanticVocabulary.KeyElementPrefix, StringComparison.OrdinalIgnoreCase))
                        continue;

                    string remainder = pair.Key.Substring(SemanticVocabulary.KeyElementPrefix.Length);
                    var parts = remainder.Split(':');
                    var candidate = SemanticVocabulary.Normalize(parts[0], SemanticVocabulary.AllTypes);
                    if (candidate == null) continue;

                    type = candidate;
                    payload = parts.Length > 1 ? string.Join(":", parts.Skip(1)) : null;
                    if (string.IsNullOrWhiteSpace(payload) && !string.IsNullOrWhiteSpace(pair.Value))
                        payload = pair.Value;
                    break;
                }
            }

            if (type == null) return null;

            var result = new ObjectClassification
            {
                ElementType = type,
                ClassifiedBy = SemanticVocabulary.ByUserData
            };

            switch (type)
            {
                case SemanticVocabulary.Mass:
                    result.Subtype = SemanticVocabulary.Normalize(
                        facts.UserString(SemanticVocabulary.KeyMassFunction) ?? payload,
                        SemanticVocabulary.MassFunctions,
                        SemanticVocabulary.FunctionOther);
                    result.MassGroupName = facts.UserString(SemanticVocabulary.KeyMassGroup);
                    break;

                case SemanticVocabulary.Opening:
                    result.Subtype = SemanticVocabulary.Normalize(
                        facts.UserString(SemanticVocabulary.KeyOpeningType) ?? payload,
                        SemanticVocabulary.OpeningTypes,
                        SemanticVocabulary.OpeningWindow);
                    result.EntryType = SemanticVocabulary.Normalize(
                        facts.UserString(SemanticVocabulary.KeyEntryType), SemanticVocabulary.EntryTypes);
                    result.IsEntry = result.EntryType != null;
                    break;

                case SemanticVocabulary.Overhang:
                    result.Subtype = SemanticVocabulary.Normalize(payload, SemanticVocabulary.OverhangTypes, "Other");
                    break;

                case SemanticVocabulary.Site:
                    result.Subtype = SemanticVocabulary.Normalize(
                        facts.UserString(SemanticVocabulary.KeySiteType) ?? payload,
                        SemanticVocabulary.SiteTypes, "Other");
                    break;

                case SemanticVocabulary.MassGroup:
                    result.MassGroupName = !string.IsNullOrWhiteSpace(payload) ? payload : facts.Name;
                    break;

                case SemanticVocabulary.Level:
                    result.Subtype = payload;
                    if (double.TryParse(facts.UserString(SemanticVocabulary.KeyLevelElevation),
                            System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out double elevation))
                        result.Elevation = elevation;
                    else if (facts.Bbox != null && facts.Bbox.IsValid)
                        result.Elevation = facts.Bbox.Min.Z;
                    break;

                default:
                    result.Subtype = payload;
                    break;
            }

            return result;
        }

        // ── Steps 2 and 3 ─────────────────────────────────────────────

        private static ObjectClassification FromConvention(ObjectFacts facts, ConventionMatch match)
        {
            // A MASS_ layer only makes an object a Mass if the object could be one. A curve on
            // MASS_Office is a construction line, not a building — classifying it as a Mass
            // would put a zero-volume entry in every program-area total.
            if (match.ElementType == SemanticVocabulary.Mass && !CouldBeMass(facts))
                return null;

            var result = new ObjectClassification
            {
                ElementType = match.ElementType,
                Subtype = match.Subtype,
                ClassifiedBy = match.ClassifiedBy,
                Elevation = match.Elevation
            };

            if (match.ElementType == SemanticVocabulary.Opening)
            {
                bool entryLayer = CanonicalConvention.IsEntryLayer(facts.LayerFullPath);
                result.EntryType = SemanticVocabulary.Normalize(
                    facts.UserString(SemanticVocabulary.KeyEntryType), SemanticVocabulary.EntryTypes)
                    ?? (entryLayer ? "Main" : null);
                result.IsEntry = result.EntryType != null;
            }

            if (match.ElementType == SemanticVocabulary.Mass)
            {
                result.MassGroupName = facts.UserString(SemanticVocabulary.KeyMassGroup);
                if (string.IsNullOrWhiteSpace(result.Subtype))
                    result.Subtype = SemanticVocabulary.FunctionOther;
            }

            if (match.ElementType == SemanticVocabulary.Level && result.Elevation == null
                && facts.Bbox != null && facts.Bbox.IsValid)
            {
                result.Elevation = facts.Bbox.Min.Z;
            }

            return result;
        }

        /// <summary>Plan §3.1 heuristic 3 — an object named "Mass: Office bar" says what it is.</summary>
        public static ObjectClassification FromObjectName(ObjectFacts facts)
        {
            if (string.IsNullOrWhiteSpace(facts.Name)) return null;

            string name = facts.Name.Trim();
            foreach (var prefix in new[] { "Mass:", "Massing:" })
            {
                if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                if (!CouldBeMass(facts)) return null;

                string tail = name.Substring(prefix.Length).Trim();
                return new ObjectClassification
                {
                    ElementType = SemanticVocabulary.Mass,
                    Subtype = FunctionFromFreeText(tail),
                    ClassifiedBy = SemanticVocabulary.ByCanonical,
                    Note = "Classified from the object name prefix '" + prefix + "'."
                };
            }

            return null;
        }

        /// <summary>Pick a function out of a free-text name — "Mass: Office bar" → Office.</summary>
        public static string FunctionFromFreeText(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return SemanticVocabulary.FunctionOther;
            foreach (var function in SemanticVocabulary.MassFunctions)
            {
                if (function == SemanticVocabulary.FunctionOther) continue;
                if (text.IndexOf(function, StringComparison.OrdinalIgnoreCase) >= 0) return function;
            }
            return SemanticVocabulary.FunctionOther;
        }

        // ── Step 4 ────────────────────────────────────────────────────

        /// <summary>
        /// Plan §3.1 heuristic 4: a closed Brep, on a layer that is not site / opening /
        /// overhang, above the volume threshold, is a Mass candidate. Marked
        /// <c>geometry-inference</c> so the agent knows to hedge and to confirm before a
        /// destructive move (plan §4.7's last rule).
        /// </summary>
        public static ObjectClassification FromGeometry(
            ObjectFacts facts, ConventionResolver conventions, UnitContext units)
        {
            if (!CouldBeMass(facts))
                return ObjectClassification.Unclassified();

            // A layer that already resolved to a non-Mass category is an explicit "not a
            // building" — inference must not overrule it.
            if (conventions != null)
            {
                var match = conventions.Resolve(facts.LayerFullPath);
                if (match.IsMatch && match.ElementType != SemanticVocabulary.Mass)
                    return ObjectClassification.Unclassified();
            }
            else if (CanonicalConvention.IsNonMassCategory(facts.LayerFullPath))
            {
                return ObjectClassification.Unclassified();
            }

            if (facts.Volume < units.MinMassVolume)
            {
                return ObjectClassification.Unclassified(
                    "Closed solid but only " + Math.Round(units.VolumeToCubicFeet(facts.Volume)) +
                    " ft³ — below the mass threshold, so more likely a component than a building mass.");
            }

            return new ObjectClassification
            {
                ElementType = SemanticVocabulary.Mass,
                Subtype = FunctionFromFreeText(facts.Name) ,
                ClassifiedBy = SemanticVocabulary.ByGeometryInference,
                MassGroupName = facts.UserString(SemanticVocabulary.KeyMassGroup),
                Note = "No tag or layer convention matched; classified as a Mass from geometry " +
                       "alone (closed solid above the volume threshold). Confirm before any " +
                       "destructive change."
            };
        }

        /// <summary>A Mass is a solid Brep. Curves, points and open surfaces never qualify.</summary>
        public static bool CouldBeMass(ObjectFacts facts) =>
            facts != null && facts.IsClosedSolid && facts.Volume > 0;
    }
}
