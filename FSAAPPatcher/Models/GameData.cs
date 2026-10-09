using System.Collections.Generic;

namespace FSAAPPatcher.Models
{
	/// <summary>
	/// Central lookup tables for FSA game data: level names, item IDs, actor types.
	/// All data sourced from "Hex & Dolphin Memory Notes.txt" and actor templates.
	/// </summary>
	public static class GameData
	{
		// ── Level names keyed by numeric ID string (e.g. "010") ──────────────

		public static readonly IReadOnlyDictionary<string, LevelInfo> Levels =
			new Dictionary<string, LevelInfo>
			{
				// World 1 – Whereabouts of the Wind
				["010"] = new("Lake Hylia", 0, "Whereabouts of the Wind", "1-1"),
				["011"] = new("Cave of No Return", 0, "Whereabouts of the Wind", "1-2"),
				["012"] = new("The River's Flow (cut)", 0, "Whereabouts of the Wind", ""),
				["013"] = new("Hyrule Castle", 0, "Whereabouts of the Wind", "1-3"),
				// World 2 – Eastern Hyrule
				["020"] = new("The Coast", 1, "Eastern Hyrule", "2-1"),
				["021"] = new("Village of the Blue Maiden", 1, "Eastern Hyrule", "2-2"),
				["022"] = new("Swamp", 1, "Eastern Hyrule", ""),
				["023"] = new("Eastern Temple", 1, "Eastern Hyrule", "2-3"),
				// World 3 – Death Mountain
				["030"] = new("Death Mountain Foothills", 2, "Death Mountain", "3-1"),
				["031"] = new("The Mountain Path", 2, "Death Mountain", "3-2"),
				["032"] = new("Death Mountain", 2, "Death Mountain", ""),
				["033"] = new("Tower of Flames", 2, "Death Mountain", "3-3"),
				// World 4 – Near the Fields
				["040"] = new("The Field", 3, "Near the Fields", "4-1"),
				["041"] = new("The Swamp", 3, "Near the Fields", "4-2"),
				["042"] = new("Infiltration of Hyrule Castle", 3, "Near the Fields", ""),
				["043"] = new("Hyrule Castle Interior", 3, "Near the Fields", "4-3"),
				// World 5 – The Dark World
				["050"] = new("Lost Woods", 4, "The Dark World", "5-1"),
				["051"] = new("Kakariko Village", 4, "The Dark World", "5-2"),
				["052"] = new("Dark World Path", 4, "The Dark World", ""),
				["053"] = new("Temple of Darkness", 4, "The Dark World", "5-3"),
				// World 6 – The Desert of Doubt
				["060"] = new("Desert of Doubt", 5, "The Desert of Doubt", "6-1"),
				["061"] = new("Desert Temple", 5, "The Desert of Doubt", "6-2"),
				["062"] = new("Desert", 5, "The Desert of Doubt", ""),
				["063"] = new("Pyramid", 5, "The Desert of Doubt", "6-3"),
				// World 7 – Frozen Hyrule
				["070"] = new("Frozen Hyrule", 6, "Frozen Hyrule", "7-1"),
				["071"] = new("The Ice Temple", 6, "Frozen Hyrule", "7-2"),
				["072"] = new("Tower of Winds", 6, "Frozen Hyrule", ""),
				["073"] = new("Zelda", 6, "Frozen Hyrule", "7-3"),
				// World 8 – Realm of the Heavens
				["080"] = new("Realm of the Heavens", 7, "Realm of the Heavens", "8-1"),
				["081"] = new("The Dark Cloud", 7, "Realm of the Heavens", "8-2"),
				["082"] = new("The Heavens (connector)", 7, "Realm of the Heavens", ""),
				["083"] = new("Palace of Winds", 7, "Realm of the Heavens", "8-3"),
				// Boss stages — boss000-009 are the end-of-world boss arenas (boss003 does not exist in the ISO)
				// boss005 has ShowE3Banner=1 and only 7 rooms — cut/demo stage, not part of the final game
				["000"] = new("W1 Boss: Phantom Ganon", -1, "Boss Stages", ""),
				["001"] = new("W2 Boss: Stone Arrghus", -1, "Boss Stages", ""),
				["002"] = new("W3 Boss: Big Dodongo", -1, "Boss Stages", ""),
				["004"] = new("W4 Boss: Phantom Ganon II", -1, "Boss Stages", ""),
				["005"] = new("Boss Stage (Cut/Demo)", -1, "Boss Stages", ""),
				["006"] = new("W5 Boss: Phantom Ganon III", -1, "Boss Stages", ""),
				["007"] = new("W6 Boss: Big Moldorm", -1, "Boss Stages", ""),
				["008"] = new("W7 Boss: Frostare", -1, "Boss Stages", ""),
				["009"] = new("W8 Boss: Vaati & Ganon", -1, "Boss Stages", ""),
				["500"] = new("Shadow Battle", -1, "Shadow Battle", ""),
			};

		// ── Chest item IDs (TKRA VariableByte4) ──────────────────────────────
		// Source: $802eb78c analysis in memory notes

		public static readonly IReadOnlyDictionary<byte, string> ChestItems =
			new Dictionary<byte, string>
			{
				[0x00] = "1 Force Gem",
				[0x01] = "5 Force Gems",
				[0x02] = "20 Force Gems",
				[0x03] = "50 Force Gems",
				[0x04] = "100 Force Gems",
				[0x05] = "150 Force Gems",
				[0x06] = "200 Force Gems",
				[0x07] = "300 Force Gems",
				[0x08] = "1000 Force Gems",
				[0x09] = "1500 Force Gems",
				[0x0A] = "2000 Force Gems",
				[0x0B] = "3000 Force Gems",
				[0x10] = "Small Key",
				[0x11] = "Big Key",
				[0x12] = "Letter",
				[0x13] = "Power Bracelet",
				[0x14] = "Heart Container",
				[0x15] = "Blue Bracelet",
				[0x16] = "Moon Pearl",
				[0x17] = "Bombos Medallion",
				[0x18] = "Quake Medallion",
				[0x19] = "Spellbook",
				[0x20] = "Crystal Ball",
				[0x2A] = "Blue Royal Jewel",
				[0x2B] = "Green Royal Jewel",
				[0x2C] = "Red Royal Jewel",
				[0x2D] = "Purple Royal Jewel",
				[0x2E] = "Big Bomb",
				[0x2F] = "Nothing",
				[0x30] = "Pegasus Boots",
				[0x31] = "Lamp",
				[0x32] = "Boomerang",
				[0x33] = "Bow",
				[0x34] = "Magic Hammer",
				[0x35] = "Fire Rod",
				[0x36] = "Roc's Feather",
				[0x37] = "Bombs",
				[0x38] = "Shovel",
				[0x39] = "Slingshot",
				[0x3A] = "Sword (Unused)",
				[0x40] = "Lv2 Pegasus Boots",
				[0x41] = "Lv2 Lamp",
				[0x42] = "Lv2 Boomerang",
				[0x43] = "Lv2 Bow",
				[0x44] = "Lv2 Magic Hammer",
				[0x45] = "Lv2 Fire Rod",
				[0x46] = "Lv2 Roc's Feather",
				[0x47] = "Lv2 Bombs",
				[0x48] = "Lv2 Shovel",
				[0x49] = "Lv2 Slingshot",
				[0x4A] = "Lv2 Sword (Unused)",
			};
	}

	/// <param name="SectionLabel">In-game section number e.g. "1-1", "2-3". Empty for cut/connector levels.</param>
	public record LevelInfo(string Name, int WorldId, string WorldName, string SectionLabel = "");
}