using System;

namespace RhinoClaude.Semantic
{
    /// <summary>
    /// Every threshold in the plan is written in feet — 1 ft² for an opening, 200 ft³ for a
    /// Cut, 1000 ft³ for a Mass. Rhino documents are just as often in millimetres or metres,
    /// so the thresholds are declared in feet here and converted once, at the boundary.
    ///
    /// Rhino-free: the analyzers construct this from <c>doc.ModelUnitSystem</c> and everything
    /// downstream reads the converted numbers.
    /// </summary>
    public sealed class UnitContext
    {
        /// <summary>Model units in one foot. 1 for a foot document, 304.8 for millimetres.</summary>
        public double UnitsPerFoot { get; }

        public string UnitSystemName { get; }

        /// <summary>Model absolute tolerance, used for coincidence tests.</summary>
        public double Tolerance { get; }

        public UnitContext(double unitsPerFoot, string unitSystemName = null, double tolerance = 0.001)
        {
            UnitsPerFoot = unitsPerFoot > 1e-9 ? unitsPerFoot : 1.0;
            UnitSystemName = unitSystemName ?? "Feet";
            Tolerance = tolerance > 0 ? tolerance : 0.001;
        }

        /// <summary>A foot document with Rhino's default tolerance — the tests' baseline.</summary>
        public static UnitContext Feet(double tolerance = 0.001) => new UnitContext(1.0, "Feet", tolerance);

        public double Length(double feet) => feet * UnitsPerFoot;
        public double Area(double squareFeet) => squareFeet * UnitsPerFoot * UnitsPerFoot;
        public double Volume(double cubicFeet) => cubicFeet * UnitsPerFoot * UnitsPerFoot * UnitsPerFoot;

        public double ToFeet(double modelLength) => modelLength / UnitsPerFoot;
        public double AreaToSquareFeet(double modelArea) => modelArea / (UnitsPerFoot * UnitsPerFoot);
        public double VolumeToCubicFeet(double modelVolume) => modelVolume / (UnitsPerFoot * UnitsPerFoot * UnitsPerFoot);

        // ── The plan's thresholds, in model units ─────────────────────

        /// <summary>Plan §3.1 heuristic 4: below this a closed Brep is not a Mass candidate.</summary>
        public double MinMassVolume => Volume(1000);

        /// <summary>Plan §5.5: an inner trim loop smaller than this is noise, not an Opening.</summary>
        public double MinOpeningArea => Area(1.0);

        /// <summary>Plan §5.5 / §3.7: a subtracted volume above this reads as a Cut rather than
        /// an Opening or Recess.</summary>
        public double MinCutVolume => Volume(200);

        /// <summary>Below this an Overhang is a face artefact rather than a real projection.</summary>
        public double MinOverhangProjection => Length(1.0);

        /// <summary>Coincidence slack for party-wall and sits-on tests. Generous relative to
        /// model tolerance: architects rarely snap masses to six decimal places.</summary>
        public double AdjacencyTolerance => Math.Max(Tolerance * 10, Length(0.05));

        public override string ToString() => UnitSystemName + " (" + UnitsPerFoot + " per ft)";
    }
}
