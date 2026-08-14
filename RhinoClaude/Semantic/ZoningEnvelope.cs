using System;
using System.Collections.Generic;
using System.Linq;

namespace RhinoClaude.Semantic
{
    /// <summary>Zoning parameters, in model units except the FAR, which is dimensionless.</summary>
    public sealed class ZoningParameters
    {
        public double MaxHeight { get; set; }
        public double SetbackNorth { get; set; }
        public double SetbackEast { get; set; }
        public double SetbackSouth { get; set; }
        public double SetbackWest { get; set; }
        public double? FarMax { get; set; }
        /// <summary>Element id of the property line to measure against; required when the doc
        /// holds more than one (plan §10.2 question 6).</summary>
        public string PropertyLineElementId { get; set; }
    }

    public sealed class ZoningViolation
    {
        /// <summary>height | setback | far.</summary>
        public string Type { get; set; }
        /// <summary>N | E | S | W, for setback violations.</summary>
        public string Side { get; set; }
        /// <summary>How far over the limit, in model units (or FAR points for a FAR violation).</summary>
        public double Amount { get; set; }
        public List<string> Ids { get; } = new List<string>();
        public string Notes { get; set; }
    }

    public sealed class ZoningReport
    {
        public BoxView AllowedEnvelope { get; set; } = BoxView.Unset;
        public double AllowedFootprintArea { get; set; }
        public double HeightLimit { get; set; }

        public BoxView CurrentBbox { get; set; } = BoxView.Unset;
        public double CurrentFootprintArea { get; set; }
        public double CurrentHeight { get; set; }
        public double GrossVolume { get; set; }
        public double? Far { get; set; }

        public List<ZoningViolation> Violations { get; } = new List<ZoningViolation>();
        /// <summary>compliant | violations | warnings.</summary>
        public string ComplianceStatus { get; set; } = "compliant";
        public string Error { get; set; }
        public List<string> Notes { get; } = new List<string>();
    }

    /// <summary>
    /// Plan §4.5's <c>get_zoning_envelope</c>: height, setbacks, optional FAR, measured against
    /// a property-line element. Deliberately minimal — plan risk #9 is feature creep here, and
    /// the answer to "can it also do parking and open space" is no.
    ///
    /// Setbacks are measured on the axis-aligned bounding box of the property line. That is the
    /// right level of precision for SD massing and honest about an irregular lot, where the
    /// report says so rather than pretending to a rigour it does not have.
    /// </summary>
    public static class ZoningEnvelope
    {
        public static ZoningReport Compute(MassingSnapshot snapshot, ZoningParameters parameters)
        {
            var report = new ZoningReport();
            if (snapshot == null || parameters == null)
            {
                report.Error = "No massing or zoning parameters were supplied.";
                return report;
            }

            var propertyLines = snapshot.View.SiteElements
                .Where(s => string.Equals(s.SiteType, "PropertyLine", StringComparison.OrdinalIgnoreCase))
                .ToList();

            SiteView propertyLine = null;
            if (!string.IsNullOrWhiteSpace(parameters.PropertyLineElementId))
            {
                propertyLine = propertyLines.FirstOrDefault(
                    s => string.Equals(s.ElementId, parameters.PropertyLineElementId, StringComparison.OrdinalIgnoreCase));
                if (propertyLine == null)
                {
                    report.Error = "No property line with element id '" + parameters.PropertyLineElementId +
                                   "'. Call describe_context to list the site elements.";
                    return report;
                }
            }
            else if (propertyLines.Count > 1)
            {
                // Plan §10.2 question 6: never silently pick one.
                report.Error = "This document has " + propertyLines.Count + " property-line curves (" +
                               string.Join(", ", propertyLines.Select(p => p.Name ?? p.ElementId)) +
                               "). Pass propertyLineElementId to say which one the setbacks are measured from.";
                return report;
            }
            else
            {
                propertyLine = propertyLines.FirstOrDefault();
            }

            var building = snapshot.View.BuildingExtents();
            report.CurrentBbox = building;
            report.CurrentFootprintArea = Math.Round(snapshot.View.TotalFootprintArea, 4);
            report.GrossVolume = Math.Round(snapshot.View.TotalVolume, 4);
            report.HeightLimit = parameters.MaxHeight;

            if (!building.IsValid)
            {
                report.Error = "There are no masses to check against the envelope.";
                return report;
            }

            double grade = snapshot.View.GradeElevation;
            report.CurrentHeight = Math.Round(building.Max.Z - grade, 4);

            // ── Height ────────────────────────────────────────────────
            if (parameters.MaxHeight > 0 && report.CurrentHeight > parameters.MaxHeight + snapshot.Units.Tolerance)
            {
                var violation = new ZoningViolation
                {
                    Type = "height",
                    Amount = Math.Round(report.CurrentHeight - parameters.MaxHeight, 4),
                    Notes = "Measured from the lowest mass base (" + Math.Round(grade, 2) + ") to the highest point."
                };
                foreach (var mass in snapshot.Masses.Where(m => m.Bbox.IsValid
                                                                && m.Bbox.Max.Z - grade > parameters.MaxHeight))
                    violation.Ids.Add(mass.ElementId);
                report.Violations.Add(violation);
            }

            // ── Setbacks ──────────────────────────────────────────────
            if (propertyLine == null || !propertyLine.Bbox.IsValid)
            {
                report.Notes.Add("No property line was found, so setbacks could not be checked. Put the " +
                                 "lot boundary on SITE_Property-Line, or tag it with ClaudeSetElement.");
                report.AllowedEnvelope = BoxView.From(
                    new Vec3(building.Min.X, building.Min.Y, grade),
                    new Vec3(building.Max.X, building.Max.Y, grade + Math.Max(parameters.MaxHeight, 0)));
            }
            else
            {
                var lot = propertyLine.Bbox;
                var allowedMin = new Vec3(lot.Min.X + parameters.SetbackWest,
                                          lot.Min.Y + parameters.SetbackSouth, grade);
                var allowedMax = new Vec3(lot.Max.X - parameters.SetbackEast,
                                          lot.Max.Y - parameters.SetbackNorth,
                                          grade + Math.Max(parameters.MaxHeight, 0));
                report.AllowedEnvelope = BoxView.From(allowedMin, allowedMax);
                report.AllowedFootprintArea = Math.Round(
                    Math.Max(0, allowedMax.X - allowedMin.X) * Math.Max(0, allowedMax.Y - allowedMin.Y), 4);

                AddSetbackViolation(report, snapshot, "N", building.Max.Y - allowedMax.Y, m => m.Bbox.Max.Y > allowedMax.Y);
                AddSetbackViolation(report, snapshot, "E", building.Max.X - allowedMax.X, m => m.Bbox.Max.X > allowedMax.X);
                AddSetbackViolation(report, snapshot, "S", allowedMin.Y - building.Min.Y, m => m.Bbox.Min.Y < allowedMin.Y);
                AddSetbackViolation(report, snapshot, "W", allowedMin.X - building.Min.X, m => m.Bbox.Min.X < allowedMin.X);

                if (!propertyLine.IsClosedCurve)
                {
                    report.Notes.Add("The property line is not a closed curve; setbacks were measured " +
                                     "against its bounding box, which is only right for a rectangular lot.");
                }

                if (propertyLine.Area is double lotArea && lotArea > 0)
                {
                    double far = snapshot.View.TotalFootprintArea > 0
                        ? EstimateGrossFloorArea(snapshot) / lotArea
                        : 0;
                    report.Far = Math.Round(far, 4);

                    if (parameters.FarMax != null && far > parameters.FarMax.Value + 1e-6)
                    {
                        report.Violations.Add(new ZoningViolation
                        {
                            Type = "far",
                            Amount = Math.Round(far - parameters.FarMax.Value, 4),
                            Notes = "Gross floor area is estimated from mass volume divided by the " +
                                    "floor-to-floor default; set one for a firmer number."
                        });
                    }
                }
                else if (parameters.FarMax != null)
                {
                    report.Notes.Add("FAR could not be computed: the property line has no enclosed area. " +
                                     "It needs to be a closed, planar curve.");
                }
            }

            report.ComplianceStatus = report.Violations.Count == 0
                ? (report.Notes.Count > 0 ? "warnings" : "compliant")
                : "violations";

            return report;
        }

        private static void AddSetbackViolation(
            ZoningReport report, MassingSnapshot snapshot, string side, double overrun, Func<MassView, bool> offender)
        {
            if (overrun <= snapshot.Units.Tolerance) return;

            var violation = new ZoningViolation
            {
                Type = "setback",
                Side = side,
                Amount = Math.Round(overrun, 4)
            };
            foreach (var mass in snapshot.Masses.Where(m => m.Bbox != null && m.Bbox.IsValid && offender(m)))
                violation.Ids.Add(mass.ElementId);
            report.Violations.Add(violation);
        }

        /// <summary>
        /// Gross floor area for the FAR, estimated as mass volume divided by floor-to-floor.
        /// With no floor-to-floor configured it falls back to footprint times storey count from
        /// the mass height, which is the same estimate stated differently.
        /// </summary>
        public static double EstimateGrossFloorArea(MassingSnapshot snapshot)
        {
            double floorToFloor = snapshot.View.FloorToFloorDefault;
            if (floorToFloor > 0) return snapshot.View.TotalVolume / floorToFloor;

            double total = 0;
            foreach (var mass in snapshot.Masses)
            {
                if (mass.Bbox == null || !mass.Bbox.IsValid) continue;
                double storeys = Math.Max(1, Math.Round(mass.Bbox.Height / snapshot.Units.Length(12)));
                total += mass.FootprintArea * storeys;
            }
            return total;
        }
    }
}
