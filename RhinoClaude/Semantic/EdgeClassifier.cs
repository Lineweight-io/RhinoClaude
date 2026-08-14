using System;
using System.Collections.Generic;
using System.Linq;

namespace RhinoClaude.Semantic
{
    /// <summary>What the analyzer measured about one Brep edge and the faces meeting at it.</summary>
    public sealed class EdgeFacts
    {
        /// <summary>Roles of the first adjacent face. Never null; empty means "no face".</summary>
        public List<string> FaceARoles { get; set; } = new List<string>();
        public List<string> FaceBRoles { get; set; } = new List<string>();

        public Vec3 FaceANormal { get; set; }
        public Vec3 FaceBNormal { get; set; }
        public Vec3 FaceACentroid { get; set; }
        public Vec3 FaceBCentroid { get; set; }

        public string FaceAOrientation { get; set; }
        public string FaceBOrientation { get; set; }

        /// <summary>Number of faces the edge bounds. 1 means a naked edge — the eave case.</summary>
        public int AdjacentFaceCount { get; set; } = 2;

        public Vec3 Midpoint { get; set; }
        public double Length { get; set; }
        public bool IsLinear { get; set; } = true;
    }

    /// <summary>
    /// Plan §3.3: an Edge's role from the roles of the faces that meet at it. Cheap
    /// post-processing on top of face labelling, recomputed on demand rather than cached.
    ///
    /// Only edges the architect would actually refer to get a role — parapets, corners,
    /// ridges, eaves. Everything else is "other" and stays out of the way.
    /// </summary>
    public static class EdgeClassifier
    {
        /// <summary>Normals closer than this in angle are the same plane, not a corner.</summary>
        public const double CoplanarDotThreshold = 0.999;

        public static string Role(EdgeFacts facts)
        {
            if (facts == null) return SemanticVocabulary.EdgeOther;

            bool aRoof = Has(facts.FaceARoles, SemanticVocabulary.RoleRoof);
            bool bRoof = Has(facts.FaceBRoles, SemanticVocabulary.RoleRoof);
            bool aFacade = Has(facts.FaceARoles, SemanticVocabulary.RoleFacade);
            bool bFacade = Has(facts.FaceBRoles, SemanticVocabulary.RoleFacade);

            // A naked edge on a roof face is an eave: roof meeting empty space, with no
            // facade coming up to it.
            if (facts.AdjacentFaceCount < 2)
                return aRoof || bRoof ? SemanticVocabulary.EdgeEave : SemanticVocabulary.EdgeOther;

            // Roof + facade. Parapet when the roof sits above the wall it meets; eave when the
            // roof overhangs past the wall below, which reads as the roof being the lower of
            // the two at the edge.
            if ((aRoof && bFacade) || (bRoof && aFacade))
            {
                double roofZ = aRoof ? facts.FaceACentroid.Z : facts.FaceBCentroid.Z;
                double facadeZ = aRoof ? facts.FaceBCentroid.Z : facts.FaceACentroid.Z;
                return roofZ > facadeZ ? SemanticVocabulary.EdgeParapet : SemanticVocabulary.EdgeEave;
            }

            if (aRoof && bRoof)
            {
                // Two roof planes meeting. A ridge is convex — the classic gable. A concave
                // meeting is a valley, which the plan's vocabulary folds into "other".
                return IsConvex(facts) ? SemanticVocabulary.EdgeRoofRidge : SemanticVocabulary.EdgeOther;
            }

            if (aFacade && bFacade)
            {
                if (SameDirection(facts)) return SemanticVocabulary.EdgeOther;   // a seam, not a corner
                return IsConvex(facts)
                    ? SemanticVocabulary.EdgeOutsideCorner
                    : SemanticVocabulary.EdgeInsideCorner;
            }

            return SemanticVocabulary.EdgeOther;
        }

        /// <summary>
        /// Convexity of the dihedral at the edge. For a convex edge each face's centroid lies
        /// behind the other face's plane — the solid is inside the wedge. For a concave edge
        /// each centroid is in front of the other's plane.
        ///
        /// Symmetric so a sliver face on one side cannot flip the answer on its own.
        /// </summary>
        public static bool IsConvex(EdgeFacts facts)
        {
            var nA = facts.FaceANormal.Unit();
            var nB = facts.FaceBNormal.Unit();
            if (nA.Length <= 1e-9 || nB.Length <= 1e-9) return true;

            var p = facts.Midpoint;
            double signal = Vec3.Dot(nA, facts.FaceBCentroid - p) + Vec3.Dot(nB, facts.FaceACentroid - p);
            return signal < 0;
        }

        /// <summary>True when both faces point the same way — a tangent seam rather than a corner.</summary>
        public static bool SameDirection(EdgeFacts facts)
        {
            var nA = facts.FaceANormal.Unit();
            var nB = facts.FaceBNormal.Unit();
            if (nA.Length <= 1e-9 || nB.Length <= 1e-9) return false;
            return Vec3.Dot(nA, nB) >= CoplanarDotThreshold;
        }

        private static bool Has(IEnumerable<string> roles, string role) =>
            roles != null && roles.Contains(role, StringComparer.Ordinal);
    }
}
