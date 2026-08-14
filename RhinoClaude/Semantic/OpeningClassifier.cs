using System;

namespace RhinoClaude.Semantic
{
    /// <summary>Dimensions of a hole in a face, in model units.</summary>
    public sealed class OpeningFacts
    {
        public double Width { get; set; }
        public double Height { get; set; }
        /// <summary>Bottom of the opening above the base of the face it sits in.</summary>
        public double SillHeight { get; set; }
        public double Area { get; set; }
    }

    /// <summary>
    /// Plan §3.4 heuristic 5: guess an Opening's subtype from its dimensions when no layer or
    /// tag says what it is. Every result from here is a guess, and the classifier marks it as
    /// such so the agent hedges rather than asserting.
    ///
    /// The thresholds are architectural rules of thumb, expressed in feet and converted by
    /// <see cref="UnitContext"/> so they hold in a millimetre document too.
    /// </summary>
    public static class OpeningClassifier
    {
        /// <summary>A sill this close to the floor reads as "at floor level".</summary>
        public const double FloorLevelSillFeet = 0.5;

        /// <summary>Openings this tall or taller, starting at the floor, are doors not windows.</summary>
        public const double DoorHeightFeet = 6.0;

        /// <summary>A floor-level opening this wide reads as a storefront rather than a door.</summary>
        public const double StorefrontWidthFeet = 8.0;

        /// <summary>Above this area a floor-level opening is a storefront whatever its width.</summary>
        public const double StorefrontAreaSquareFeet = 60.0;

        /// <summary>Above this area an opening is a curtain wall — a whole bay, not an aperture.</summary>
        public const double CurtainWallAreaSquareFeet = 400.0;

        public static string InferType(OpeningFacts facts, UnitContext units, out string note)
        {
            note = null;
            if (facts == null || units == null) return SemanticVocabulary.OpeningOther;

            double width = units.ToFeet(facts.Width);
            double height = units.ToFeet(facts.Height);
            double sill = units.ToFeet(facts.SillHeight);
            double area = units.AreaToSquareFeet(facts.Area > 0 ? facts.Area : facts.Width * facts.Height);

            if (area >= CurtainWallAreaSquareFeet)
            {
                note = "Inferred from size alone: " + Describe(area) + " of opening in one piece reads " +
                       "as a curtain-wall bay rather than a punched opening.";
                return SemanticVocabulary.OpeningCurtainWall;
            }

            bool atFloor = sill <= FloorLevelSillFeet;

            if (atFloor && height >= DoorHeightFeet)
            {
                if (width >= StorefrontWidthFeet || area >= StorefrontAreaSquareFeet)
                {
                    note = "Inferred: full-height opening at floor level, " + Round(width) +
                           " ft wide — storefront rather than a door.";
                    return SemanticVocabulary.OpeningStorefront;
                }

                note = "Inferred: full-height opening at floor level, " + Round(width) + " ft wide — a door.";
                return SemanticVocabulary.OpeningDoor;
            }

            if (atFloor && area >= StorefrontAreaSquareFeet)
            {
                note = "Inferred: large opening starting at floor level — storefront.";
                return SemanticVocabulary.OpeningStorefront;
            }

            note = "Inferred from a sill at " + Round(sill) + " ft and " + Describe(area) + " of opening — a window.";
            return SemanticVocabulary.OpeningWindow;
        }

        /// <summary>
        /// An opening detected on a face is an Opening only when it is big enough to be one.
        /// A sub-square-foot inner loop is a modelling artefact — a sliver from a boolean, a
        /// tiny fillet remnant — not a window.
        /// </summary>
        public static bool IsSignificant(double area, UnitContext units) =>
            units != null && area >= units.MinOpeningArea;

        private static string Describe(double squareFeet) => Round(squareFeet) + " ft²";

        private static string Round(double value) => Math.Round(value, 1).ToString("0.#");
    }
}
