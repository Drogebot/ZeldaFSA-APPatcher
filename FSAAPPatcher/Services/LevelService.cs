using AuroraLib.Core.IO;
using FSALib;
using FSALib.Structs;
using FSAAPPatcher.Archive;
using FSAAPPatcher.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace FSAAPPatcher.Services
{
	/// <summary>
	/// Discovers and loads FSA level data from:
	///   - A folder of extracted level directories (boss010/, boss011/, …)
	///   - A Boss/ folder containing boss*.arc archives
	/// </summary>
	public class LevelService
	{
		// ── Open ─────────────────────────────────────────────────────────────

		/// <summary>
		/// Detect what kind of folder the user opened and return LoadedGame.
		/// </summary>
		public LoadedGame OpenDirectory(string path, IProgress<string>? progress = null)
		{
			if (!Directory.Exists(path))
				throw new DirectoryNotFoundException(path);

			var game = new LoadedGame(path);

			// Check for .arc files → Boss/ directory
			var arcFiles = Directory.GetFiles(path, "boss*.arc");
			if (arcFiles.Length > 0)
			{
				progress?.Report("Found .arc files – extracting to temp directory…");
				string tempDir = Path.Combine(Path.GetTempPath(), "FSARandomizerAP", "extracted");
				Directory.CreateDirectory(tempDir);
				game.TempExtractDirectory = tempDir;

				foreach (var arc in arcFiles.OrderBy(f => f))
				{
					string id = GetLevelId(Path.GetFileNameWithoutExtension(arc));
					if (id == "") continue;
					string outDir = Path.Combine(tempDir, $"boss{id}");

					// Always wipe and re-extract so stale temp dirs never block fresh data
					if (Directory.Exists(outDir))
						Directory.Delete(outDir, recursive: true);

					progress?.Report($"Extracting boss{id}.arc…");
					try
					{
						var archive = RarcArchive.Load(arc);
						archive.ExtractTo(outDir);
					}
					catch (Exception ex)
					{
						progress?.Report($"  Warning: boss{id}.arc – {ex.Message}");
						continue;
					}

					var level = LoadExtractedLevel(outDir, id, arc);
					if (level != null) game.Levels.Add(level);
					else progress?.Report($"  Warning: boss{id} extracted but map{id}.csv not found in map/ – skipped.");
				}
			}
			else
			{
				// Assume pre-extracted directories
				foreach (var dir in Directory.GetDirectories(path).OrderBy(d => d))
				{
					string dirName = Path.GetFileName(dir);
					// Accept directories named "boss010", "(Level 1-1) Lake Hylia", etc.
					string id = ExtractIdFromDirName(dirName);
					if (id == "") continue;
					progress?.Report($"Loading boss{id}…");
					var level = LoadExtractedLevel(dir, id, null);
					if (level != null) game.Levels.Add(level);
				}
			}

			return game;
		}

		// ── Load single extracted level ───────────────────────────────────────

		public LoadedLevel? LoadExtractedLevel(string dir, string id, string? arcPath)
		{
			string mapDir = Path.Combine(dir, "map");
			if (!Directory.Exists(mapDir)) return null;

			if (!int.TryParse(id, out int mapIndex)) return null;

			// Try singleplayer map first, fall back to multiplayer
			string mapFile = MapLayout.GetFilePath(dir, mapIndex, singleplayer: true);
			if (!File.Exists(mapFile))
				mapFile = MapLayout.GetFilePath(dir, mapIndex, singleplayer: false);
			if (!File.Exists(mapFile)) return null;

			MapLayout map;
			using (var fs = File.OpenRead(mapFile))
				map = new MapLayout(fs);

			var info = GameData.Levels.TryGetValue(id, out var li) ? li
					   : new LevelInfo($"Level {id}", -1, "Unknown");

			var level = new LoadedLevel
			{
				Id = id,
				MapIndex = mapIndex,
				Name = info.Name,
				WorldName = info.WorldName,
				WorldId = info.WorldId,
				SectionLabel = info.SectionLabel,
				BaseDirectory = dir,
				SourceArcPath = arcPath,
				Map = map,
			};

			// Load all rooms that exist on the map
			for (int y = 0; y < map.YDimension; y++)
			{
				for (int x = 0; x < map.XDimension; x++)
				{
					int roomIdx = map[x, y];
					if (roomIdx == MapLayout.EMPTY_ROOM_VALUE) continue;
					if (level.Rooms.Any(r => r.RoomIndex == roomIdx)) continue;

					string binPath = ActorList.GetFilePath(dir, level.MapIndex, roomIdx);
					if (!File.Exists(binPath)) continue;

					var actors = new ActorList();
					using (var fs = File.OpenRead(binPath))
						actors.ReadFromStream(fs);

					level.Rooms.Add(new LoadedRoom
					{
						RoomIndex = roomIdx,
						MapX = x,
						MapY = y,
						Actors = actors,
						ActorFilePath = binPath,
					});
				}
			}

			return level;
		}

		// ── Open ISO ─────────────────────────────────────────────────────────

		/// <summary>
		/// Load levels directly from a GameCube disc image (.iso / .gcm).
		/// The Boss/ folder inside the disc is extracted to a temp directory.
		/// </summary>
		public LoadedGame OpenIso(string isoPath, IProgress<string>? progress = null, bool assumeExtracted = false)
		{
			var game = new LoadedGame(isoPath);

			progress?.Report("Opening GameCube disc image…");
			using var gcm = GcmReader.Open(isoPath);

			// Boss arc files live at GC4Sword_usa/Boss/boss*.arc
			const string bossDir = "GC4Sword_usa/Boss";
			var arcEntries = gcm.ListFiles(bossDir)
				.Where(e => e.Name.StartsWith("boss", StringComparison.OrdinalIgnoreCase)
						 && e.Name.EndsWith(".arc", StringComparison.OrdinalIgnoreCase))
				.OrderBy(e => e.Name)
				.ToList();

			if (arcEntries.Count == 0)
				throw new InvalidDataException($"No boss*.arc files found at '{bossDir}' inside the disc image.");

			progress?.Report($"Found {arcEntries.Count} boss*.arc files in disc image.");

			string tempDir = Path.Combine(Path.GetTempPath(), "FSARandomizerAP", "iso_extracted",
										  Path.GetFileNameWithoutExtension(isoPath));
			game.TempExtractDirectory = tempDir;

			//Directory.CreateDirectory(Path.Combine(tempDir, "sys"));
			//var dolfile = File.Create(Path.Combine(tempDir, "sys", "main.dol"));
			//gcm.OpenDol().CopyTo(dolfile);
			//dolfile.Flush();
			//dolfile.Dispose();
			//
			//var dol = gcm.OpenDol();
			//var textOffsets = new uint[7];
			//var dataOffsets = new uint[11];
			//for (int i = 0; i < textOffsets.Length; i++)
			//	textOffsets[i] = dol.ReadUInt32BigEndian();
			//for (int i = 0; i < dataOffsets.Length; i++)
			//	dataOffsets[i] = dol.ReadUInt32BigEndian();
			//dol.Seek(0x90, SeekOrigin.Begin);
			//var textSizes = new uint[7];
			//var dataSizes = new uint[11];
			//for (int i = 0; i < textSizes.Length; i++)
			//	textSizes[i] = dol.ReadUInt32BigEndian();
			//for (int i = 0; i < dataSizes.Length; i++)
			//	dataSizes[i] = dol.ReadUInt32BigEndian();
			//
			//for (int i = 0; i < textOffsets.Length; i++)
			//{
			//	progress?.Report($"Text segment {i}: offset=0x{textOffsets[i]:X8}, size=0x{textSizes[i]:X8}");
			//}
			//
			//for (int i = 0; i < dataOffsets.Length; i++)
			//{
			//	progress?.Report($"Data segment {i}: offset=0x{dataOffsets[i]:X8}, size=0x{dataSizes[i]:X8}");
			//}

			foreach (var entry in arcEntries)
			{
				string id = GetLevelId(Path.GetFileNameWithoutExtension(entry.Name));
				if (id == "") continue;

				string outDir = Path.Combine(tempDir, $"boss{id}");

				if (!assumeExtracted)
				{
					if (Directory.Exists(outDir))
						Directory.Delete(outDir, recursive: true);

					progress?.Report($"Extracting boss{id}.arc from ISO…");
					try
					{
						using var arcStream = gcm.OpenFile(entry);
						var archive = RarcArchive.Load(arcStream);
						archive.ExtractTo(outDir);
					}
					catch (Exception ex)
					{
						progress?.Report($"  Warning: boss{id}.arc – {ex.Message}");
						continue;
					}
				}

				var level = LoadExtractedLevel(outDir, id, arcPath: entry.FullPath);
				if (level != null) game.Levels.Add(level);
				else progress?.Report($"  Warning: boss{id} extracted but map{id}.csv not found – skipped.");
			}

			return game;
		}

		// ── Save ─────────────────────────────────────────────────────────────

		/// <summary>Save modified actor lists back to the extracted level directory.</summary>
		public void SaveRoom(LoadedRoom room)
		{
			Directory.CreateDirectory(Path.GetDirectoryName(room.ActorFilePath)!);
			using var fs = File.Create(room.ActorFilePath);
			room.Actors.WriteToStream(fs);
		}

		// ── Export ISO ──────────────────────────────────────────────────────

		/// <summary>
		/// Build a new .iso with all modified levels repacked as .arc and injected back.
		/// Only valid when the game was originally opened from an .iso.
		/// </summary>
		/// <param name="stagePerm">
		/// Optional 32-element stage permutation (8 worlds × 4 stages): stagePerm[i] = which
		/// original stage-index's content goes in target slot i. Both world-shuffle and
		/// stage-shuffle use this format. File content is physically swapped and internal RARC
		/// entries are renamed so the game's path lookups remain consistent.
		/// </param>
		public void ExportIso(LoadedGame game, string outputIsoPath,
							  IReadOnlyList<(uint Offset, uint Value)> patches,
							  int[]? stagePerm = null,
							  IProgress<string>? progress = null)
		{
			// Determine source ISO path
			string sourcePath = game.SourceDirectory;
			if (!File.Exists(sourcePath))
				throw new InvalidOperationException(
					"Cannot export ISO: the game was not opened from a disc image.");

			// Never overwrite the source
			if (string.Equals(Path.GetFullPath(outputIsoPath), Path.GetFullPath(sourcePath),
							  StringComparison.OrdinalIgnoreCase))
				throw new InvalidOperationException(
					"Output path is the same as the source ISO. Choose a different location.");

			// Repack all modified levels into temp .arc bytes
			var replacements = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
			const string bossDir = "GC4Sword_usa/Boss";

			foreach (var level in game.Levels.Where(l => l.BaseDirectory.Length > 0))
			{
				var archive = RarcArchive.FromDirectory(level.BaseDirectory);
				using var ms = new MemoryStream();
				archive.Save(ms, compress: true);
				replacements[$"{bossDir}/boss{level.Id}.arc"] = ms.ToArray();
			}

			progress?.Report($"Injecting {replacements.Count} modified level(s) into new ISO…");
			GcmWriter.ReplaceFiles(sourcePath, outputIsoPath, patches, replacements, progress);
		}

		// ── RARC rename helpers ───────────────────────────────────────────────

		private static void RenameDirectory(RarcDirectory dir, string from, string to)
		{
			if (dir.Name.Contains(from, StringComparison.Ordinal))
				dir.Name = dir.Name.Replace(from, to, StringComparison.Ordinal);
			foreach (var subdir in dir.Directories)
				RenameDirectory(subdir, from, to);
			foreach (var file in dir.Files)
				if (file.Name.Contains(from, StringComparison.Ordinal))
					file.Name = file.Name.Replace(from, to, StringComparison.Ordinal);
		}

		// ── Helpers ───────────────────────────────────────────────────────────

		private static string GetLevelId(string filename)
		{
			// "boss010" → "010"
			if (filename.StartsWith("boss", StringComparison.OrdinalIgnoreCase))
				return filename.Substring(4);
			return "";
		}

		private static string ExtractIdFromDirName(string name)
		{
			// "boss010" → "010"
			if (name.StartsWith("boss", StringComparison.OrdinalIgnoreCase) && name.Length >= 7)
				return name.Substring(4, 3);

			// "(Level 1-1) Lake Hylia" – match by known level names
			foreach (var (id, info) in GameData.Levels)
				if (name.Contains(info.Name, StringComparison.OrdinalIgnoreCase))
					return id;

			return "";
		}
	}

	// ── Data containers ───────────────────────────────────────────────────────

	public class LoadedGame
	{
		public string SourceDirectory { get; }
		public string? TempExtractDirectory { get; set; }
		public List<LoadedLevel> Levels { get; } = new();
		public bool IsModified => Levels.Any(l => l.IsModified);

		public LoadedGame(string sourceDir) => SourceDirectory = sourceDir;
	}

	public class LoadedLevel
	{
		public string Id { get; set; } = "";
		public int MapIndex { get; set; }
		public string Name { get; set; } = "";
		public string WorldName { get; set; } = "";
		public int WorldId { get; set; }
		public string SectionLabel { get; set; } = "";
		public string BaseDirectory { get; set; } = "";
		public string? SourceArcPath { get; set; }
		public MapLayout Map { get; set; } = new MapLayout();
		public List<LoadedRoom> Rooms { get; } = new();
		public bool IsModified => Rooms.Any(r => r.IsDirty);
	}

	public class LoadedRoom
	{
		public int RoomIndex { get; set; }
		public int MapX { get; set; }
		public int MapY { get; set; }
		public ActorList Actors { get; set; } = new ActorList();
		public string ActorFilePath { get; set; } = "";
		public bool IsDirty { get; set; }
	}
}