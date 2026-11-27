using System.Globalization;

namespace ExileLens.Core.Items;

public interface IItemParser
{
    /// <summary>Parses clipboard item text. Returns false (with a reason) when the text is not a game item.</summary>
    bool TryParse(string text, GameId game, out ParsedItem? item, out string? error);
}

/// <summary>
/// Tolerant parser for the clipboard text produced by the game's item copy (Ctrl+C) and advanced item copy
/// (Ctrl+Alt+C) in Path of Exile 1 and 2. Unknown lines and properties never fail the parse; they are kept
/// in <see cref="ParsedItem.UnparsedLines"/> / <see cref="ParsedItem.Properties"/>.
/// </summary>
public sealed class ItemTextParser : IItemParser
{
    private const string Separator = "--------";

    /// <summary>PoE 1 synthesised items carry this prefix on the base line; the trade API only knows the plain base.</summary>
    private const string SynthesisedPrefix = "Synthesised ";

    /// <summary>PoE 1 imbued gem line: "Supported by Level 1 Lifetap".</summary>
    private const string ImbuedPrefix = "Supported by Level ";

    private static readonly string[] DescriptionPrefixes =
    [
        "Right click", "Place into", "Travel to this", "Can be used in", "Stack Size", "Shift click",
        "Use this", "Socket this", "Can only be", "Charm Slots", "Combine this", "Click to",
        "Take this item", "Place this", "Can be applied", "Used by", "Modifiers on this", "Unidentified items",
        "Can be socketed",
    ];

    private static readonly string[] InfluenceLines =
    [
        "Shaper Item", "Elder Item", "Crusader Item", "Redeemer Item", "Hunter Item", "Warlord Item",
        "Searing Exarch Item", "Eater of Worlds Item",
    ];

    public bool TryParse(string text, GameId game, out ParsedItem? item, out string? error)
    {
        item = null;
        error = null;

        if (string.IsNullOrWhiteSpace(text))
        {
            error = "Clipboard is empty.";
            return false;
        }

        var sections = SplitSections(text);
        if (sections.Count == 0 || sections[0].Count < 2)
        {
            error = "Clipboard text is not a Path of Exile item.";
            return false;
        }

        var header = sections[0];
        string? itemClass = null;
        var rarity = ItemRarity.Unknown;
        var nameLines = new List<string>(2);

        foreach (var line in header)
        {
            if (line.StartsWith("Item Class:", StringComparison.Ordinal))
            {
                itemClass = line["Item Class:".Length..].Trim();
            }
            else if (line.StartsWith("Rarity:", StringComparison.Ordinal))
            {
                rarity = ParseRarity(line["Rarity:".Length..].Trim());
            }
            else
            {
                nameLines.Add(line);
            }
        }

        if (rarity == ItemRarity.Unknown && itemClass is null)
        {
            error = "Clipboard text is not a Path of Exile item.";
            return false;
        }

        if (nameLines.Count == 0)
        {
            error = "Item name is missing.";
            return false;
        }

        string? name;
        string baseType;
        if (nameLines.Count >= 2)
        {
            name = nameLines[0];
            baseType = nameLines[1];
        }
        else
        {
            name = null;
            baseType = nameLines[0];
        }

        var props = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var modifiers = new List<ItemModifier>(12);
        var affixes = new List<ItemAffix>(8);
        var unparsed = new List<string>();
        var influences = new List<string>();
        int? itemLevel = null, quality = null, gemLevel = null, mapTier = null, stack = null, maxStack = null, reqLevel = null;
        string? imbuedSupport = null;
        bool corrupted = false, identified = true, mirrored = false, fractured = false, synthesised = false, advanced = false;
        var sockets = SocketInfo.None;
        var modifierSectionCount = 0;
        var lastModSectionStart = -1;
        var lastModSectionHasDigits = true;

        for (var s = 1; s < sections.Count; s++)
        {
            var section = sections[s];
            if (section.Count == 0)
            {
                continue;
            }

            var first = section[0];

            // Single-line flag sections.
            if (section.Count == 1)
            {
                switch (first)
                {
                    case "Corrupted":
                        corrupted = true;
                        continue;
                    case "Unidentified":
                        identified = false;
                        continue;
                    case "Mirrored":
                        mirrored = true;
                        continue;
                    case "Split":
                    case "Unmodifiable":
                        props[first] = "true";
                        continue;
                    case "Fractured Item":
                        fractured = true;
                        continue;
                    case "Synthesised Item":
                        synthesised = true;
                        continue;
                }
            }

            if (first.StartsWith("Note:", StringComparison.Ordinal))
            {
                continue;
            }

            if (rarity == ItemRarity.Gem && section.Count == 1 && TryParseImbuedSupport(first) is { } support)
            {
                imbuedSupport = support;
                continue;
            }

            if (Array.Exists(InfluenceLines, l => section.Contains(l)))
            {
                foreach (var l in section)
                {
                    if (l.EndsWith(" Item", StringComparison.Ordinal))
                    {
                        influences.Add(l[..^" Item".Length]);
                    }
                }

                continue;
            }

            if (IsDescriptionSection(section))
            {
                continue;
            }

            var inRequirements = false;
            var isPropertySection = true;
            foreach (var line in section)
            {
                if (!LooksLikeProperty(line) && line != "Requirements:")
                {
                    isPropertySection = false;
                    break;
                }
            }

            // Gem property blocks open with a tag line ("Aura, Banner, Spell, AoE, Duration") and may carry free-text
            // lines between "Key: value" pairs; the first block before any modifiers is still the property block.
            var lenientGemSection = !isPropertySection && rarity == ItemRarity.Gem && modifierSectionCount == 0 && HasPropertyLine(section);
            if (isPropertySection || lenientGemSection)
            {
                foreach (var line in section)
                {
                    if (line == "Requirements:")
                    {
                        inRequirements = true;
                        continue;
                    }

                    if (lenientGemSection && !LooksLikeProperty(line))
                    {
                        continue;
                    }

                    var colon = line.IndexOf(": ", StringComparison.Ordinal);
                    var key = line[..colon];
                    var value = line[(colon + 2)..].Trim();
                    switch (key)
                    {
                        case "Item Level":
                            itemLevel = LeadingInt(value);
                            break;
                        case "Quality":
                            quality = LeadingInt(value);
                            break;
                        case "Level" when inRequirements:
                            reqLevel = LeadingInt(value);
                            break;
                        case "Level":
                            gemLevel = LeadingInt(value);
                            break;
                        case "Requires":
                            reqLevel = ParseRequiresLevel(value);
                            break;
                        case "Map Tier":
                        case "Waystone Tier":
                            mapTier = LeadingInt(value);
                            break;
                        case "Stack Size":
                            ParseStack(value, out stack, out maxStack);
                            break;
                        case "Sockets":
                            sockets = ParseSockets(value);
                            break;
                    }

                    props[key] = value;
                }

                continue;
            }

            // Modifier section (may include advanced headers).
            modifierSectionCount++;
            lastModSectionStart = modifiers.Count;
            lastModSectionHasDigits = false;
            var currentGroup = -1;
            List<int>? currentLines = null;
            AffixSlot currentSlot = AffixSlot.Unknown;
            string? currentName = null;
            int? currentTier = null;
            var currentKind = ModifierKind.Explicit;

            void FlushAffix()
            {
                if (currentLines is { Count: > 0 })
                {
                    affixes.Add(new ItemAffix(currentSlot, currentName, currentTier, currentKind, currentLines));
                }

                currentLines = null;
            }

            foreach (var raw in section)
            {
                if (raw.StartsWith('{') && raw.EndsWith('}'))
                {
                    FlushAffix();
                    advanced = true;
                    ParseAffixHeader(raw, out currentSlot, out currentName, out currentTier, out currentKind);
                    currentGroup = affixes.Count + 1000;
                    currentLines = [];
                    continue;
                }

                if (raw.StartsWith('(') && raw.EndsWith(')'))
                {
                    continue; // reminder text in advanced copy
                }

                var fallbackKind = currentLines is not null ? currentKind : ModifierKind.Explicit;
                var body = ModifierText.StripKindSuffix(raw, out var kind, fallbackKind);
                if (body.IsEmpty)
                {
                    continue;
                }

                var pattern = ModifierText.Normalize(body, out var values);
                if (values.Length > 0)
                {
                    lastModSectionHasDigits = true;
                }

                var group = currentLines is not null ? currentGroup : modifiers.Count;
                currentLines?.Add(modifiers.Count);
                modifiers.Add(new ItemModifier(ModifierText.StripRanges(body), pattern, values, kind, group));
            }

            FlushAffix();
        }

        // Unique flavour text: a trailing modifier section without any numbers.
        if (rarity == ItemRarity.Unique && modifierSectionCount >= 2 && !lastModSectionHasDigits && lastModSectionStart >= 0)
        {
            modifiers.RemoveRange(lastModSectionStart, modifiers.Count - lastModSectionStart);
        }

        // "Synthesised Harmonic Spirit Shield" → "Harmonic Spirit Shield" (the trade API rejects the prefixed base).
        if (baseType.Length > SynthesisedPrefix.Length && baseType.StartsWith(SynthesisedPrefix, StringComparison.Ordinal))
        {
            baseType = baseType[SynthesisedPrefix.Length..];
            synthesised = true;
        }

        var category = DetectCategory(itemClass, baseType, rarity);

        if (rarity == ItemRarity.Unknown)
        {
            rarity = category is ItemCategory.Currency or ItemCategory.Essence or ItemCategory.Scarab or ItemCategory.Omen or ItemCategory.Rune
                ? ItemRarity.Currency
                : ItemRarity.Normal;
        }

        if (!identified && rarity is ItemRarity.Rare or ItemRarity.Unique && nameLines.Count == 1)
        {
            name = null;
        }

        item = new ParsedItem
        {
            Game = game,
            RawText = text,
            ItemClass = itemClass,
            Rarity = rarity,
            Category = category,
            Name = name,
            BaseType = StripMapTierSuffix(baseType),
            ItemLevel = itemLevel,
            Quality = quality,
            GemLevel = gemLevel,
            ImbuedSupport = imbuedSupport,
            MapTier = mapTier ?? ParseTierFromBase(baseType),
            StackSize = stack,
            MaxStackSize = maxStack,
            RequiredLevel = reqLevel,
            IsCorrupted = corrupted,
            IsIdentified = identified,
            IsMirrored = mirrored,
            IsFractured = fractured,
            IsSynthesised = synthesised,
            IsFoulborn = rarity == ItemRarity.Unique && name is not null && name.StartsWith(ParsedItem.FoulbornPrefix, StringComparison.Ordinal),
            Sockets = sockets,
            Influences = influences,
            Modifiers = modifiers,
            Affixes = affixes,
            Properties = props,
            HasAdvancedAffixInfo = advanced,
            UnparsedLines = unparsed,
        };
        return true;
    }

    internal static List<List<string>> SplitSections(string text)
    {
        var sections = new List<List<string>>(8);
        var current = new List<string>(6);
        foreach (var rawLine in text.AsSpan().EnumerateLines())
        {
            var line = rawLine.Trim();
            if (line.IsEmpty)
            {
                continue;
            }

            if (line.SequenceEqual(Separator))
            {
                if (current.Count > 0)
                {
                    sections.Add(current);
                    current = new List<string>(6);
                }

                continue;
            }

            current.Add(line.ToString());
        }

        if (current.Count > 0)
        {
            sections.Add(current);
        }

        return sections;
    }

    private static ItemRarity ParseRarity(string value) => value switch
    {
        "Normal" => ItemRarity.Normal,
        "Magic" => ItemRarity.Magic,
        "Rare" => ItemRarity.Rare,
        "Unique" => ItemRarity.Unique,
        "Currency" => ItemRarity.Currency,
        "Gem" => ItemRarity.Gem,
        "Divination Card" => ItemRarity.DivinationCard,
        "Quest" => ItemRarity.Quest,
        _ => ItemRarity.Unknown,
    };

    internal static ItemCategory DetectCategory(string? itemClass, string baseType, ItemRarity rarity)
    {
        var cls = itemClass ?? string.Empty;
        if (cls.Contains("Waystone", StringComparison.OrdinalIgnoreCase))
        {
            return ItemCategory.Waystone;
        }

        if (cls.Equals("Maps", StringComparison.OrdinalIgnoreCase) || cls.Equals("Map", StringComparison.OrdinalIgnoreCase))
        {
            return ItemCategory.Map;
        }

        if (cls.Contains("Divination", StringComparison.OrdinalIgnoreCase) || rarity == ItemRarity.DivinationCard)
        {
            return ItemCategory.DivinationCard;
        }

        if (cls.Contains("Gem", StringComparison.OrdinalIgnoreCase) || rarity == ItemRarity.Gem)
        {
            return ItemCategory.Gem;
        }

        if (cls.Contains("Jewel", StringComparison.OrdinalIgnoreCase))
        {
            return ItemCategory.Jewel;
        }

        if (cls.Contains("Flask", StringComparison.OrdinalIgnoreCase))
        {
            return ItemCategory.Flask;
        }

        if (cls.Contains("Charm", StringComparison.OrdinalIgnoreCase))
        {
            return ItemCategory.Charm;
        }

        if (cls.Contains("Tablet", StringComparison.OrdinalIgnoreCase))
        {
            return ItemCategory.Tablet;
        }

        if (cls.Contains("Relic", StringComparison.OrdinalIgnoreCase))
        {
            return ItemCategory.Relic;
        }

        if (cls.Contains("Fragment", StringComparison.OrdinalIgnoreCase) || cls.Contains("Map Items", StringComparison.OrdinalIgnoreCase)
            || cls.Contains("Pinnacle", StringComparison.OrdinalIgnoreCase) || cls.Contains("Splinter", StringComparison.OrdinalIgnoreCase))
        {
            return ItemCategory.Fragment;
        }

        var isStackable = cls.Contains("Currency", StringComparison.OrdinalIgnoreCase) || cls.Contains("Socketable", StringComparison.OrdinalIgnoreCase)
                          || cls.Contains("Omen", StringComparison.OrdinalIgnoreCase) || rarity == ItemRarity.Currency;
        if (isStackable)
        {
            if (baseType.Contains("Essence", StringComparison.OrdinalIgnoreCase))
            {
                return ItemCategory.Essence;
            }

            if (baseType.Contains("Scarab", StringComparison.OrdinalIgnoreCase))
            {
                return ItemCategory.Scarab;
            }

            if (baseType.StartsWith("Omen", StringComparison.OrdinalIgnoreCase) || cls.Contains("Omen", StringComparison.OrdinalIgnoreCase))
            {
                return ItemCategory.Omen;
            }

            if (baseType.Contains("Soul Core", StringComparison.OrdinalIgnoreCase))
            {
                return ItemCategory.SoulCore;
            }

            if (baseType.EndsWith("Rune", StringComparison.OrdinalIgnoreCase) || baseType.Contains(" Rune of ", StringComparison.OrdinalIgnoreCase))
            {
                return ItemCategory.Rune;
            }

            return ItemCategory.Currency;
        }

        return rarity is ItemRarity.Normal or ItemRarity.Magic or ItemRarity.Rare or ItemRarity.Unique
            ? ItemCategory.Equipment
            : ItemCategory.Other;
    }

    private static bool IsDescriptionSection(List<string> section)
    {
        foreach (var prefix in DescriptionPrefixes)
        {
            if (section[0].StartsWith(prefix, StringComparison.Ordinal) && !section[0].StartsWith("Stack Size:", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>"Supported by Level 1 Lifetap" → "Lifetap"; null for any other line.</summary>
    private static string? TryParseImbuedSupport(string line)
    {
        if (!line.StartsWith(ImbuedPrefix, StringComparison.Ordinal))
        {
            return null;
        }

        var rest = line.AsSpan(ImbuedPrefix.Length);
        var digits = 0;
        while (digits < rest.Length && char.IsAsciiDigit(rest[digits]))
        {
            digits++;
        }

        if (digits == 0 || digits >= rest.Length || rest[digits] != ' ')
        {
            return null;
        }

        // The game may separate the name with a zero-width space.
        var name = rest[(digits + 1)..].Trim().Trim('\u200B');
        return name.IsEmpty ? null : name.ToString();
    }

    private static bool HasPropertyLine(List<string> section)
    {
        foreach (var line in section)
        {
            if (LooksLikeProperty(line))
            {
                return true;
            }
        }

        return false;
    }

    private static bool LooksLikeProperty(string line)
    {
        var colon = line.IndexOf(": ", StringComparison.Ordinal);
        if (colon <= 0 || colon > 40)
        {
            return false;
        }

        // Property keys are words only (letters, spaces, apostrophes), e.g. "Item Level", "Attacks per Second".
        for (var i = 0; i < colon; i++)
        {
            var c = line[i];
            if (!(char.IsLetter(c) || c == ' ' || c == '\''))
            {
                return false;
            }
        }

        return true;
    }

    private static void ParseAffixHeader(string header, out AffixSlot slot, out string? name, out int? tier, out ModifierKind kind)
    {
        var inner = header.AsSpan(1, header.Length - 2).Trim();
        slot = AffixSlot.Other;
        if (inner.Contains("Prefix", StringComparison.Ordinal))
        {
            slot = AffixSlot.Prefix;
        }
        else if (inner.Contains("Suffix", StringComparison.Ordinal))
        {
            slot = AffixSlot.Suffix;
        }
        else if (inner.Contains("Implicit", StringComparison.Ordinal))
        {
            slot = AffixSlot.Implicit;
        }
        else if (inner.Contains("Unique", StringComparison.Ordinal))
        {
            slot = AffixSlot.Unique;
        }

        kind = slot == AffixSlot.Implicit ? ModifierKind.Implicit : ModifierKind.Explicit;
        if (inner.StartsWith("Master Crafted", StringComparison.Ordinal) || inner.StartsWith("Crafted", StringComparison.Ordinal))
        {
            kind = ModifierKind.Crafted;
        }
        else if (inner.StartsWith("Fractured", StringComparison.Ordinal))
        {
            kind = ModifierKind.Fractured;
        }
        else if (inner.StartsWith("Desecrated", StringComparison.Ordinal))
        {
            kind = ModifierKind.Desecrated;
        }
        else if (inner.StartsWith("Foulborn", StringComparison.Ordinal))
        {
            kind = ModifierKind.Mutated;
        }

        name = null;
        var q1 = inner.IndexOf('"');
        if (q1 >= 0)
        {
            var rest = inner[(q1 + 1)..];
            var q2 = rest.IndexOf('"');
            if (q2 > 0)
            {
                name = rest[..q2].ToString();
            }
        }

        tier = null;
        foreach (var marker in (ReadOnlySpan<string>)["(Tier: ", "(Rank: "])
        {
            var t = inner.IndexOf(marker, StringComparison.Ordinal);
            if (t >= 0)
            {
                tier = LeadingInt(inner[(t + marker.Length)..].ToString());
                break;
            }
        }
    }

    private static int? LeadingInt(string value)
    {
        var span = value.AsSpan().TrimStart();
        if (!span.IsEmpty && span[0] == '+')
        {
            span = span[1..];
        }

        var end = 0;
        while (end < span.Length && (char.IsAsciiDigit(span[end]) || span[end] == ','))
        {
            end++;
        }

        if (end == 0)
        {
            return null;
        }

        var digits = span[..end].ToString().Replace(",", string.Empty, StringComparison.Ordinal);
        return int.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    private static int? ParseRequiresLevel(string value)
    {
        // PoE 2: "Level 60, 100 Str" ; also "Level 60"
        var idx = value.IndexOf("Level ", StringComparison.Ordinal);
        return idx >= 0 ? LeadingInt(value[(idx + 6)..]) : null;
    }

    private static void ParseStack(string value, out int? stack, out int? max)
    {
        var slash = value.IndexOf('/');
        if (slash < 0)
        {
            stack = LeadingInt(value);
            max = null;
            return;
        }

        stack = LeadingInt(value[..slash]);
        max = LeadingInt(value[(slash + 1)..]);
    }

    internal static SocketInfo ParseSockets(string value)
    {
        // PoE 1: "R-G-B B-W", PoE 2: "S S S" (rune sockets, no links).
        var count = 0;
        var largest = 0;
        var hasRgb = false;
        foreach (var group in value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = group.Split('-', StringSplitOptions.RemoveEmptyEntries);
            count += parts.Length;
            largest = Math.Max(largest, parts.Length);
            bool r = false, g = false, b = false;
            foreach (var p in parts)
            {
                switch (p)
                {
                    case "R": r = true; break;
                    case "G": g = true; break;
                    case "B": b = true; break;
                    case "W": break;
                }
            }

            hasRgb |= r && g && b;
        }

        return new SocketInfo(count, largest, value) { HasLinkedRgb = hasRgb };
    }

    private static int? ParseTierFromBase(string baseType)
    {
        // "Waystone (Tier 15)" / "Something Map (Tier 16)"
        var idx = baseType.IndexOf("(Tier ", StringComparison.Ordinal);
        return idx >= 0 ? LeadingInt(baseType[(idx + 6)..]) : null;
    }

    private static string StripMapTierSuffix(string baseType)
    {
        var idx = baseType.IndexOf(" (Tier ", StringComparison.Ordinal);
        return idx > 0 ? baseType[..idx] : baseType;
    }
}
