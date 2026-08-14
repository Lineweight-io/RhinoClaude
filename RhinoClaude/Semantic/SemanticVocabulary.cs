using System;
using System.Collections.Generic;
using System.Linq;

namespace RhinoClaude.Semantic
{
    /// <summary>
    /// The closed vocabulary of the semantic layer — plan §3's eleven element types plus the
    /// enums that hang off them.
    ///
    /// Deliberately free of any <c>using Rhino…</c>: the vocabulary and every rule expressed
    /// in terms of it are pure, which is what makes the classifier testable outside Rhino.
    /// Element types are hard-coded (plan §10.2 open question 2); only layer naming is pluggable.
    /// </summary>
    public static class SemanticVocabulary
    {
        // ── Element types (plan §3) ───────────────────────────────────

        public const string Mass = "Mass";
        public const string Face = "Face";
        public const string Edge = "Edge";
        public const string Opening = "Opening";
        public const string Overhang = "Overhang";
        public const string Recess = "Recess";
        public const string Cut = "Cut";
        public const string MassGroup = "MassGroup";
        public const string Composition = "Composition";
        public const string Level = "Level";
        public const string Site = "Site";

        /// <summary>The seven first-class types. Face and Edge are labels on Mass geometry.</summary>
        public static readonly string[] FirstClassTypes = { Mass, Face, Edge, Opening, Overhang, Recess, Cut };

        public static readonly string[] CompositionTypes = { MassGroup, Composition, Level, Site };

        public static readonly string[] AllTypes = FirstClassTypes.Concat(CompositionTypes).ToArray();

        /// <summary>Types a user can put on an object with <c>ClaudeSetElement</c>. Face/Edge/
        /// Composition are never object-level, so they are not offered.</summary>
        public static readonly string[] TaggableTypes = { Mass, Opening, Overhang, MassGroup, Level, Site };

        // ── Mass function (a property, not a subtype — plan §3.1) ─────

        public const string FunctionOffice = "Office";
        public const string FunctionResidential = "Residential";
        public const string FunctionRetail = "Retail";
        public const string FunctionInstitutional = "Institutional";
        public const string FunctionCommon = "Common";
        public const string FunctionOther = "Other";

        public static readonly string[] MassFunctions =
        {
            FunctionOffice, FunctionResidential, FunctionRetail,
            FunctionInstitutional, FunctionCommon, FunctionOther
        };

        // ── Face role and orientation (plan §3.2) ─────────────────────

        public const string RoleFacade = "facade";
        public const string RoleRoof = "roof";
        public const string RoleFloor = "floor";
        public const string RolePartyWall = "party-wall";
        public const string RoleInterior = "interior";
        public const string RoleUnclassified = "unclassified";

        public static readonly string[] FaceRoles =
        {
            RoleFacade, RoleRoof, RoleFloor, RolePartyWall, RoleInterior, RoleUnclassified
        };

        public const string OrientationUp = "up";
        public const string OrientationDown = "down";
        public const string OrientationOther = "other";

        /// <summary>The eight compass sectors, in the order a compass rose walks them.</summary>
        public static readonly string[] CompassSectors = { "N", "NE", "E", "SE", "S", "SW", "W", "NW" };

        public static readonly string[] Orientations =
            CompassSectors.Concat(new[] { OrientationUp, OrientationDown, OrientationOther }).ToArray();

        // ── Edge role (plan §3.3) ─────────────────────────────────────

        public const string EdgeParapet = "parapet";
        public const string EdgeOutsideCorner = "outside-corner";
        public const string EdgeInsideCorner = "inside-corner";
        public const string EdgeRoofRidge = "roof-ridge";
        public const string EdgeEave = "eave";
        public const string EdgeOther = "other";

        public static readonly string[] EdgeRoles =
        {
            EdgeParapet, EdgeOutsideCorner, EdgeInsideCorner, EdgeRoofRidge, EdgeEave, EdgeOther
        };

        // ── Opening subtype (plan §3.4) ───────────────────────────────

        public const string OpeningWindow = "Window";
        public const string OpeningDoor = "Door";
        public const string OpeningStorefront = "Storefront";
        public const string OpeningCurtainWall = "CurtainWall";
        public const string OpeningLouver = "Louver";
        public const string OpeningOther = "Other";

        public static readonly string[] OpeningTypes =
        {
            OpeningWindow, OpeningDoor, OpeningStorefront, OpeningCurtainWall, OpeningLouver, OpeningOther
        };

        public static readonly string[] EntryTypes = { "Main", "Secondary", "Service", "Emergency" };

        // ── Overhang subtype ──────────────────────────────────────────

        public static readonly string[] OverhangTypes = { "Canopy", "Eave", "Brise-Soleil", "Balcony", "Other" };

        public const string OverhangFromSeparateMass = "separate-mass";
        public const string OverhangFromExtrudedEdge = "extruded-edge";
        public const string OverhangFromFaceCantilever = "face-cantilever";

        // ── Site subtype (plan §3.12) ─────────────────────────────────

        public static readonly string[] SiteTypes =
        {
            "PropertyLine", "Topography", "ContextBuilding", "Street", "Curb", "Utility", "Other"
        };

        // ── Composition relationship (plan §3.10) ─────────────────────

        public const string RelAbuts = "abuts";
        public const string RelSitsOn = "sits-on";
        public const string RelSitsUnder = "sits-under";
        public const string RelCoincidentFace = "coincident-face";
        public const string RelWasUnionedWith = "was-unioned-with";
        public const string RelWasSubtractedFrom = "was-subtracted-from";

        // ── classifiedBy (plan §5.2's resolution rule) ────────────────

        public const string ByUserData = "user-data";
        public const string ByLearnedConvention = "learned-convention";
        public const string ByCanonical = "canonical";
        public const string ByGeometryInference = "geometry-inference";

        /// <summary>Lower is more trusted. Used to pick a winner when two paths both fire.</summary>
        public static int ConfidenceRank(string classifiedBy)
        {
            switch (classifiedBy)
            {
                case ByUserData: return 0;
                case ByLearnedConvention: return 1;
                case ByCanonical: return 2;
                case ByGeometryInference: return 3;
                default: return 4;
            }
        }

        // ── User-string keys (plan §5.2, §10.1 risk 10) ───────────────
        // Distinct from phase 1's `RC:` attribute namespace — no collision by construction.

        public const string KeyElementPrefix = "RhinoClaude:Element:";
        public const string KeyFaceRolePrefix = "RhinoClaude:FaceRole:";
        public const string KeyMassFunction = "RhinoClaude:MassFunction";
        public const string KeyOpeningType = "RhinoClaude:OpeningType";
        public const string KeyEntryType = "RhinoClaude:EntryType";
        public const string KeyMassGroup = "RhinoClaude:MassGroup";
        public const string KeySiteType = "RhinoClaude:SiteType";
        public const string KeyLevelElevation = "RhinoClaude:LevelElevation";
        public const string KeyElementId = "RhinoClaude:ElementId";

        /// <summary>Doc-string key the learned convention is persisted under.</summary>
        public const string DocKeyLayerConvention = "RhinoClaude:LayerConvention:v1";

        public static string FaceRoleKey(int faceIndex) => KeyFaceRolePrefix + faceIndex.ToString();

        // ── Validation helpers ────────────────────────────────────────

        /// <summary>Case-insensitive match against a closed set, returning the canonical spelling.</summary>
        public static string Normalize(string value, string[] allowed, string fallback = null)
        {
            if (string.IsNullOrWhiteSpace(value)) return fallback;
            foreach (var candidate in allowed)
                if (string.Equals(candidate, value.Trim(), StringComparison.OrdinalIgnoreCase))
                    return candidate;
            return fallback;
        }

        public static bool IsCompassSector(string orientation) =>
            orientation != null && CompassSectors.Contains(orientation, StringComparer.Ordinal);

        /// <summary>Human-readable list for a tool description or a prompt.</summary>
        public static string Join(IEnumerable<string> values) => string.Join(" | ", values);
    }
}
