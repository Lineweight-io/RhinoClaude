using RhinoClaude.Semantic;
using Xunit;

namespace RhinoClaude.Tests
{
    /// <summary>
    /// The shipped canonical convention (semantic plan §5.3) and the learned-convention
    /// override (§5.2 steps 2–3). These are the classifier's cheapest and most-used inputs,
    /// and they are pure string work — no Rhino needed to pin them down.
    /// </summary>
    public class LayerConventionTests
    {
        // ── Canonical matching ────────────────────────────────────────

        [Theory]
        [InlineData("MASS_Office", "Office")]
        [InlineData("MASS_Residential", "Residential")]
        [InlineData("MASS_Retail", "Retail")]
        [InlineData("MASS_Institutional", "Institutional")]
        [InlineData("MASS_Common", "Common")]
        [InlineData("MASS_Other", "Other")]
        public void CanonicalMassLayersCarryTheirFunction(string layer, string function)
        {
            var match = CanonicalConvention.Match(layer);

            Assert.Equal(SemanticVocabulary.Mass, match.ElementType);
            Assert.Equal(function, match.Subtype);
            Assert.Equal(SemanticVocabulary.ByCanonical, match.ClassifiedBy);
        }

        [Fact]
        public void AnUnknownMassSubcategoryStillClassifiesAsAMass()
        {
            // "prove it belongs in SD-level thinking" cuts both ways: an unrecognised function
            // must not cost the object its Mass classification.
            var match = CanonicalConvention.Match("MASS_Warehouse");

            Assert.Equal(SemanticVocabulary.Mass, match.ElementType);
            Assert.Equal(SemanticVocabulary.FunctionOther, match.Subtype);
        }

        [Fact]
        public void MatchingIsCaseInsensitive()
        {
            Assert.Equal(SemanticVocabulary.Mass, CanonicalConvention.Match("mass_office").ElementType);
            Assert.Equal(SemanticVocabulary.Site, CanonicalConvention.Match("Site_Street").ElementType);
        }

        [Fact]
        public void NestedLayersResolveThroughTheirAncestors()
        {
            var match = CanonicalConvention.Match("Building::MASS_Office");

            Assert.Equal(SemanticVocabulary.Mass, match.ElementType);
            Assert.Equal("Office", match.Subtype);
        }

        [Fact]
        public void AChildLayerInheritsItsParentsCategory()
        {
            var match = CanonicalConvention.Match("MASS_Office::Level 2");

            Assert.Equal(SemanticVocabulary.Mass, match.ElementType);
            Assert.Equal("Office", match.Subtype);
        }

        [Fact]
        public void TheLeafSegmentBeatsAnAncestor()
        {
            var match = CanonicalConvention.Match("MASS_Office::OPENING_Window");

            Assert.Equal(SemanticVocabulary.Opening, match.ElementType);
            Assert.Equal(SemanticVocabulary.OpeningWindow, match.Subtype);
        }

        [Theory]
        [InlineData("OPENING_Window", "Window")]
        [InlineData("OPENING_Door", "Door")]
        [InlineData("OPENING_Storefront", "Storefront")]
        [InlineData("OPENING_Louver", "Louver")]
        public void CanonicalOpeningLayersCarryTheirSubtype(string layer, string subtype)
        {
            var match = CanonicalConvention.Match(layer);

            Assert.Equal(SemanticVocabulary.Opening, match.ElementType);
            Assert.Equal(subtype, match.Subtype);
        }

        [Fact]
        public void HyphenatedCanonicalNamesMapOntoThePascalCaseEnum()
        {
            Assert.Equal(SemanticVocabulary.OpeningCurtainWall,
                CanonicalConvention.Match("OPENING_Curtain-Wall").Subtype);
            Assert.Equal("PropertyLine", CanonicalConvention.Match("SITE_Property-Line").Subtype);
            Assert.Equal("ContextBuilding", CanonicalConvention.Match("SITE_Context-Building").Subtype);
        }

        [Fact]
        public void TheEntrySuffixDoesNotChangeTheOpeningSubtype()
        {
            // Entry is a property on the Opening (plan §3.8), not a different subtype.
            var match = CanonicalConvention.Match("OPENING_Door_Entry");

            Assert.Equal(SemanticVocabulary.Opening, match.ElementType);
            Assert.Equal(SemanticVocabulary.OpeningDoor, match.Subtype);
            Assert.True(CanonicalConvention.IsEntryLayer("OPENING_Door_Entry"));
            Assert.False(CanonicalConvention.IsEntryLayer("OPENING_Door"));
        }

        [Theory]
        [InlineData("OVERHANG_Canopy", "Canopy")]
        [InlineData("OVERHANG_Balcony", "Balcony")]
        [InlineData("OVERHANG_Brise-Soleil", "Brise-Soleil")]
        [InlineData("CANOPY_Main", "Canopy")]
        [InlineData("EAVE_North", "Eave")]
        public void OverhangAliasPrefixesAreRecognised(string layer, string subtype)
        {
            var match = CanonicalConvention.Match(layer);

            Assert.Equal(SemanticVocabulary.Overhang, match.ElementType);
            Assert.Equal(subtype, match.Subtype);
        }

        [Fact]
        public void AnUnconventionalLayerMatchesNothing()
        {
            var match = CanonicalConvention.Match("Working::Sketches");

            Assert.False(match.IsMatch);
            Assert.Null(match.ElementType);
        }

        [Fact]
        public void SiteAndOpeningLayersAreExcludedFromMassInference()
        {
            Assert.True(CanonicalConvention.IsNonMassCategory("SITE_Context-Building"));
            Assert.True(CanonicalConvention.IsNonMassCategory("OPENING_Window"));
            Assert.True(CanonicalConvention.IsNonMassCategory("OVERHANG_Canopy"));
            Assert.False(CanonicalConvention.IsNonMassCategory("MASS_Office"));
            Assert.False(CanonicalConvention.IsNonMassCategory("Some Random Layer"));
        }

        // ── Level elevation parsing ───────────────────────────────────

        [Theory]
        [InlineData("LEVEL_01_+0ft", 0.0)]
        [InlineData("LEVEL_02_+12ft", 12.0)]
        [InlineData("LEVEL_Roof_+36ft", 36.0)]
        [InlineData("LEVEL_B1_-10ft", -10.0)]
        [InlineData("LEVEL_Mezz_+7.5ft", 7.5)]
        public void LevelLayersCarryTheirElevation(string layer, double elevation)
        {
            var match = CanonicalConvention.Match(layer);

            Assert.Equal(SemanticVocabulary.Level, match.ElementType);
            Assert.Equal(elevation, match.Elevation.Value, 6);
        }

        [Fact]
        public void ALevelLayerWithoutASignedNumberHasNoElevation()
        {
            var match = CanonicalConvention.Match("LEVEL_02");

            Assert.Equal(SemanticVocabulary.Level, match.ElementType);
            Assert.Null(match.Elevation);
        }

        [Fact]
        public void FloorPlateLayersDoNotReadAsMasses()
        {
            // FLOOR_* holds derived plates. Counting them as masses would double the program area.
            var match = CanonicalConvention.Match("FLOOR_L01");

            Assert.Equal(SemanticVocabulary.Level, match.ElementType);
        }

        // ── Learned convention (resolution rule steps 2–3) ────────────

        [Fact]
        public void ALearnedMappingBeatsTheCanonicalConvention()
        {
            var learned = new LayerConventionMap();
            learned.Add("MASS_Office", SemanticVocabulary.Site, "ContextBuilding");

            var resolver = new ConventionResolver(learned);
            var match = resolver.Resolve("MASS_Office");

            Assert.Equal(SemanticVocabulary.Site, match.ElementType);
            Assert.Equal(SemanticVocabulary.ByLearnedConvention, match.ClassifiedBy);
        }

        [Fact]
        public void ALearnedMappingTeachesTheClassifierAFirmsOwnNames()
        {
            var learned = new LayerConventionMap();
            learned.Add("BLDG-MASSING-OFFICE", SemanticVocabulary.Mass, "Office");

            var resolver = new ConventionResolver(learned);
            var match = resolver.Resolve("BLDG-MASSING-OFFICE");

            Assert.Equal(SemanticVocabulary.Mass, match.ElementType);
            Assert.Equal("Office", match.Subtype);
        }

        [Fact]
        public void ANullElementTypeMeansNotArchitecturalAndStopsTheSearch()
        {
            // Without the "Covers" short-circuit this would fall through to canonical and
            // classify as a Mass anyway, which defeats the point of teaching the mapping.
            var learned = new LayerConventionMap();
            learned.Add("MASS_Office", null, note: "legacy layer, no longer used");

            var resolver = new ConventionResolver(learned);

            Assert.False(resolver.Resolve("MASS_Office").IsMatch);
        }

        [Fact]
        public void TheDocumentMapBeatsTheFirmMap()
        {
            var firm = new LayerConventionMap();
            firm.Add("Shell", SemanticVocabulary.Mass, "Office");

            var doc = new LayerConventionMap();
            doc.Add("Shell", SemanticVocabulary.Mass, "Retail");

            var resolver = new ConventionResolver(doc, firm);

            Assert.Equal("Retail", resolver.Resolve("Shell").Subtype);
        }

        [Fact]
        public void TheFirmMapAppliesWhenTheDocumentHasNothingToSay()
        {
            var firm = new LayerConventionMap();
            firm.Add("Shell", SemanticVocabulary.Mass, "Office");

            var resolver = new ConventionResolver(new LayerConventionMap(), firm);

            Assert.Equal("Office", resolver.Resolve("Shell").Subtype);
        }

        [Fact]
        public void AnUncoveredLayerFallsThroughToCanonical()
        {
            var learned = new LayerConventionMap();
            learned.Add("Shell", SemanticVocabulary.Mass, "Office");

            var resolver = new ConventionResolver(learned);
            var match = resolver.Resolve("SITE_Street");

            Assert.Equal(SemanticVocabulary.Site, match.ElementType);
            Assert.Equal(SemanticVocabulary.ByCanonical, match.ClassifiedBy);
        }

        [Fact]
        public void AddingTheSameLayerTwiceReplacesRatherThanDuplicates()
        {
            var map = new LayerConventionMap();
            map.Add("Shell", SemanticVocabulary.Mass, "Office");
            map.Add("Shell", SemanticVocabulary.Mass, "Retail");

            Assert.Single(map.Entries);
            Assert.Equal("Retail", map.Entries[0].Subtype);
        }

        [Fact]
        public void FloorToFloorPrefersTheDocumentOverTheFirmDefault()
        {
            var firm = new LayerConventionMap { FloorToFloorDefault = 13 };
            var doc = new LayerConventionMap { FloorToFloorDefault = 11 };

            Assert.Equal(11, new ConventionResolver(doc, firm).FloorToFloorDefault);
            Assert.Equal(13, new ConventionResolver(new LayerConventionMap(), firm).FloorToFloorDefault);
            Assert.Equal(0, new ConventionResolver(null, null).FloorToFloorDefault);
        }

        // ── Persistence round-trip ────────────────────────────────────

        [Fact]
        public void TheMapSurvivesAJsonRoundTrip()
        {
            var original = new LayerConventionMap { FloorToFloorDefault = 12.5, Source = "learn:2026-08-14" };
            original.Add("BLDG-OFFICE", SemanticVocabulary.Mass, "Office", note: "the office bar");
            original.Add("L2", SemanticVocabulary.Level, "02", elevation: 12.0);
            original.Add("XREF", null);

            var restored = LayerConventionMap.FromJson(original.ToJson());

            Assert.NotNull(restored);
            Assert.Equal(LayerConventionMap.CurrentVersion, restored.Version);
            Assert.Equal(12.5, restored.FloorToFloorDefault);
            Assert.Equal("learn:2026-08-14", restored.Source);
            Assert.Equal(3, restored.Entries.Count);
            Assert.Equal(SemanticVocabulary.Mass, restored.Match("BLDG-OFFICE").ElementType);
            Assert.Equal(12.0, restored.Match("L2").Elevation.Value, 6);
            Assert.True(restored.Covers("XREF"));
            Assert.False(restored.Match("XREF").IsMatch);
        }

        [Fact]
        public void CorruptOrFutureVersionedJsonYieldsNullRatherThanThrowing()
        {
            Assert.Null(LayerConventionMap.FromJson("{not json"));
            Assert.Null(LayerConventionMap.FromJson("[]"));
            Assert.Null(LayerConventionMap.FromJson("{\"version\":99,\"entries\":[]}"));
            Assert.Null(LayerConventionMap.FromJson(null));
            Assert.Null(LayerConventionMap.FromJson("   "));
        }

        [Fact]
        public void EntriesWithoutALayerNameAreDropped()
        {
            var restored = LayerConventionMap.FromJson(
                "{\"version\":1,\"entries\":[{\"elementType\":\"Mass\"},{\"layer\":\"Shell\",\"elementType\":\"Mass\"}]}");

            Assert.Single(restored.Entries);
            Assert.Equal("Shell", restored.Entries[0].Layer);
        }

        // ── The shipped vocabulary itself ─────────────────────────────

        [Fact]
        public void EveryShippedCanonicalLayerActuallyClassifies()
        {
            // LAYER_CONVENTIONS.md documents this exact list; a layer in the doc the parser
            // cannot read would be a lie told to every user who follows it.
            foreach (var layer in CanonicalConvention.CanonicalLayers)
                Assert.True(CanonicalConvention.Match(layer).IsMatch, layer + " did not classify");
        }

        [Fact]
        public void TheTaxonomyIsElevenTypes()
        {
            Assert.Equal(7, SemanticVocabulary.FirstClassTypes.Length);
            Assert.Equal(4, SemanticVocabulary.CompositionTypes.Length);
            Assert.Equal(11, SemanticVocabulary.AllTypes.Length);
        }
    }
}
