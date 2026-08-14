using System;
using System.Collections.Generic;
using System.Linq;
using Rhino;
using RhinoClaude.Agent;
using RhinoClaude.Semantic;
using RhinoClaude.Services.Agent;

namespace RhinoClaude.Services.Semantic
{
    /// <summary>
    /// Every semantic read tool (semantic plan §4.1–§4.5). Reads from
    /// <see cref="ElementRegistry"/>, never from the document directly, and returns plain
    /// dictionaries the tool layer serializes.
    ///
    /// The analysis itself lives in the Rhino-free <c>RhinoClaude.Semantic</c> namespace — this
    /// service resolves arguments, scopes the snapshot, and shapes the response. That split is
    /// why the numbers the reviewer treats as facts have tests on them.
    ///
    /// UI-thread only, like phase 1's <see cref="RhinoQueryService"/>.
    /// </summary>
    public sealed class SemanticQueryService
    {
        private readonly ElementRegistry _registry;
        private readonly uint _docSerialNumber;

        public SemanticQueryService(RhinoDoc doc, ElementRegistry registry)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));
            _docSerialNumber = doc.RuntimeSerialNumber;
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        }

        public ElementRegistry Registry => _registry;

        private RhinoDoc Doc => RhinoDoc.FromRuntimeSerialNumber(_docSerialNumber);

        private UnitContext Units => _registry.Units;

        /// <summary>A snapshot over every mass — the expensive path, used by whole-building queries.</summary>
        private MassingSnapshot WholeBuilding() =>
            new MassingSnapshot(_registry.View, _registry.AllGeometry(), Units);

        private MassingSnapshot For(string massId)
        {
            if (string.IsNullOrWhiteSpace(massId)) return WholeBuilding();
            var mass = RequireMass(massId);
            return new MassingSnapshot(_registry.View, new[] { _registry.GeometryFor(mass) }, Units);
        }

        private MassView RequireMass(string massId)
        {
            var mass = _registry.FindMass(massId);
            if (mass == null)
            {
                var known = _registry.View.Masses.Take(8)
                    .Select(m => m.ElementId + " (" + m.Name + ")");
                throw new ArgumentException(
                    "No Mass with id or name '" + massId + "'. " +
                    (_registry.View.Masses.Count == 0
                        ? "This document has no classified masses — call describe_massing for why."
                        : "Known masses: " + string.Join(", ", known) + "."));
            }
            return mass;
        }

        // ══ §4.1 Descriptive ══════════════════════════════════════════

        public object DescribeMassing(string levelOfDetail)
        {
            var snapshot = WholeBuilding();
            var view = snapshot.View;
            var units = snapshot.Units;

            var result = new Dictionary<string, object>
            {
                { "narrative", MassingNarrator.Narrate(snapshot, levelOfDetail) },
                { "unitSystem", view.UnitSystem },
                { "masses", view.Masses.Select(m => MassSummary(m, snapshot)).ToList() },
                { "massGroups", view.Groups.Select(GroupSummary).ToList() },
                { "compositionRelationships", CompositionAnalyzer.Relationships(view.Masses, units)
                                                                 .Select(r => r.ToJson()).ToList() },
                { "totals", new Dictionary<string, object>
                    {
                        { "grossVolume", Round(view.TotalVolume) },
                        { "footprintArea", Round(view.TotalFootprintArea) },
                        { "massCount", view.Masses.Count },
                        { "buildingExtents", view.BuildingExtents().ToJson() },
                        { "gradeElevation", Round(view.GradeElevation) }
                    } },
                { "siteContext", new Dictionary<string, object>
                    {
                        { "elementCount", view.SiteElements.Count },
                        { "byType", view.SiteElements.GroupBy(s => s.SiteType)
                                                     .ToDictionary(g => g.Key, g => g.Count()) }
                    } },
                { "unclassifiedCount", view.UnclassifiedCount },
                { "unclassifiedLayers", view.UnclassifiedLayers.Select(ToolJson.Safe).Take(20).ToList() },
                { "floorToFloorDefault", Round(view.FloorToFloorDefault) },
                { "notes", view.Notes }
            };

            return result;
        }

        public object DescribeContext(
            double distance, bool includeTopography, bool includeContextBuildings, bool includeStreets)
        {
            var view = _registry.View;
            var units = Units;
            var building = view.BuildingExtents();

            bool Near(SiteView site)
            {
                if (!building.IsValid || site.Bbox == null || !site.Bbox.IsValid) return true;
                return site.Bbox.Intersects(building, distance);
            }

            List<object> Section(string siteType, bool include)
            {
                if (!include) return new List<object>();
                return view.SiteElements
                    .Where(s => string.Equals(s.SiteType, siteType, StringComparison.OrdinalIgnoreCase) && Near(s))
                    .Select(SiteSummary)
                    .ToList();
            }

            var propertyLines = view.SiteElements
                .Where(s => string.Equals(s.SiteType, "PropertyLine", StringComparison.OrdinalIgnoreCase))
                .ToList();

            object propertyLine = null;
            if (propertyLines.Count > 0)
            {
                var line = propertyLines[0];
                var setbacks = new Dictionary<string, object>();
                if (line.Bbox != null && line.Bbox.IsValid && building.IsValid)
                {
                    setbacks["N"] = Round(line.Bbox.Max.Y - building.Max.Y);
                    setbacks["E"] = Round(line.Bbox.Max.X - building.Max.X);
                    setbacks["S"] = Round(building.Min.Y - line.Bbox.Min.Y);
                    setbacks["W"] = Round(building.Min.X - line.Bbox.Min.X);
                }

                propertyLine = new Dictionary<string, object>
                {
                    { "elementId", line.ElementId },
                    { "name", ToolJson.Safe(line.Name) },
                    { "area", line.Area == null ? (object)null : Round(line.Area.Value) },
                    { "isClosed", line.IsClosedCurve },
                    { "setbacksFromBuilding", setbacks },
                    { "count", propertyLines.Count }
                };
            }

            return new Dictionary<string, object>
            {
                { "distance", Round(distance) },
                { "contextBuildings", Section("ContextBuilding", includeContextBuildings) },
                { "streets", Section("Street", includeStreets) },
                { "curbs", Section("Curb", includeStreets) },
                { "topography", Section("Topography", includeTopography) },
                { "utilities", Section("Utility", true) },
                { "propertyLine", propertyLine },
                { "notes", view.SiteElements.Count == 0
                    ? "No site elements are classified. Put context on SITE_* layers — see LAYER_CONVENTIONS.md."
                    : null }
            };
        }

        /// <summary>
        /// Plan §4.1's <c>find_element</c>: rules-based natural-language lookup, with the LLM
        /// fallback deliberately absent on zero matches — the tool reports what it understood
        /// and what exists, which is cheaper and more useful than a second model call.
        /// </summary>
        public object FindElement(string queryText, string expect)
        {
            var query = ElementQueryParser.Parse(queryText);
            var snapshot = WholeBuilding();
            var matches = new List<Dictionary<string, object>>();

            void Add(string elementId, string type, string name, double confidence, string note = null)
            {
                matches.Add(new Dictionary<string, object>
                {
                    { "elementId", elementId },
                    { "type", type },
                    { "name", ToolJson.Safe(name) },
                    { "confidence", Math.Round(confidence, 3) },
                    { "notes", note }
                });
            }

            var candidateMasses = snapshot.Masses.AsEnumerable();
            if (query.Function != null)
                candidateMasses = candidateMasses.Where(m =>
                    string.Equals(m.Function, query.Function, StringComparison.OrdinalIgnoreCase));

            if (query.NameHints.Count > 0)
            {
                var byName = candidateMasses
                    .Select(m => new { Mass = m, Score = ElementQueryParser.NameScore(m.Name, query.NameHints) })
                    .Where(x => x.Score > 0)
                    .ToList();
                if (byName.Count > 0) candidateMasses = byName.Select(x => x.Mass);
            }

            var massList = candidateMasses.OrderByDescending(m => m.Volume).ToList();

            if (query.Superlative == "smallest") massList = massList.OrderBy(m => m.Volume).ToList();
            else if (query.Superlative == "tallest")
                massList = massList.OrderByDescending(m => m.Bbox.IsValid ? m.Bbox.Height : 0).ToList();

            if (query.Superlative != null && massList.Count > 1) massList = massList.Take(1).ToList();

            switch (query.TargetType)
            {
                case SemanticVocabulary.Face:
                    foreach (var mass in massList)
                    {
                        var geometry = snapshot.GeometryFor(mass.ElementId);
                        if (geometry == null) continue;

                        var selector = new FaceSelector { Orientation = query.Orientation, Role = query.FaceRole };
                        if (selector.IsEmpty) continue;

                        foreach (var face in FaceSelectorResolver.Filter(geometry.Faces, selector)
                                                                 .OrderByDescending(f => f.Area))
                        {
                            Add(face.FaceId, SemanticVocabulary.Face,
                                (mass.Name ?? mass.Function) + " / " + face.Orientation + " face",
                                Confidence(query, mass), string.Join("; ", face.Notes));
                        }
                    }
                    break;

                case SemanticVocabulary.Edge:
                    foreach (var mass in massList)
                    {
                        var geometry = snapshot.GeometryFor(mass.ElementId);
                        if (geometry == null) continue;

                        foreach (var edge in geometry.Edges.Where(e =>
                                     query.EdgeRole == null || e.Role == query.EdgeRole)
                                 .OrderByDescending(e => e.Length).Take(24))
                        {
                            Add(edge.EdgeId, SemanticVocabulary.Edge,
                                (mass.Name ?? mass.Function) + " / " + edge.Role, Confidence(query, mass));
                        }
                    }
                    break;

                case SemanticVocabulary.Opening:
                    foreach (var opening in snapshot.AllOpenings
                                 .Where(o => query.OpeningType == null || o.OpeningType == query.OpeningType)
                                 .Where(o => !query.WantsEntry || o.IsEntry)
                                 .OrderByDescending(o => o.Area).Take(40))
                    {
                        Add(opening.ElementId, SemanticVocabulary.Opening,
                            opening.OpeningType + " on " + opening.FaceId, 0.7);
                    }
                    break;

                case SemanticVocabulary.MassGroup:
                    foreach (var group in snapshot.View.Groups)
                        Add(group.ElementId, SemanticVocabulary.MassGroup, group.Name,
                            ElementQueryParser.NameScore(group.Name, query.NameHints) + 0.5);
                    break;

                case SemanticVocabulary.Site:
                    foreach (var site in snapshot.View.SiteElements
                                 .Where(s => query.SiteType == null || s.SiteType == query.SiteType))
                        Add(site.ElementId, SemanticVocabulary.Site, site.Name, 0.8);
                    break;

                case SemanticVocabulary.Level:
                    foreach (var level in snapshot.View.Levels)
                        Add(level.ElementId, SemanticVocabulary.Level, level.Name, level.Inferred ? 0.5 : 0.9);
                    break;

                case SemanticVocabulary.Cut:
                    foreach (var cut in snapshot.AllGeometry.SelectMany(g => g.Cuts))
                        Add(cut.ElementId, SemanticVocabulary.Cut, cut.Name, 0.6);
                    break;

                case SemanticVocabulary.Recess:
                    foreach (var recess in snapshot.AllGeometry.SelectMany(g => g.Recesses))
                        Add(recess.ElementId, SemanticVocabulary.Recess, recess.Name, 0.5);
                    break;

                case SemanticVocabulary.Overhang:
                    foreach (var overhang in snapshot.AllGeometry.SelectMany(g => g.Overhangs))
                        Add(overhang.ElementId, SemanticVocabulary.Overhang, overhang.Name, 0.6);
                    break;

                default:
                    foreach (var mass in massList)
                        Add(mass.ElementId, SemanticVocabulary.Mass, mass.Name, Confidence(query, mass),
                            mass.ClassifiedBy == SemanticVocabulary.ByGeometryInference
                                ? "Classified from geometry alone — confirm before a destructive move."
                                : null);
                    break;
            }

            bool truncated = matches.Count > 25;
            var trimmed = matches.OrderByDescending(m => (double)m["confidence"]).Take(25).ToList();

            string interpretation = query.IsEmpty
                ? "No element vocabulary was recognised in that query."
                : "Read as: " + query;

            return new Dictionary<string, object>
            {
                { "matches", trimmed },
                { "truncated", truncated },
                { "interpretation", interpretation },
                { "notes", trimmed.Count == 0
                    ? interpretation + " Nothing matched. Call describe_massing or list_masses to see what " +
                      "the classifier can see, then query by element id."
                    : (string.Equals(expect, "one", StringComparison.OrdinalIgnoreCase) && trimmed.Count > 1
                        ? "Several elements matched but you asked for one; the highest-confidence match is first."
                        : null) }
            };
        }

        private static double Confidence(ElementQuery query, MassView mass)
        {
            double score = 0.5;
            if (query.Function != null
                && string.Equals(mass.Function, query.Function, StringComparison.OrdinalIgnoreCase))
                score += 0.3;
            if (query.NameHints.Count > 0) score += ElementQueryParser.NameScore(mass.Name, query.NameHints) * 0.2;
            if (mass.ClassifiedBy == SemanticVocabulary.ByUserData) score += 0.1;
            else if (mass.ClassifiedBy == SemanticVocabulary.ByGeometryInference) score -= 0.2;
            return Math.Max(0.05, Math.Min(1.0, score));
        }

        // ══ §4.2 Mass catalog ═════════════════════════════════════════

        public object ListMasses(string functionFilter, string massGroupId)
        {
            var view = _registry.View;
            var snapshot = new MassingSnapshot(view, null, Units);

            var masses = view.Masses.AsEnumerable();

            if (!string.IsNullOrWhiteSpace(functionFilter))
            {
                var function = SemanticVocabulary.Normalize(functionFilter, SemanticVocabulary.MassFunctions);
                if (function == null)
                    throw new ArgumentException("Unknown function '" + functionFilter + "'. Valid values: " +
                                                SemanticVocabulary.Join(SemanticVocabulary.MassFunctions) + ".");
                masses = masses.Where(m => string.Equals(m.Function, function, StringComparison.Ordinal));
            }

            if (!string.IsNullOrWhiteSpace(massGroupId))
            {
                var group = view.FindGroup(massGroupId);
                if (group == null)
                    throw new ArgumentException("No MassGroup '" + massGroupId +
                                                "'. Call list_mass_groups for the current groups.");
                masses = masses.Where(m => group.MassIds.Contains(m.ElementId));
            }

            return new Dictionary<string, object>
            {
                { "masses", masses.OrderByDescending(m => m.Volume)
                                  .Select(m => MassSummary(m, snapshot)).ToList() },
                { "totalVolume", Round(view.TotalVolume) },
                { "unclassifiedCount", view.UnclassifiedCount }
            };
        }

        public object ListMassGroups()
        {
            var view = _registry.View;
            return new Dictionary<string, object>
            {
                { "groups", view.Groups.Select(GroupSummary).ToList() },
                { "ungroupedMassIds", view.Masses.Where(m => m.MassGroupId == null)
                                                 .Select(m => m.ElementId).ToList() },
                { "notes", view.Groups.Count == 0
                    ? "No MassGroups. Groups come from a RhinoClaude:MassGroup tag, Rhino Group " +
                      "membership, or masses sharing a parent layer — a group of one is not reported."
                    : null }
            };
        }

        public object AnalyzeBooleanHistory(string massId)
        {
            var mass = RequireMass(massId);
            var geometry = _registry.GeometryFor(mass);

            return new Dictionary<string, object>
            {
                { "massId", mass.ElementId },
                { "historyAvailable", geometry?.HistoryAvailable ?? false },
                { "operations", (geometry?.History ?? new List<BooleanOperationRecord>())
                                .Select(o => o.ToJson()).ToList() },
                { "derivedFromTopology", new Dictionary<string, object>
                    {
                        { "cuts", (geometry?.Cuts ?? new List<CutView>()).Select(CutSummary).ToList() },
                        { "openingCount", geometry?.AllOpenings.Count() ?? 0 },
                        { "recessCount", geometry?.Recesses.Count ?? 0 }
                    } },
                { "notes", geometry != null && geometry.HistoryAvailable
                    ? "Rhino recorded history for this mass; the operations above are what it kept."
                    : "Rhino history is not available on this mass — most architects work with history " +
                      "off. The derivedFromTopology figures come from the Brep itself and are always " +
                      "present; a through-cut light well shows up there as openings rather than a Cut, " +
                      "because a through cut is welded to the outer skin." }
            };
        }

        // ══ §4.3 Face and edge analysis ═══════════════════════════════

        public object GetMassFaces(string massId, string filterByRole, string filterByOrientation)
        {
            var mass = RequireMass(massId);
            var geometry = RequireGeometry(mass);

            var selector = new FaceSelector
            {
                Role = SemanticVocabulary.Normalize(filterByRole, SemanticVocabulary.FaceRoles),
                Orientation = SemanticVocabulary.Normalize(filterByOrientation, SemanticVocabulary.Orientations)
            };

            var faces = selector.IsEmpty
                ? geometry.Faces
                : FaceSelectorResolver.Filter(geometry.Faces, selector).ToList();

            // Largest first, then capped: on a dense mass the tail is slivers, and everything
            // returned here is re-sent on every later iteration of the turn.
            var ranked = faces.OrderByDescending(f => f.Area).ToList();
            var shown = ranked.Take(PayloadCaps.FacesPerCall).ToList();

            return new Dictionary<string, object>
            {
                { "massId", mass.ElementId },
                { "massName", ToolJson.Safe(mass.Name) },
                { "faces", shown.Select(FaceSummary).ToList() },
                { "faceCount", geometry.Faces.Count },
                { "matchedFaceCount", ranked.Count },
                { "truncated", ranked.Count > shown.Count },
                { "unclassifiedFaceArea", Round(geometry.UnclassifiedFaceArea) },
                { "notes", faces.Count == 0 && !selector.IsEmpty
                    ? "No face matched that filter. This mass has orientations [" +
                      string.Join(", ", geometry.Faces.Select(f => f.Orientation).Distinct()) + "]."
                    : string.Join(" ", geometry.Notes) +
                      (ranked.Count > shown.Count
                        ? " Showing the " + shown.Count + " largest of " + ranked.Count +
                          " matching faces; filter by role or orientation to see the rest."
                        : string.Empty) }
            };
        }

        public object GetFace(FaceSelector selector, string massId)
        {
            var mass = ResolveMassForFace(selector, massId);
            var geometry = RequireGeometry(mass);

            var resolution = FaceSelectorResolver.Resolve(geometry, selector);
            if (!resolution.Resolved) throw new ArgumentException(resolution.Error);

            var face = resolution.Face;
            var result = FaceSummary(face);

            result["centroid"] = face.Centroid.ToArray();
            result["normal"] = face.Normal.ToArray();
            result["surfaceType"] = face.SurfaceType;
            result["classifiedBy"] = face.ClassifiedBy;
            result["openings"] = face.Openings.Select(OpeningSummary).ToList();
            result["overhangs"] = geometry.Overhangs
                .Where(o => o.AttachedToFaceId == face.FaceId).Select(OverhangSummary).ToList();
            result["recesses"] = geometry.Recesses
                .Where(r => r.OpeningFaceId == face.FaceId).Select(RecessSummary).ToList();
            result["boundingEdges"] = face.BoundingEdgeIndices
                .Select(i => geometry.Edges.FirstOrDefault(e => e.EdgeIndex == i))
                .Where(e => e != null)
                .Select(EdgeSummary).ToList();
            result["massName"] = ToolJson.Safe(mass.Name);
            result["selectorNote"] = resolution.Note;

            return result;
        }

        public object GetMassEdges(string massId, string filterByRole)
        {
            var mass = RequireMass(massId);
            var geometry = RequireGeometry(mass);

            var role = SemanticVocabulary.Normalize(filterByRole, SemanticVocabulary.EdgeRoles);
            var edges = role == null
                ? geometry.Edges.Where(e => e.Role != SemanticVocabulary.EdgeOther)
                : geometry.Edges.Where(e => e.Role == role);

            // Longest first, then capped — same reasoning as GetMassFaces.
            var ranked = edges.OrderByDescending(e => e.Length).ToList();
            var shown = ranked.Take(PayloadCaps.EdgesPerCall).ToList();

            var notes = new List<string>();
            if (role == null)
            {
                notes.Add("Edges with role 'other' are omitted; there are " +
                          geometry.Edges.Count(e => e.Role == SemanticVocabulary.EdgeOther) + " of them.");
            }
            if (ranked.Count > shown.Count)
            {
                notes.Add("Showing the " + shown.Count + " longest of " + ranked.Count +
                          " matching edges; filter by role to see the rest.");
            }

            return new Dictionary<string, object>
            {
                { "massId", mass.ElementId },
                { "edges", shown.Select(EdgeSummary).ToList() },
                { "totalEdgeCount", geometry.Edges.Count },
                { "matchedEdgeCount", ranked.Count },
                { "truncated", ranked.Count > shown.Count },
                { "notes", notes.Count == 0 ? null : string.Join(" ", notes) }
            };
        }

        public object CheckFaceRelationships(List<string> massIds, double? tolerance)
        {
            var snapshot = massIds == null || massIds.Count == 0
                ? WholeBuilding()
                : new MassingSnapshot(_registry.View,
                    massIds.Select(id => _registry.GeometryFor(RequireMass(id))).Where(g => g != null),
                    Units);

            double resolved = tolerance ?? Units.AdjacencyTolerance;
            var report = FaceRelationships.Compute(snapshot.AllFaces.ToList(), resolved);

            return new Dictionary<string, object>
            {
                { "tolerance", Round(resolved) },
                { "facesConsidered", report.FacesConsidered },
                { "coplanarGroups", report.CoplanarGroups },
                { "parallelPairs", report.ParallelPairs.Select(p => (object)new Dictionary<string, object>
                    {
                        { "a", p.A }, { "b", p.B },
                        { "offset", Round(p.Offset) },
                        { "facingEachOther", p.FacingEachOther }
                    }).Take(200).ToList() },
                { "perpendicularPairs", report.PerpendicularPairs.Select(p => (object)new Dictionary<string, object>
                    {
                        { "a", p.A }, { "b", p.B }
                    }).Take(200).ToList() },
                { "flushAlignments", report.FlushAlignments.Select(f => (object)new Dictionary<string, object>
                    {
                        { "faces", f.Faces }, { "notes", f.Notes }
                    }).ToList() },
                { "notes", string.Join(" ", report.Notes) }
            };
        }

        public object FindOpeningsInFace(FaceSelector selector, string massId)
        {
            var mass = ResolveMassForFace(selector, massId);
            var geometry = RequireGeometry(mass);

            var resolution = FaceSelectorResolver.Resolve(geometry, selector);
            if (!resolution.Resolved) throw new ArgumentException(resolution.Error);

            var face = resolution.Face;

            return new Dictionary<string, object>
            {
                { "faceId", face.FaceId },
                { "massId", mass.ElementId },
                { "faceArea", Round(face.Area) },
                { "openings", face.Openings.OrderByDescending(o => o.Area).Select(OpeningSummary).ToList() },
                { "totalOpeningArea", Round(face.OpeningArea) },
                { "wallWindowRatio", face.WallWindowRatio == null ? (object)null : Round(face.WallWindowRatio.Value) },
                { "notes", face.WallWindowRatio == null
                    ? "This face is not a facade, so a wall-window ratio is not defined for it."
                    : resolution.Note }
            };
        }

        // ══ §4.4 Envelope, program, composition ═══════════════════════

        public object CheckWallWindowRatio(string scope, string massId, bool includeOverhangsAsShading)
        {
            var snapshot = For(massId);
            var report = WallWindowRatio.Compute(snapshot, scope, includeOverhangsAsShading);

            return new Dictionary<string, object>
            {
                { "scope", scope },
                { "massId", massId },
                { "results", report.Results.Select(r => (object)new Dictionary<string, object>
                    {
                        { "key", r.Key },
                        { "area", Round(r.Area) },
                        { "openingArea", Round(r.OpeningArea) },
                        { "ratio", Round(r.Ratio) },
                        { "faceCount", r.FaceCount },
                        { "glazingByType", r.GlazingByType.ToDictionary(kv => kv.Key, kv => Round(kv.Value)) }
                    }).ToList() },
                { "overallRatio", Round(report.OverallRatio) },
                { "totalFacadeArea", Round(report.TotalFacadeArea) },
                { "totalOpeningArea", Round(report.TotalOpeningArea) },
                { "skippedUnclassifiedArea", Round(report.SkippedUnclassifiedArea) },
                { "notes", string.Join(" ", report.Notes) }
            };
        }

        public object GetRoofAnalysis(string massId)
        {
            var snapshot = For(massId);
            var report = RoofAnalysis.Compute(snapshot);

            return new Dictionary<string, object>
            {
                { "roofFaces", report.RoofFaces.Select(f => (object)new Dictionary<string, object>
                    {
                        { "id", f.FaceId },
                        { "massId", f.MassId },
                        { "area", Round(f.Area) },
                        { "slopePercent", Round(f.SlopePercent) },
                        { "drainageDirection", f.DrainageDirection },
                        { "isPlanar", f.IsPlanar },
                        { "elevationRange", f.ElevationRange?.Select(Round).ToArray() },
                        { "adjacentEdges", f.AdjacentEdges.Select(e => (object)new Dictionary<string, object>
                            {
                                { "edgeId", e.EdgeId }, { "role", e.Role }, { "length", Round(e.Length) }
                            }).ToList() }
                    }).ToList() },
                { "totalRoofArea", Round(report.TotalRoofArea) },
                { "predominantForm", report.PredominantForm },
                { "ridgeLengths", Round(report.RidgeLength) },
                { "eaveLengths", Round(report.EaveLength) },
                { "parapetLengths", Round(report.ParapetLength) },
                { "notes", string.Join(" ", report.Notes) }
            };
        }

        public object GetProgramAllocation()
        {
            var view = _registry.View;
            var byFunction = ProgramAllocation.Compute(view, out double totalVolume);

            return new Dictionary<string, object>
            {
                { "byFunction", byFunction.ToDictionary(kv => kv.Key, kv => (object)new Dictionary<string, object>
                    {
                        { "totalVolume", Round(kv.Value.TotalVolume) },
                        { "footprintArea", Round(kv.Value.FootprintArea) },
                        { "percentOfTotal", Round(kv.Value.PercentOfTotal) },
                        { "massCount", kv.Value.MassCount }
                    }) },
                { "totalVolume", Round(totalVolume) },
                { "unitSystem", view.UnitSystem },
                { "notes", byFunction.ContainsKey(SemanticVocabulary.FunctionOther)
                    ? "Masses with function 'Other' have no function tag or MASS_* layer. Tag them with " +
                      "ClaudeSetElement, or put them on MASS_Office / MASS_Residential / … layers."
                    : null }
            };
        }

        public object CheckMassingComposition()
        {
            var snapshot = WholeBuilding();
            var report = MassingComposition.Compute(snapshot);

            return new Dictionary<string, object>
            {
                { "proportions", new Dictionary<string, object>
                    {
                        { "overallBbox", report.OverallBbox.ToJson() },
                        { "aspectRatios", report.AspectRatios },
                        { "dominantAxis", report.DominantAxis }
                    } },
                { "symmetry", new Dictionary<string, object>
                    {
                        { "aboutX", Round(report.SymmetryAboutX) },
                        { "aboutY", Round(report.SymmetryAboutY) }
                    } },
                { "massHierarchy", new Dictionary<string, object>
                    {
                        { "ranked", report.Ranked.Select(r => (object)new Dictionary<string, object>
                            {
                                { "id", r.MassId },
                                { "name", ToolJson.Safe(r.Name) },
                                { "function", r.Function },
                                { "volume", Round(r.Volume) },
                                { "percentOfTotal", Round(r.PercentOfTotal) }
                            }).ToList() },
                        { "primaryMassId", report.PrimaryMassId },
                        { "ratioPrimaryToSecondary", report.RatioPrimaryToSecondary == null
                            ? (object)null : Round(report.RatioPrimaryToSecondary.Value) }
                    } },
                { "booleanComposition", new Dictionary<string, object>
                    {
                        { "unionCount", report.UnionCount },
                        { "differenceCount", report.DifferenceCount },
                        { "cutVolumeTotal", Round(report.CutVolumeTotal) },
                        { "additiveVolumeTotal", Round(report.AdditiveVolumeTotal) }
                    } },
                { "verticalRhythm", new Dictionary<string, object>
                    {
                        { "inferredLevelCount", report.InferredLevelCount },
                        { "floorToFloorConsistency", report.FloorToFloorConsistency == null
                            ? (object)null : Round(report.FloorToFloorConsistency.Value) }
                    } },
                { "notes", string.Join(" ", report.Notes) }
            };
        }

        public object GetLevelInfo(string levelName, string massId)
        {
            var view = _registry.View;
            var snapshot = For(massId);

            var levels = view.Levels.AsEnumerable();
            if (!string.IsNullOrWhiteSpace(levelName))
            {
                levels = levels.Where(l =>
                    (l.Name ?? string.Empty).IndexOf(levelName, StringComparison.OrdinalIgnoreCase) >= 0);
            }

            var results = new List<object>();
            foreach (var level in levels.OrderBy(l => l.Elevation))
            {
                var plates = new List<object>();
                double totalFloorArea = 0;

                foreach (var mass in snapshot.Masses)
                {
                    if (mass.Bbox == null || !mass.Bbox.IsValid) continue;
                    if (level.Elevation < mass.Bbox.Min.Z - Units.AdjacencyTolerance) continue;
                    if (level.Elevation > mass.Bbox.Max.Z + Units.AdjacencyTolerance) continue;

                    // The plate area is the mass's footprint at this elevation. Approximated by
                    // the footprint of the whole mass — right for a prismatic mass, and flagged
                    // for a stepped one.
                    plates.Add(new Dictionary<string, object>
                    {
                        { "massId", mass.ElementId },
                        { "massName", ToolJson.Safe(mass.Name) },
                        { "netArea", Round(mass.FootprintArea) }
                    });
                    totalFloorArea += mass.FootprintArea;
                }

                results.Add(new Dictionary<string, object>
                {
                    { "id", level.ElementId },
                    { "name", ToolJson.Safe(level.Name) },
                    { "elevation", Round(level.Elevation) },
                    { "floorToFloor", level.FloorToFloor == null ? (object)null : Round(level.FloorToFloor.Value) },
                    { "isRoofLevel", level.IsRoofLevel },
                    { "inferred", level.Inferred },
                    { "floorPlates", plates },
                    { "totalFloorArea", Round(totalFloorArea) }
                });
            }

            return new Dictionary<string, object>
            {
                { "levels", results },
                { "floorToFloorDefault", Round(view.FloorToFloorDefault) },
                { "notes", view.Levels.Count == 0
                    ? "No Levels are drawn and no floor-to-floor default is configured, so there is no " +
                      "level ladder to report. Set one with ClaudeLearnNamingConvention, or draw levels " +
                      "on LEVEL_* layers."
                    : "Floor-plate areas are each mass's overall footprint, which is exact for a prismatic " +
                      "mass and approximate for a stepped one. Use slice_mass_at_elevation for a real plate." }
            };
        }

        // ══ §4.5 Constraints ══════════════════════════════════════════

        public object GetZoningEnvelope(ZoningParameters parameters)
        {
            var snapshot = WholeBuilding();
            var report = ZoningEnvelope.Compute(snapshot, parameters);

            if (report.Error != null) throw new ArgumentException(report.Error);

            return new Dictionary<string, object>
            {
                { "allowedEnvelope", new Dictionary<string, object>
                    {
                        { "bbox", report.AllowedEnvelope.ToJson() },
                        { "footprintArea", Round(report.AllowedFootprintArea) },
                        { "heightLimit", Round(report.HeightLimit) }
                    } },
                { "currentBuilding", new Dictionary<string, object>
                    {
                        { "bbox", report.CurrentBbox.ToJson() },
                        { "footprintArea", Round(report.CurrentFootprintArea) },
                        { "height", Round(report.CurrentHeight) },
                        { "grossVolume", Round(report.GrossVolume) },
                        { "far", report.Far == null ? (object)null : Round(report.Far.Value) }
                    } },
                { "violations", report.Violations.Select(v => (object)new Dictionary<string, object>
                    {
                        { "type", v.Type },
                        { "side", v.Side },
                        { "amount", Round(v.Amount) },
                        { "ids", v.Ids },
                        { "notes", v.Notes }
                    }).ToList() },
                { "complianceStatus", report.ComplianceStatus },
                { "notes", string.Join(" ", report.Notes) }
            };
        }

        // ══ Shared serialization ══════════════════════════════════════

        private MassView ResolveMassForFace(FaceSelector selector, string massId)
        {
            if (!string.IsNullOrWhiteSpace(massId)) return RequireMass(massId);

            // A face id carries its mass, so massId is optional when one is given.
            string derived = MassGeometryAnalyzer.MassIdOf(selector?.FaceId);
            if (!string.IsNullOrWhiteSpace(derived)) return RequireMass(derived);

            var masses = _registry.View.Masses;
            if (masses.Count == 1) return masses[0];

            throw new ArgumentException(
                "massId is required when the document has more than one mass and the selector is not a " +
                "faceId. Call list_masses first.");
        }

        private MassGeometryView RequireGeometry(MassView mass)
        {
            var geometry = _registry.GeometryFor(mass);
            if (geometry == null)
                throw new InvalidOperationException("Could not analyse the geometry of mass " + mass.ElementId + ".");
            return geometry;
        }

        private Dictionary<string, object> MassSummary(MassView mass, MassingSnapshot snapshot)
        {
            var geometry = snapshot.GeometryFor(mass.ElementId);

            var summary = new Dictionary<string, object>
            {
                { "id", mass.ElementId },
                { "rhinoObjectIds", mass.RhinoObjectIds },
                { "name", ToolJson.Safe(mass.Name) },
                { "function", mass.Function },
                { "layer", ToolJson.Safe(mass.Layer) },
                { "volume", Round(mass.Volume) },
                { "footprintArea", Round(mass.FootprintArea) },
                { "height", Round(mass.HeightAboveGrade) },
                { "bbox", mass.Bbox.ToJson() },
                { "centroid", mass.Centroid.ToArray() },
                { "isSolid", mass.IsSolid },
                { "faceCount", mass.FaceCount },
                { "edgeCount", mass.EdgeCount },
                { "classifiedBy", mass.ClassifiedBy },
                { "massGroupId", mass.MassGroupId },
                { "adjacentMasses", mass.AdjacentMasses.Select(a => (object)new Dictionary<string, object>
                    {
                        { "massId", a.MassId }, { "relationship", a.Relationship }
                    }).ToList() },
                { "tags", mass.Tags },
                { "notes", mass.Notes.Count == 0 ? null : string.Join(" ", mass.Notes) }
            };

            if (geometry != null)
            {
                summary["faceCountByRole"] = SemanticVocabulary.FaceRoles
                    .Select(role => new { role, count = geometry.Faces.Count(f => f.HasRole(role)) })
                    .Where(x => x.count > 0)
                    .ToDictionary(x => x.role, x => x.count);
                summary["openingCount"] = geometry.AllOpenings.Count();
                summary["cutCount"] = geometry.Cuts.Count;
            }

            return summary;
        }

        private object GroupSummary(MassGroupView group) => new Dictionary<string, object>
        {
            { "id", group.ElementId },
            { "name", ToolJson.Safe(group.Name) },
            { "masses", group.MassIds },
            { "combinedVolume", Round(group.CombinedVolume) },
            { "combinedFootprintArea", Round(group.CombinedFootprintArea) },
            { "bbox", group.Bbox.ToJson() },
            { "dominantFunction", group.DominantFunction },
            { "classifiedBy", group.ClassifiedBy }
        };

        private Dictionary<string, object> FaceSummary(FaceView face) => new Dictionary<string, object>
        {
            { "id", face.FaceId },
            { "massId", face.MassId },
            { "faceIndex", face.FaceIndex },
            { "orientation", face.Orientation },
            { "roles", face.Roles },
            { "area", Round(face.Area) },
            { "elevationRange", new[] { Round(face.ElevationMin), Round(face.ElevationMax) } },
            { "isPlanar", face.IsPlanar },
            { "openingArea", Round(face.OpeningArea) },
            { "openingCount", face.Openings.Count },
            { "wallWindowRatio", face.WallWindowRatio == null ? (object)null : Round(face.WallWindowRatio.Value) },
            { "notes", face.Notes.Count == 0 ? null : string.Join(" ", face.Notes) }
        };

        private object EdgeSummary(EdgeView edge) => new Dictionary<string, object>
        {
            { "id", edge.EdgeId },
            { "edgeIndex", edge.EdgeIndex },
            { "role", edge.Role },
            { "length", Round(edge.Length) },
            { "startPoint", edge.StartPoint.ToArray() },
            { "endPoint", edge.EndPoint.ToArray() },
            { "isLinear", edge.IsLinear },
            { "adjacentFaces", edge.AdjacentFaceIndices }
        };

        private object OpeningSummary(OpeningView opening) => new Dictionary<string, object>
        {
            { "id", opening.ElementId },
            { "type", opening.OpeningType },
            { "width", Round(opening.Width) },
            { "height", Round(opening.Height) },
            { "sillHeight", Round(opening.SillHeight) },
            { "area", Round(opening.Area) },
            { "centroidOnFace", opening.CentroidOnFace },
            { "depth", opening.Depth == null ? (object)null : Round(opening.Depth.Value) },
            { "origin", opening.Origin },
            { "isEntry", opening.IsEntry },
            { "entryType", opening.EntryType },
            { "classifiedBy", opening.ClassifiedBy },
            { "notes", opening.Notes.Count == 0 ? null : string.Join(" ", opening.Notes) }
        };

        private object OverhangSummary(OverhangView overhang) => new Dictionary<string, object>
        {
            { "id", overhang.ElementId },
            { "subtype", overhang.Subtype },
            { "attachedToMassId", overhang.AttachedToMassId },
            { "attachedToFaceId", overhang.AttachedToFaceId },
            { "projectionDistance", Round(overhang.ProjectionDistance) },
            { "width", Round(overhang.Width) },
            { "thickness", Round(overhang.Thickness) },
            { "area", Round(overhang.Area) },
            { "origin", overhang.Origin },
            { "notes", overhang.Notes.Count == 0 ? null : string.Join(" ", overhang.Notes) }
        };

        private object RecessSummary(RecessView recess) => new Dictionary<string, object>
        {
            { "id", recess.ElementId },
            { "massId", recess.MassId },
            { "depthIntoMass", Round(recess.DepthIntoMass) },
            { "openingFaceId", recess.OpeningFaceId },
            { "interiorFaceIds", recess.InteriorFaceIds },
            { "area", Round(recess.Area) },
            { "volume", Round(recess.Volume) },
            { "notes", recess.Notes.Count == 0 ? null : string.Join(" ", recess.Notes) }
        };

        private object CutSummary(CutView cut) => new Dictionary<string, object>
        {
            { "id", cut.ElementId },
            { "massId", cut.MassId },
            { "volume", Round(cut.Volume) },
            { "bbox", cut.Bbox.ToJson() },
            { "topOpen", cut.TopOpen },
            { "bottomOpen", cut.BottomOpen },
            { "interiorFaceIds", cut.InteriorFaceIds },
            { "centroid", cut.Centroid.ToArray() },
            { "notes", cut.Notes.Count == 0 ? null : string.Join(" ", cut.Notes) }
        };

        private object SiteSummary(SiteView site) => new Dictionary<string, object>
        {
            { "id", site.ElementId },
            { "siteType", site.SiteType },
            { "name", ToolJson.Safe(site.Name) },
            { "layer", ToolJson.Safe(site.Layer) },
            { "bbox", site.Bbox.ToJson() },
            { "area", site.Area == null ? (object)null : Round(site.Area.Value) },
            { "length", site.Length == null ? (object)null : Round(site.Length.Value) },
            { "isClosedCurve", site.IsClosedCurve },
            { "classifiedBy", site.ClassifiedBy }
        };

        private static double Round(double value) => RhinoQueryService.Round(value);
    }
}
