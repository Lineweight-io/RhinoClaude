using System;
using System.Collections.Generic;
using RhinoClaude.Semantic;

namespace RhinoClaude.Tests
{
    /// <summary>
    /// Builds massing snapshots by hand, so the analytical read tools can be tested without a
    /// Rhino document. The shapes here are the ones the plan keeps using as examples: an office
    /// bar with a retail plinth, windows cut in its faces, a light well through the middle.
    /// </summary>
    internal static class SemanticFixture
    {
        public static readonly UnitContext Feet = UnitContext.Feet();

        public static MassView Mass(
            string id, Vec3 min, Vec3 max,
            string function = "Office", string layer = "MASS_Office", string name = null,
            string classifiedBy = SemanticVocabulary.ByCanonical)
        {
            var mass = new MassView
            {
                ElementId = id,
                Name = name ?? id,
                Function = function,
                Layer = layer,
                Bbox = BoxView.From(min, max),
                IsSolid = true,
                ClassifiedBy = classifiedBy
            };
            mass.RhinoObjectIds.Add(id);
            mass.Volume = CompositionAnalyzer.Volume(mass.Bbox);
            mass.FootprintArea = mass.Bbox.FootprintArea;
            mass.HeightAboveGrade = mass.Bbox.Height;
            mass.Centroid = mass.Bbox.Center;
            return mass;
        }

        public static FaceView Face(
            string massId, int index, string orientation, Vec3 normal, double area,
            double zMin, double zMax, Vec3 centroid, params string[] roles)
        {
            var face = new FaceView
            {
                FaceId = massId + ":face:" + index,
                MassId = massId,
                FaceIndex = index,
                Orientation = orientation,
                Normal = normal,
                Area = area,
                ElevationMin = zMin,
                ElevationMax = zMax,
                Centroid = centroid,
                IsPlanar = true
            };
            face.Roles.AddRange(roles.Length == 0 ? new[] { SemanticVocabulary.RoleUnclassified } : roles);
            return face;
        }

        public static OpeningView Opening(
            FaceView face, int index, string type, double width, double height, double sill)
        {
            var opening = new OpeningView
            {
                ElementId = face.FaceId + ":opening:" + index,
                MassId = face.MassId,
                FaceId = face.FaceId,
                FaceIndex = face.FaceIndex,
                OpeningType = type,
                Width = width,
                Height = height,
                SillHeight = sill,
                Area = width * height,
                Centroid = face.Centroid,
                Origin = "subtracted"
            };
            face.Openings.Add(opening);
            return opening;
        }

        public static EdgeView Edge(string massId, int index, string role, double length)
        {
            return new EdgeView
            {
                EdgeId = massId + ":edge:" + index,
                MassId = massId,
                EdgeIndex = index,
                Role = role,
                Length = length,
                IsLinear = true
            };
        }

        /// <summary>
        /// A 100 × 60 × 36 office bar: four facades, a flat roof, a base. Glazing on the south
        /// face only, which is what makes the by-orientation WWR interesting.
        /// </summary>
        public static MassGeometryView OfficeBarGeometry(string massId = "office")
        {
            var view = new MassGeometryView { MassId = massId, SourceObjectId = massId };

            var north = Face(massId, 0, "N", new Vec3(0, 1, 0), 100 * 36, 0, 36,
                new Vec3(50, 60, 18), SemanticVocabulary.RoleFacade);
            var south = Face(massId, 1, "S", new Vec3(0, -1, 0), 100 * 36, 0, 36,
                new Vec3(50, 0, 18), SemanticVocabulary.RoleFacade);
            var east = Face(massId, 2, "E", new Vec3(1, 0, 0), 60 * 36, 0, 36,
                new Vec3(100, 30, 18), SemanticVocabulary.RoleFacade);
            var west = Face(massId, 3, "W", new Vec3(-1, 0, 0), 60 * 36, 0, 36,
                new Vec3(0, 30, 18), SemanticVocabulary.RoleFacade);
            var roof = Face(massId, 4, SemanticVocabulary.OrientationUp, new Vec3(0, 0, 1), 100 * 60, 36, 36,
                new Vec3(50, 30, 36), SemanticVocabulary.RoleRoof);
            var floor = Face(massId, 5, SemanticVocabulary.OrientationDown, new Vec3(0, 0, -1), 100 * 60, 0, 0,
                new Vec3(50, 30, 0), SemanticVocabulary.RoleFloor);

            // 12 windows on the south face, 6 × 4 each: 288 ft² of 3600 → exactly 8%.
            for (int i = 0; i < 12; i++) Opening(south, i, SemanticVocabulary.OpeningWindow, 6, 4, 2.5);

            view.Faces.AddRange(new[] { north, south, east, west, roof, floor });

            view.Edges.Add(Edge(massId, 0, SemanticVocabulary.EdgeOutsideCorner, 36));
            view.Edges.Add(Edge(massId, 1, SemanticVocabulary.EdgeOutsideCorner, 36));
            view.Edges.Add(Edge(massId, 2, SemanticVocabulary.EdgeOutsideCorner, 36));
            view.Edges.Add(Edge(massId, 3, SemanticVocabulary.EdgeOutsideCorner, 36));
            view.Edges.Add(Edge(massId, 4, SemanticVocabulary.EdgeParapet, 100));
            view.Edges.Add(Edge(massId, 5, SemanticVocabulary.EdgeParapet, 100));

            roof.BoundingEdgeIndices.AddRange(new[] { 4, 5 });

            return view;
        }

        /// <summary>The whole worked example: office bar plus a retail plinth abutting it.</summary>
        public static MassingSnapshot MixedUse(double floorToFloor = 12)
        {
            var office = Mass("office", new Vec3(0, 0, 0), new Vec3(100, 60, 36), "Office", "MASS_Office", "Office bar");
            var retail = Mass("retail", new Vec3(100, 0, 0), new Vec3(160, 60, 24), "Retail", "MASS_Retail", "Retail plinth");

            var view = new SemanticView { UnitSystem = "Feet", FloorToFloorDefault = floorToFloor };
            view.Masses.Add(office);
            view.Masses.Add(retail);
            CompositionAnalyzer.FillAdjacencies(view.Masses, Feet);

            var retailGeometry = OfficeBarGeometry("retail");

            return new MassingSnapshot(view, new[] { OfficeBarGeometry("office"), retailGeometry }, Feet);
        }

        public static SiteView PropertyLine(Vec3 min, Vec3 max, double area, bool closed = true)
        {
            var site = new SiteView
            {
                ElementId = "lot",
                Name = "Property line",
                SiteType = "PropertyLine",
                Bbox = BoxView.From(min, max),
                Area = area,
                IsClosedCurve = closed
            };
            site.RhinoObjectIds.Add("lot");
            return site;
        }
    }
}
