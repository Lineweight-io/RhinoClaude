using System;
using System.Collections.Generic;

namespace RhinoClaude.Semantic
{
    /// <summary>What the analyzer measured about one Brep face, before any labels are applied.</summary>
    public sealed class FaceFacts
    {
        /// <summary>Outward normal at the face centroid. Need not be unitized.</summary>
        public Vec3 Normal { get; set; }
        public bool IsPlanar { get; set; } = true;
        public double ElevationMin { get; set; }
        public double ElevationMax { get; set; }
        /// <summary>Base of the parent Mass — what "at the bottom" is measured against.</summary>
        public double MassBaseElevation { get; set; }
        /// <summary>True when this face is coincident with a face of another Mass.</summary>
        public bool CoincidentWithAnotherMass { get; set; }
        /// <summary>True when this face bounds a subtracted void rather than the exterior.</summary>
        public bool BoundsInteriorVoid { get; set; }
        /// <summary>Tolerance for the "is it sitting on the Mass base" test, in model units.</summary>
        public double BaseTolerance { get; set; } = 0.01;
    }

    /// <summary>
    /// Plan §3.2 and §5.5: face orientation and role, from the normal, the elevation range and
    /// two adjacency flags. Pure — no Rhino, no document, no cache.
    ///
    /// Roles are cheap to recompute and never stored, so this runs per query rather than per edit.
    /// </summary>
    public static class FaceClassifier
    {
        /// <summary>|Z| below this is a vertical face; at or above it the face points up or down.</summary>
        public const double VerticalThreshold = 0.3;

        /// <summary>
        /// Faces whose |Z| lands within this of the threshold get both labels. Plan §5.5:
        /// "a face at the top of a facade wall that has a slight upward tilt might get
        /// ["facade", "roof"] — the agent decides."
        /// </summary>
        public const double AmbiguityBand = 0.1;

        // ── Orientation ───────────────────────────────────────────────

        /// <summary>
        /// N|NE|E|SE|S|SW|W|NW for a vertical face, up/down for a horizontal one, other when
        /// the normal is degenerate or the face is curved.
        ///
        /// A curved face has no single orientation — a cylindrical mass's exterior is one Brep
        /// face wrapping every compass point (plan risk #3). Calling it "other" is honest; the
        /// RhinoClaude:FaceRole tag is the override path when the user needs it labelled.
        /// </summary>
        public static string Orientation(Vec3 normal, bool isPlanar = true)
        {
            if (normal.Length <= 1e-9) return SemanticVocabulary.OrientationOther;

            var n = normal.Unit();

            if (!isPlanar)
            {
                // A curved face still reads as a roof or a floor when it is decisively
                // up- or down-facing (a domed roof); sideways-curved gets no compass label.
                if (n.Z >= VerticalThreshold) return SemanticVocabulary.OrientationUp;
                if (n.Z <= -VerticalThreshold) return SemanticVocabulary.OrientationDown;
                return SemanticVocabulary.OrientationOther;
            }

            if (n.Z >= VerticalThreshold) return SemanticVocabulary.OrientationUp;
            if (n.Z <= -VerticalThreshold) return SemanticVocabulary.OrientationDown;

            return CompassSector(n.X, n.Y);
        }

        /// <summary>
        /// The eight-point compass sector of an XY direction. North is +Y, east is +X, and
        /// each sector spans 45° centred on its cardinal — so a normal 20° east of north is
        /// still "N".
        /// </summary>
        public static string CompassSector(double x, double y)
        {
            if (Math.Abs(x) <= 1e-12 && Math.Abs(y) <= 1e-12)
                return SemanticVocabulary.OrientationOther;

            double bearing = Math.Atan2(x, y) * 180.0 / Math.PI;   // 0 = +Y = north, clockwise
            if (bearing < 0) bearing += 360.0;

            int sector = (int)Math.Round(bearing / 45.0) % 8;
            return SemanticVocabulary.CompassSectors[sector];
        }

        // ── Roles ─────────────────────────────────────────────────────

        /// <summary>
        /// The roles a face carries. Priority follows plan §5.5: an interior void face and a
        /// party wall are decided by adjacency and stop there; everything else falls out of
        /// the normal's Z. The list is never empty — an unlabelled face is explicitly
        /// "unclassified" rather than silently absent.
        /// </summary>
        public static List<string> Roles(FaceFacts facts, out string note)
        {
            note = null;
            var roles = new List<string>();

            if (facts == null)
            {
                roles.Add(SemanticVocabulary.RoleUnclassified);
                note = "No geometry facts were available for this face.";
                return roles;
            }

            if (facts.BoundsInteriorVoid)
            {
                roles.Add(SemanticVocabulary.RoleInterior);
                return roles;
            }

            if (facts.CoincidentWithAnotherMass)
            {
                roles.Add(SemanticVocabulary.RolePartyWall);
                return roles;
            }

            if (facts.Normal.Length <= 1e-9)
            {
                roles.Add(SemanticVocabulary.RoleUnclassified);
                note = "The face normal is degenerate, so no orientation could be read.";
                return roles;
            }

            var n = facts.Normal.Unit();
            double nz = n.Z;

            bool vertical = Math.Abs(nz) < VerticalThreshold;
            bool upward = nz >= VerticalThreshold;
            bool downward = nz <= -VerticalThreshold;

            // A curved face gets a role only when it is decisively up or down facing; a
            // sideways-curving surface is still a facade in every architectural sense.
            if (vertical) roles.Add(SemanticVocabulary.RoleFacade);
            if (upward) roles.Add(SemanticVocabulary.RoleRoof);

            if (downward)
            {
                if (IsAtMassBase(facts))
                {
                    roles.Add(SemanticVocabulary.RoleFloor);
                }
                else
                {
                    roles.Add(SemanticVocabulary.RoleUnclassified);
                    note = "Down-facing but well above the mass base — most likely a soffit under " +
                           "a cantilever or overhang rather than the building's underside.";
                }
            }

            // The ambiguity band: near the tilt threshold, hand the agent both readings rather
            // than picking one and being confidently wrong about a sloped-roof-meets-wall face.
            double distanceToThreshold = Math.Abs(Math.Abs(nz) - VerticalThreshold);
            if (distanceToThreshold <= AmbiguityBand)
            {
                if (nz > 0 && !roles.Contains(SemanticVocabulary.RoleFacade)) roles.Add(SemanticVocabulary.RoleFacade);
                if (nz > 0 && !roles.Contains(SemanticVocabulary.RoleRoof)) roles.Add(SemanticVocabulary.RoleRoof);
                if (nz > 0)
                    note = "Tilted near the facade/roof threshold; both readings are plausible.";
            }

            if (roles.Count == 0)
            {
                roles.Add(SemanticVocabulary.RoleUnclassified);
                note = "The face normal fits none of the facade / roof / floor bands.";
            }

            return roles;
        }

        private static bool IsAtMassBase(FaceFacts facts)
        {
            double tolerance = Math.Max(facts.BaseTolerance, 1e-9);
            return Math.Abs(facts.ElevationMax - facts.MassBaseElevation) <= tolerance
                || Math.Abs(facts.ElevationMin - facts.MassBaseElevation) <= tolerance;
        }

        // ── Slope, for the roof analysis ──────────────────────────────

        /// <summary>
        /// Roof slope as a percentage of run. A dead-flat roof is 0; a 45° pitch is 100.
        /// Vertical and degenerate normals return 0 rather than infinity.
        /// </summary>
        public static double SlopePercent(Vec3 normal)
        {
            if (normal.Length <= 1e-9) return 0;
            var n = normal.Unit();
            double horizontal = Math.Sqrt(n.X * n.X + n.Y * n.Y);
            double vertical = Math.Abs(n.Z);
            if (vertical <= 1e-9) return 0;
            return horizontal / vertical * 100.0;
        }

        /// <summary>
        /// Which way water runs off a roof face: the compass sector of the downhill direction,
        /// which is the horizontal component of the normal for an upward-facing face. A flat
        /// roof drains nowhere in particular and returns null.
        /// </summary>
        public static string DrainageDirection(Vec3 normal, double flatSlopeCutoffPercent = 1.0)
        {
            if (SlopePercent(normal) < flatSlopeCutoffPercent) return null;
            var n = normal.Unit();
            return CompassSector(n.X, n.Y);
        }

        /// <summary>The compass sector opposite a given one — "which way does the north face look from".</summary>
        public static string Opposite(string sector)
        {
            int index = Array.IndexOf(SemanticVocabulary.CompassSectors, sector);
            return index < 0 ? sector : SemanticVocabulary.CompassSectors[(index + 4) % 8];
        }
    }
}
