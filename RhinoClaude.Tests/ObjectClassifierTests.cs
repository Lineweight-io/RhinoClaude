using RhinoClaude.Semantic;
using Xunit;

namespace RhinoClaude.Tests
{
    /// <summary>
    /// The four-step resolution rule at object level (semantic plan §5.2). Each step has to
    /// beat the one below it, and an object that matches nothing has to stay unclassified
    /// rather than being guessed into the model.
    /// </summary>
    public class ObjectClassifierTests
    {
        private static readonly UnitContext Feet = UnitContext.Feet();

        private static ObjectFacts Solid(string layer, double volume = 30000, string name = null)
        {
            return new ObjectFacts
            {
                ObjectId = "00000000-0000-0000-0000-000000000001",
                LayerFullPath = layer,
                Name = name,
                IsBrep = true,
                IsClosedSolid = true,
                Volume = volume,
                Bbox = BoxView.From(new Vec3(0, 0, 0), new Vec3(30, 50, 20))
            };
        }

        private static ConventionResolver Canonical => new ConventionResolver(null);

        // ── Step 1: explicit tag ──────────────────────────────────────

        [Fact]
        public void AnExplicitTagBeatsTheLayerConvention()
        {
            var facts = Solid("SITE_Context-Building");
            facts.UserStrings["RhinoClaude:Element:Mass"] = "";
            facts.UserStrings[SemanticVocabulary.KeyMassFunction] = "Retail";

            var verdict = ObjectClassifier.Classify(facts, Canonical, Feet);

            Assert.Equal(SemanticVocabulary.Mass, verdict.ElementType);
            Assert.Equal("Retail", verdict.Subtype);
            Assert.Equal(SemanticVocabulary.ByUserData, verdict.ClassifiedBy);
        }

        [Fact]
        public void TheBareKeyFormOfTheTagIsAlsoRead()
        {
            // Two spellings exist in the wild: the keyed form the plan uses for MassGroup, and
            // a plain RhinoClaude:Element = "Mass" pair. Both mean the same thing.
            var facts = Solid("Whatever");
            facts.UserStrings["RhinoClaude:Element"] = "Mass";

            var verdict = ObjectClassifier.Classify(facts, Canonical, Feet);

            Assert.Equal(SemanticVocabulary.Mass, verdict.ElementType);
            Assert.Equal(SemanticVocabulary.ByUserData, verdict.ClassifiedBy);
        }

        [Fact]
        public void AMassGroupTagCarriesTheGroupNameInTheKey()
        {
            var facts = Solid("MASS_Office");
            facts.UserStrings["RhinoClaude:Element:MassGroup:Office Wing"] = "";

            var verdict = ObjectClassifier.Classify(facts, Canonical, Feet);

            Assert.Equal(SemanticVocabulary.MassGroup, verdict.ElementType);
            Assert.Equal("Office Wing", verdict.MassGroupName);
        }

        [Fact]
        public void AnOpeningTagCarriesSubtypeAndEntryPromotion()
        {
            var facts = Solid("Working", volume: 40);
            facts.UserStrings["RhinoClaude:Element:Opening"] = "";
            facts.UserStrings[SemanticVocabulary.KeyOpeningType] = "Storefront";
            facts.UserStrings[SemanticVocabulary.KeyEntryType] = "Main";

            var verdict = ObjectClassifier.Classify(facts, Canonical, Feet);

            Assert.Equal(SemanticVocabulary.Opening, verdict.ElementType);
            Assert.Equal(SemanticVocabulary.OpeningStorefront, verdict.Subtype);
            Assert.True(verdict.IsEntry);
            Assert.Equal("Main", verdict.EntryType);
        }

        [Fact]
        public void AnUnknownTagValueIsIgnoredRatherThanInventingAType()
        {
            var facts = Solid("MASS_Office");
            facts.UserStrings["RhinoClaude:Element:Doodad"] = "";

            var verdict = ObjectClassifier.Classify(facts, Canonical, Feet);

            Assert.Equal(SemanticVocabulary.Mass, verdict.ElementType);
            Assert.Equal(SemanticVocabulary.ByCanonical, verdict.ClassifiedBy);
        }

        // ── Steps 2 and 3: convention ─────────────────────────────────

        [Fact]
        public void ACanonicalMassLayerClassifiesASolid()
        {
            var verdict = ObjectClassifier.Classify(Solid("MASS_Office"), Canonical, Feet);

            Assert.Equal(SemanticVocabulary.Mass, verdict.ElementType);
            Assert.Equal("Office", verdict.Subtype);
            Assert.Equal(SemanticVocabulary.ByCanonical, verdict.ClassifiedBy);
        }

        [Fact]
        public void ACurveOnAMassLayerIsNotAMass()
        {
            // A construction line on MASS_Office would otherwise land in every program-area
            // total with zero volume.
            var facts = new ObjectFacts
            {
                LayerFullPath = "MASS_Office",
                IsCurve = true,
                IsClosedCurve = true,
                Bbox = BoxView.From(new Vec3(0, 0, 0), new Vec3(30, 50, 0))
            };

            var verdict = ObjectClassifier.Classify(facts, Canonical, Feet);

            Assert.False(verdict.IsClassified);
        }

        [Fact]
        public void ASiteLayerKeepsAContextBuildingOutOfTheMassCount()
        {
            var verdict = ObjectClassifier.Classify(Solid("SITE_Context-Building"), Canonical, Feet);

            Assert.Equal(SemanticVocabulary.Site, verdict.ElementType);
            Assert.Equal("ContextBuilding", verdict.Subtype);
        }

        [Fact]
        public void AnOpeningLayerObjectCarriesItsEntryFlagFromTheLayerName()
        {
            var facts = Solid("OPENING_Storefront_Entry", volume: 200);

            var verdict = ObjectClassifier.Classify(facts, Canonical, Feet);

            Assert.Equal(SemanticVocabulary.Opening, verdict.ElementType);
            Assert.Equal(SemanticVocabulary.OpeningStorefront, verdict.Subtype);
            Assert.True(verdict.IsEntry);
            Assert.Equal("Main", verdict.EntryType);
        }

        [Fact]
        public void ALearnedConventionClassifiesAFirmsOwnLayerName()
        {
            var learned = new LayerConventionMap();
            learned.Add("BLDG-MASSING", SemanticVocabulary.Mass, "Institutional");

            var verdict = ObjectClassifier.Classify(
                Solid("BLDG-MASSING"), new ConventionResolver(learned), Feet);

            Assert.Equal(SemanticVocabulary.Mass, verdict.ElementType);
            Assert.Equal("Institutional", verdict.Subtype);
            Assert.Equal(SemanticVocabulary.ByLearnedConvention, verdict.ClassifiedBy);
        }

        // ── Object name prefix ────────────────────────────────────────

        [Fact]
        public void AnObjectNamedMassClassifiesAsOne()
        {
            var verdict = ObjectClassifier.Classify(
                Solid("Working::Shapes", name: "Mass: Office bar"), Canonical, Feet);

            Assert.Equal(SemanticVocabulary.Mass, verdict.ElementType);
            Assert.Equal("Office", verdict.Subtype);
            Assert.Equal(SemanticVocabulary.ByCanonical, verdict.ClassifiedBy);
        }

        [Fact]
        public void AMassNamedObjectThatIsNotASolidDoesNotClassify()
        {
            var facts = new ObjectFacts { Name = "Mass: Office", IsCurve = true, LayerFullPath = "Working" };

            Assert.False(ObjectClassifier.Classify(facts, Canonical, Feet).IsClassified);
        }

        // ── Step 4: geometry inference ────────────────────────────────

        [Fact]
        public void ABigClosedSolidOnAnUnknownLayerIsInferredToBeAMass()
        {
            var verdict = ObjectClassifier.Classify(Solid("Working::Shapes"), Canonical, Feet);

            Assert.Equal(SemanticVocabulary.Mass, verdict.ElementType);
            Assert.Equal(SemanticVocabulary.ByGeometryInference, verdict.ClassifiedBy);
            Assert.Contains("Confirm before any destructive change", verdict.Note);
        }

        [Fact]
        public void ASmallSolidIsBelowTheMassThresholdAndStaysUnclassified()
        {
            var verdict = ObjectClassifier.Classify(Solid("Working::Shapes", volume: 40), Canonical, Feet);

            Assert.False(verdict.IsClassified);
            Assert.Contains("below the mass threshold", verdict.Note, System.StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void TheVolumeThresholdIsInFeetNotModelUnits()
        {
            // 30 000 mm³ is a sugar cube; without the unit conversion it would clear a
            // 1000-unit threshold and be classified as a building.
            var millimetres = new UnitContext(304.8, "Millimeters");
            var facts = Solid("Working", volume: 30000);

            Assert.False(ObjectClassifier.Classify(facts, Canonical, millimetres).IsClassified);

            // The same building in millimetres: 30 m × 50 m × 20 m.
            facts.Volume = 30000.0 * 50000.0 * 20000.0;
            Assert.Equal(SemanticVocabulary.Mass,
                ObjectClassifier.Classify(facts, Canonical, millimetres).ElementType);
        }

        [Fact]
        public void InferenceNeverOverrulesANonMassLayer()
        {
            // A big closed solid on SITE_Context-Building is a neighbour, not the design.
            var verdict = ObjectClassifier.FromGeometry(Solid("SITE_Context-Building"), Canonical, Feet);

            Assert.False(verdict.IsClassified);
        }

        [Fact]
        public void InferenceReadsAFunctionOutOfAFreeTextName()
        {
            var verdict = ObjectClassifier.Classify(
                Solid("Working", name: "residential tower study"), Canonical, Feet);

            Assert.Equal(SemanticVocabulary.Mass, verdict.ElementType);
            Assert.Equal("Residential", verdict.Subtype);
        }

        [Fact]
        public void AnUnnamedInferredMassFallsBackToFunctionOther()
        {
            var verdict = ObjectClassifier.Classify(Solid("Working"), Canonical, Feet);

            Assert.Equal(SemanticVocabulary.FunctionOther, verdict.Subtype);
        }

        [Fact]
        public void OpenBrepsAreNeverMasses()
        {
            var facts = Solid("MASS_Office");
            facts.IsClosedSolid = false;

            Assert.False(ObjectClassifier.Classify(facts, Canonical, Feet).IsClassified);
        }
    }
}
