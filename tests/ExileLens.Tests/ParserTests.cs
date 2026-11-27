global using Microsoft.VisualStudio.TestTools.UnitTesting;
using ExileLens.Core.Items;

namespace ExileLens.Tests;

[TestClass]
public sealed class ParserTests
{
    [TestMethod]
    public void Poe2RareAdvancedCopyParsesHeaderPropertiesAndExactAffixes()
    {
        var item = Fixture.Parse("poe2/rare-ring-advanced.txt", GameId.Poe2);

        Assert.AreEqual("Rings", item.ItemClass);
        Assert.AreEqual(ItemRarity.Rare, item.Rarity);
        Assert.AreEqual(ItemCategory.Equipment, item.Category);
        Assert.AreEqual("Doom Circle", item.Name);
        Assert.AreEqual("Gold Ring", item.BaseType);
        Assert.AreEqual(82, item.ItemLevel);
        Assert.AreEqual(60, item.RequiredLevel);
        Assert.IsTrue(item.HasAdvancedAffixInfo);
        Assert.IsTrue(item.ExplicitAffixCountIsExact);
        Assert.AreEqual(4, item.ExplicitAffixCount);

        var life = item.Modifiers.Single(m => m.Pattern == "# to maximum Life");
        Assert.AreEqual(85d, life.PrimaryValue);
        Assert.AreEqual(ModifierKind.Explicit, life.Kind);
        Assert.AreEqual("+85 to maximum Life", life.Text, "advanced roll ranges are stripped from display text");

        var implicitMod = item.Modifiers.Single(m => m.Kind == ModifierKind.Implicit);
        Assert.AreEqual("#% increased Rarity of Items found", implicitMod.Pattern);
    }

    [TestMethod]
    public void GemTagLineDoesNotHideLevelAndQuality()
    {
        var item = Fixture.Parse("poe1/war-banner-gem.txt", GameId.Poe1);

        Assert.AreEqual(ItemRarity.Gem, item.Rarity);
        Assert.AreEqual(ItemCategory.Gem, item.Category);
        Assert.AreEqual("War Banner", item.BaseType);
        Assert.AreEqual(12, item.GemLevel);
        Assert.AreEqual(17, item.Quality);
        Assert.AreEqual(52, item.RequiredLevel, "requirement level must not overwrite the gem level");
        Assert.IsTrue(item.IsCorrupted);
        Assert.IsFalse(item.Modifiers.Any(m => m.Text.StartsWith("Level", StringComparison.Ordinal)), "gem properties are not modifiers");
    }

    [TestMethod]
    public void ImbuedGemSupportIsParsedAndNotAModifier()
    {
        var item = Fixture.Parse("poe1/imbued-transfigured-gem.txt", GameId.Poe1);

        Assert.AreEqual("Arc of Oscillating", item.BaseType);
        Assert.AreEqual("Lifetap", item.ImbuedSupport);
        Assert.AreEqual(20, item.GemLevel);
        Assert.AreEqual(20, item.Quality);
        Assert.IsFalse(item.Modifiers.Any(m => m.Text.StartsWith("Supported by", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void SimpleCopyEstimatesAffixCountFromLines()
    {
        var item = Fixture.Parse("poe2/rare-ring-simple.txt", GameId.Poe2);

        Assert.IsFalse(item.HasAdvancedAffixInfo);
        Assert.IsFalse(item.ExplicitAffixCountIsExact);
        Assert.AreEqual(4, item.ExplicitAffixCount);
        Assert.AreEqual(5, item.Modifiers.Count);
    }

    [TestMethod]
    public void HybridAffixCountsOnceWhileKeepingBothLines()
    {
        var item = Fixture.Parse("poe2/rare-body-hybrid-advanced.txt", GameId.Poe2);

        Assert.AreEqual(6, item.ExplicitAffixCount, "hybrid prefix owns two lines but is one affix");
        var explicitLines = item.Modifiers.Count(m => m.Kind is ModifierKind.Explicit or ModifierKind.Desecrated);
        Assert.AreEqual(7, explicitLines);

        var hybrid = item.Affixes.Single(a => a.Name == "Fortified");
        Assert.AreEqual(AffixSlot.Prefix, hybrid.Slot);
        Assert.AreEqual(3, hybrid.Tier);
        Assert.AreEqual(2, hybrid.LineIndexes.Count);

        Assert.AreEqual(ModifierKind.Rune, item.Modifiers.First().Kind);
        Assert.AreEqual(ModifierKind.Desecrated, item.Modifiers.Single(m => m.Pattern == "# Life Regeneration per second").Kind);
        Assert.AreEqual(2, item.Sockets.Count);
        Assert.AreEqual(12, item.Quality);
        Assert.AreEqual(65, item.RequiredLevel);
        Assert.IsTrue(item.IsCorrupted);
    }

    [TestMethod]
    public void Poe1RareParsesSocketsInfluencesRequirementsAndCrafted()
    {
        var item = Fixture.Parse("poe1/rare-gloves.txt", GameId.Poe1);

        Assert.AreEqual("Sorcerer Gloves", item.BaseType);
        Assert.AreEqual(20, item.Quality);
        Assert.AreEqual(69, item.RequiredLevel);
        Assert.IsNull(item.GemLevel, "'Level' inside Requirements is the required level, not a gem level");
        Assert.AreEqual(4, item.Sockets.Count);
        Assert.AreEqual(4, item.Sockets.LargestLinkGroup);
        Assert.IsTrue(item.Sockets.HasLinkedRgb);
        CollectionAssert.AreEquivalent(new[] { "Searing Exarch", "Eater of Worlds" }, item.Influences.ToArray());
        Assert.AreEqual(ModifierKind.Crafted, item.Modifiers.Last().Kind);

        var accuracy = item.Modifiers.Single(m => m.Pattern == "# to Accuracy Rating");
        Assert.AreEqual(1240d, accuracy.PrimaryValue, "thousands separators are understood");
    }

    [TestMethod]
    public void UniqueFlavourTextAndNotesAreIgnored()
    {
        var item = Fixture.Parse("poe1/unique-amulet.txt", GameId.Poe1);

        Assert.AreEqual(ItemRarity.Unique, item.Rarity);
        Assert.AreEqual("Astramentis", item.Name);
        Assert.AreEqual(3, item.Modifiers.Count);
        Assert.IsFalse(item.Modifiers.Any(m => m.Text.Contains("Kaom", StringComparison.Ordinal)));
        Assert.AreEqual("-# Physical Damage taken from Attack Hits", item.Modifiers[^1].Pattern);
        Assert.AreEqual(-4d, item.Modifiers[^1].PrimaryValue);
    }

    [TestMethod]
    public void WaystoneParsesTierPropertiesAndMods()
    {
        var item = Fixture.Parse("poe2/waystone-t15.txt", GameId.Poe2);

        Assert.AreEqual(ItemCategory.Waystone, item.Category);
        Assert.AreEqual(15, item.MapTier);
        Assert.AreEqual("Waystone", item.BaseType);
        Assert.AreEqual("+48% (augmented)", item.Properties["Item Quantity"]);
        Assert.IsTrue(item.Properties.ContainsKey("Waystone Drop Chance"), "unknown properties are preserved");
        Assert.AreEqual(5, item.Modifiers.Count);
    }

    [TestMethod]
    public void UnidentifiedWaystoneHasNoModsAndTierFromBase()
    {
        var item = Fixture.Parse("poe2/waystone-unidentified.txt", GameId.Poe2);

        Assert.IsFalse(item.IsIdentified);
        Assert.AreEqual(12, item.MapTier);
        Assert.AreEqual(0, item.Modifiers.Count);
        Assert.IsNull(item.Name);
    }

    [TestMethod]
    public void CurrencyParsesStackSize()
    {
        var item = Fixture.Parse("poe2/exalted-orb.txt", GameId.Poe2);

        Assert.AreEqual(ItemCategory.Currency, item.Category);
        Assert.AreEqual("Exalted Orb", item.BaseType);
        Assert.AreEqual(7, item.StackSize);
        Assert.AreEqual(20, item.MaxStackSize);
    }

    [TestMethod]
    public void Poe1MapParsesTierAndModifiers()
    {
        var item = Fixture.Parse("poe1/cemetery-map.txt", GameId.Poe1);

        Assert.AreEqual(ItemCategory.Map, item.Category);
        Assert.AreEqual(16, item.MapTier);
        Assert.AreEqual(4, item.Modifiers.Count);
        Assert.AreEqual("Players have -#% to all maximum Resistances", item.Modifiers[1].Pattern);
    }

    [TestMethod]
    public void MagicItemKeepsPropertySectionsOutOfModifiers()
    {
        var item = Fixture.Parse("poe2/magic-wand.txt", GameId.Poe2);

        Assert.AreEqual(ItemRarity.Magic, item.Rarity);
        Assert.AreEqual(2, item.Modifiers.Count);
        Assert.IsTrue(item.Properties.ContainsKey("Grants Skill"));
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("hello world")]
    [DataRow("Some random text\n--------\nwith separators")]
    public void NonItemTextIsRejected(string text)
    {
        Assert.IsFalse(new ItemTextParser().TryParse(text, GameId.Poe2, out _, out var error));
        Assert.IsNotNull(error);
    }

    [TestMethod]
    public void UnknownFutureSectionsAreTolerated()
    {
        const string text = """
            Item Class: Rings
            Rarity: Rare
            Future Band
            Mythic Ring
            --------
            Brand New Property: 42
            Another One: something
            --------
            Item Level: 99
            --------
            +50 to maximum Life
            --------
            {{ Weird Header Nobody Knows }}
            --------
            Spectral Item
            """;
        Assert.IsTrue(new ItemTextParser().TryParse(text, GameId.Poe2, out var item, out _));
        Assert.AreEqual(99, item!.ItemLevel);
        Assert.AreEqual("42", item.Properties["Brand New Property"]);
        Assert.IsTrue(item.Modifiers.Any(m => m.Pattern == "# to maximum Life"));
    }

    [TestMethod]
    public void WindowsLineEndingsAreSupported()
    {
        var text = Fixture.Load("poe2/rare-ring-simple.txt").ReplaceLineEndings("\r\n");
        Assert.IsTrue(new ItemTextParser().TryParse(text, GameId.Poe2, out var item, out _));
        Assert.AreEqual("Gold Ring", item!.BaseType);
    }
}

[TestClass]
public sealed class ModifierTextTests
{
    [TestMethod]
    [DataRow("+85 to maximum Life", "# to maximum Life", 85d)]
    [DataRow("+85(80-89) to maximum Life", "# to maximum Life", 85d)]
    [DataRow("-12% maximum Player Resistances", "-#% maximum Player Resistances", -12d)]
    [DataRow("Adds 10 to 20 Fire Damage", "Adds # to # Fire Damage", 10d)]
    [DataRow("1.5% of Physical Attack Damage Leeched as Life", "#% of Physical Attack Damage Leeched as Life", 1.5d)]
    [DataRow("Monsters  are   Hexproof", "Monsters are Hexproof", double.NaN)]
    [DataRow("+#% to Fire Resistance", "#% to Fire Resistance", double.NaN)]
    public void NormalizeProducesStablePatterns(string input, string expected, double firstValue)
    {
        var pattern = ModifierText.Normalize(input, out var values);
        Assert.AreEqual(expected, pattern);
        if (double.IsNaN(firstValue))
        {
            Assert.AreEqual(0, values.Length);
        }
        else
        {
            Assert.AreEqual(firstValue, values[0], 1e-9);
        }
    }

    [TestMethod]
    public void TwoValuesAreBothCaptured()
    {
        ModifierText.Normalize("Adds 10(8-12) to 20(18-22) Fire Damage", out var values);
        CollectionAssert.AreEqual(new[] { 10d, 20d }, values);
    }
}
