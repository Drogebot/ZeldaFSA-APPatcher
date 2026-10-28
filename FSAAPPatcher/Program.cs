using FSAAPPatcher.Archive;
using FSAAPPatcher.Models;
using FSAAPPatcher.Services;

namespace FSAAPPatcher
{
	internal class Program
	{
		static void Main(string[] args)
		{
			//var values = new int[] { 1, 5, 20, 50, 100, 150, 200, 300, 1000, 1500, 2000, 3000 };

			var progress = new Progress<string>(message => Console.WriteLine(message));

			LevelService levelService = new LevelService();
			Console.WriteLine("Enter path to/Drag the ISO file here:");
			var iso = Console.ReadLine();
			iso = iso?.Trim('"', '\'');
			var game = levelService.OpenIso(iso, progress, false);

			var levels = game.Levels.Where(l => l.Id[0] == '0' && l.Id[1] != '0' && l.Id[1] != '9' && (l.Id[0] < '8' && l.Id[2] != '2' || l.Id[0] == '8' && l.Id[2] != '2'));

			//foreach (var level in levels)
			//{
			//	Console.WriteLine($"Level: {level.Name} ({level.Id}), Rooms: {level.Rooms.Count}, Chests: {level.Rooms.Sum(r => r.Actors.Where(a => a.Name == "TKRA").Count())}, Keys: {level.Rooms.Sum(r => r.Actors.Where(a => a.Name == "KEY0" || a.Name == "TKRA" && (a.VariableByte1 == 16 || a.VariableByte1 == 22)).Count())}");
			//}
			//var totalChests = levels.Sum(l => l.Rooms.Sum(r => r.Actors.Where(a => a.Name == "TKRA").Count()));
			//var totalGems = levels.Sum(l => l.Rooms.Sum(r => r.Actors.Where(a => a.Name == "RUPY").Count()));
			//var totalGemValue = levels.Sum(l => l.Rooms.Sum(r => r.Actors.Where(a => a.Name == "TKRA" && a.VariableByte1 <= 11).Select(r => values[r.VariableByte1]).Sum()));
			//var test = levels.SelectMany(l => l.Rooms.SelectMany(r => r.Actors.Where(a => a.Name == "TKRA"))).CountBy(a => GameData.ChestItems[a.VariableByte1]).ToDictionary();
			//Console.WriteLine($"Total Chests: {totalChests}, Total Gems: {totalGems}, Total Gem Value: {totalGemValue}");


			foreach (var level in levels)
			{
				foreach (var room in level.Rooms)
				{
					for (int i = 0; i < room.Actors.Count; i++)
					{
						var a = room.Actors[i];
						// Remove keys, change chest contents, remove characters that give key-items, fix RNG chests access
						if(a.Name == "KEY0")
						{
							a.XCoord = 31;
							a.YCoord = 63;
						}
						else if (a.Name == "TKRA")
						{
							a.VariableByte1 = 0;
						}
						else if (a.Name == "SWEL" && (a.VariableByte1 == 5 || a.VariableByte1 == 6 || a.VariableByte1 == 7))
						{
							a.VariableByte1 = 4;
						}
						else if (a.Name == "SNBL" && a.VariableByte1 == 1)
						{
							a.VariableByte1 = 0;
						}

						if (level.Id == "021")
						{
							if (room.RoomIndex == 0 && a.Name == "SNPC" && a.Layer == 0)
							{
								a.XCoord = 31;
								a.YCoord = 63;
							}
							else if (room.RoomIndex == 3 && a.Name == "JIJI" && a.Layer == 3)
							{
								a.XCoord = 31;
								a.YCoord = 63;
							}
							else if (room.RoomIndex == 4 && a.Name == "JIJI" && a.Layer == 2)
							{
								a.XCoord = 31;
								a.YCoord = 63;
							}
							else if (room.RoomIndex == 6 && a.Name == "SNPC" && a.Layer == 4)
							{
								a.XCoord = 31;
								a.YCoord = 63;
							}
						}
						else if (level.Id == "040")
						{
							if (room.RoomIndex == 2 && a.Name == "JIJI" && a.Layer == 2)
							{
								a.XCoord = 31;
								a.YCoord = 63;
							}
						}
						else if (level.Id == "050")
						{
							if (room.RoomIndex == 23 && a.Name == "JIJI" && a.Layer == 1)
							{
								a.XCoord = 31;
								a.YCoord = 63;
							}
						}
						else if (level.Id == "051")
						{
							if (room.RoomIndex == 7 && a.Name == "JIJI" && a.Layer == 2)
							{
								a.XCoord = 31;
								a.YCoord = 63;
							}
						}
						else if (level.Id == "083")
						{
							if (room.RoomIndex == 2 && (a.Name == "PNPC" || a.Name == "TKRA") && a.Layer == 0 && a.YCoord == 6) // move top row of chests one back
							{
								a.YCoord -= 2;
							}
						}

						// QoL: Make certain doors open after defeating bosses to allow backtracking to previous rooms in certain levels
						if (level.Id == "033")
						{
							if (room.RoomIndex == 6 && a.Name == "DOOR" && a.Layer == 0)    // reopen door after the ball and chain fight
							{
								a.VariableByte4 = 0xA0;
							}
							else if (room.RoomIndex == 10 && a.Name == "DOOR" && a.Layer == 0)	// reopen door after the shadow fight
							{
								a.VariableByte4 = 0xA0;
							}
						}
						else if (level.Id == "043")
						{
							if (room.RoomIndex == 13 && a.Name == "DOOR" && a.Layer == 0)  // allow reopening door
							{
								a.VariableByte4 = 0xA0;
							}
						}
						else if (level.Id == "053")
						{
							if (room.RoomIndex == 2 && a.Name == "PONT" && a.Layer == 0 && a.YCoord == 20)	// reopen door instantly
							{
								a.XCoord = 30;
								a.YCoord = 44;
							}
						}

						room.Actors[i] = a;
					}

					// QoL:Add one way warps to allow backtracking to previous rooms in certain levels
					if (level.Id == "040" && room.RoomIndex == 3)
					{
						room.Actors.Add(new FSALib.Structs.Actor
						{
							Name = "WARP",
							Layer = 0,
							XCoord = 46,
							YCoord = 20,
							Variable = 3018268,
						});
						room.Actors.Add(new FSALib.Structs.Actor
						{
							Name = "YAJI",
							Layer = 0,
							XCoord = 49,
							YCoord = 21,
							Variable = 1,
						});
					}
					else if (level.Id == "081" && room.RoomIndex == 10)
					{
						room.Actors.Add(new FSALib.Structs.Actor
						{
							Name = "WARP",
							Layer = 0,
							XCoord = 2,
							YCoord = 38,
							Variable = 3933698,
						});
						room.Actors.Add(new FSALib.Structs.Actor
						{
							Name = "YAJI",
							Layer = 0,
							XCoord = 3,
							YCoord = 39,
							Variable = 3,
						});
					}

					levelService.SaveRoom(room);
				}

				//levelService.RepackLevel(level);
			}

			var patches = new List<(uint, uint)>
			{
				// disable key item changes
				(0x80241618, 0x60000000),	// stw r4, 0xc10(r3) -> ori r0, r0, 0x0
				// disable placing key items on pedestals
				(0x802ac10c, 0x4280013c),	// bgt 0x0x802ac25c -> b 0x802ac248
				(0x802ac24c, 0x38e00000),	// li r7, 0xfff -> li r7, 0x0
				(0x802ac254, 0x39000000),	// li r8, 0x100f -> li r8, 0x0
				//// stop changing gem total
				//(0x8024edac, 0x60000000),	// stw r0, 0xc04(r3) -> ori r0, r0, 0x0
				//(0x8024ede0, 0x60000000),	// stw r0, 0xc04(r3) -> ori r0, r0, 0x0
				//// stop gems from despawning
				//(0x8032c8f8, 0x60000000),	// stw r0, 0x24c(r30) -> ori r0, r0, 0x0
				//// disable default z button behaviour
				//(0x80288a60, 0x42800038),	// bne 0x80288a98 -> b 0x80288a98
				// disable d-pad button behaviour
				(0x8030a8d0, 0x42800078),	// rlwinm. r0, r3, 0x0, 0x1f, 0x1f -> b 0x8030a948
				// skip the intro cutscene
				(0x803ec450, 0x60000000),	// beq 0x803ec470 -> ori r0, r0, 0x0
				// death restores healthMax because if you die with <=4 healthMax, you will be stuck in a death loop
				(0x803d9de4, 0x3bc30000),	// subi r30, r3, 0x4 -> addi r30, r3, 0x0
				(0x803d9e14, 0x38840000),	// subi r4, r4, 0x4 -> addi r4, r4, 0x0
				// unlock all levels
				(0x80404da0, 0x60000000),	// beq 0x804050e8 -> ori r0, r0, 0x0
				// default level 0
				(0x8040f5b8, 0x60000000),	// bge 0x8040f5cc -> ori r0, r0, 0x0
				(0x8040f5c0, 0x60000000),	// blt 0x8040f5cc -> ori r0, r0, 0x0
				// enable level select
				(0x80413f08, 0x901f038c),	// stw r7, 0x38c(r31) -> stw r0, 0x38c(r31)
			};

			;
			var outputIso = Path.Join(Path.GetDirectoryName(iso), $"{Path.GetFileNameWithoutExtension(iso)}_modified.iso");
			levelService.ExportIso(game, outputIso, patches, progress: progress);

			((IProgress<string>)progress).Report($"Press any key to exit...");
		}
	}
}
