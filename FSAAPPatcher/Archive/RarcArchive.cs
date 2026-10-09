using AuroraLib.Compression;
//using AuroraLib.Compression.Algorithms;
using AuroraLib.Compression.Formats.Nintendo;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace FSAAPPatcher.Archive
{
	/// <summary>
	/// Reads and writes GameCube RARC archives (optionally Yaz0-compressed).
	/// Format used by Four Swords Adventures boss*.arc level files.
	/// </summary>
	public class RarcArchive
	{
		public RarcDirectory Root { get; private set; } = new RarcDirectory("root");

		private static readonly Yaz0 s_yaz0 = new Yaz0
		{
			//LookAhead = false,
			FormatByteOrder = AuroraLib.Core.IO.Endian.Big
		};

		// ── Reading ──────────────────────────────────────────────────────────

		public static RarcArchive Load(string path)
		{
			using var fs = File.OpenRead(path);
			return Load(fs);
		}

		public static RarcArchive Load(Stream source)
		{
			// Peek magic
			Span<byte> magic = stackalloc byte[4];
			source.ReadExactly(magic);
			source.Position -= 4;

			Stream dataStream;
			if (magic[0] == 'Y' && magic[1] == 'a' && magic[2] == 'z' && magic[3] == '0')
			{
				var ms = new MemoryStream();
				s_yaz0.Decompress(source, ms);
				ms.Position = 0;
				dataStream = ms;
			}
			else
			{
				dataStream = source;
			}

			return ParseRarc(dataStream);
		}

		private static RarcArchive ParseRarc(Stream s)
		{
			Span<byte> buf4 = stackalloc byte[4];

			// ── Header at 0x00 ──
			s.ReadExactly(buf4); // "RARC"
			if (buf4[0] != 'R' || buf4[1] != 'A' || buf4[2] != 'R' || buf4[3] != 'C')
				throw new InvalidDataException("Not a RARC archive.");

			ReadU32(s); // file size
			ReadU32(s); // header size (0x20)
			uint dataOffset = ReadU32(s);   // relative to 0x20
			ReadU32(s); // data size
			ReadU32(s); // mram size
			ReadU32(s); // aram size
			ReadU32(s); // dvd size  — end of 0x20 header

			// ── Info block at 0x20 ──
			long infoBase = s.Position; // should be 0x20
			uint nodeCount = ReadU32(s);
			uint nodeOffset = ReadU32(s);      // from infoBase
			uint fileEntryCount = ReadU32(s);
			uint fileEntryOffset = ReadU32(s); // from infoBase
			uint stringTableSize = ReadU32(s);
			uint stringTableOffset = ReadU32(s); // from infoBase
			ReadU16(s); // next file id
			s.ReadByte(); // sync flag
			s.Position += 5; // padding

			// ── String table ──
			long stringTablePos = infoBase + stringTableOffset;
			byte[] stringTableBytes = new byte[stringTableSize];
			s.Position = stringTablePos;
			s.ReadExactly(stringTableBytes);

			string GetString(uint offset)
			{
				int end = Array.IndexOf(stringTableBytes, (byte)0, (int)offset);
				if (end < 0) end = stringTableBytes.Length;
				return Encoding.ASCII.GetString(stringTableBytes, (int)offset, end - (int)offset);
			}

			// ── Nodes ──
			// Actual JSystem RARC layout: type[4], unknown(u16), nameOffset(u16), nameHash(u16), fileCount(u16), firstFileIndex(u32)
			s.Position = infoBase + nodeOffset;
			var nodes = new NodeRaw[nodeCount];
			for (int i = 0; i < nodeCount; i++)
			{
				var type = new byte[4];
				s.ReadExactly(type);
				ReadU16(s);                      // +4  unknown (always 0)
				ushort nameOff = ReadU16(s);   // +6  nameOffset
				ReadU16(s);                      // +8  nameHash (unused)
				ushort fileCount = ReadU16(s);  // +10 fileCount
				uint firstFile = ReadU32(s);   // +12 firstFileIndex (u32!)
				nodes[i] = new NodeRaw
				{
					Type = Encoding.ASCII.GetString(type).TrimEnd('\0'),
					Name = GetString(nameOff),
					FirstFileIndex = firstFile,
					FileCount = fileCount
				};
			}

			// ── File entries ──
			s.Position = infoBase + fileEntryOffset;
			var entries = new FileEntryRaw[fileEntryCount];
			for (int i = 0; i < fileEntryCount; i++)
			{
				ushort id = ReadU16(s);
				ReadU16(s); // hash
				uint typeAndName = ReadU32(s);
				byte type = (byte)(typeAndName >> 24);
				uint nameOff = typeAndName & 0x00FFFFFF;
				uint dataOff = ReadU32(s);
				uint dataSize = ReadU32(s);
				ReadU32(s); // mem size
				entries[i] = new FileEntryRaw
				{
					Id = id,
					IsDir = (type & 0x02) != 0,
					Name = GetString(nameOff),
					DataOffset = dataOff,
					DataSize = dataSize
				};
			}

			// ── Data section ──
			long dataBase = infoBase + dataOffset;

			// ── Build directory tree ──
			var archive = new RarcArchive();
			archive.Root = BuildDirectory(nodes, entries, s, dataBase, nodeIndex: 0);
			return archive;
		}

		private static RarcDirectory BuildDirectory(
			NodeRaw[] nodes, FileEntryRaw[] entries,
			Stream s, long dataBase, int nodeIndex)
		{
			var node = nodes[nodeIndex];
			var dir = new RarcDirectory(node.Name);

			for (uint i = node.FirstFileIndex; i < node.FirstFileIndex + node.FileCount; i++)
			{
				if (i >= entries.Length) break;
				var e = entries[i];
				if (e.Name == "." || e.Name == "..") continue;

				if (e.IsDir)
				{
					// Find the node that this directory entry points to
					uint subdirNodeIdx = e.DataOffset;
					if (subdirNodeIdx < nodes.Length)
					{
						var subdir = BuildDirectory(nodes, entries, s, dataBase, (int)subdirNodeIdx);
						dir.Directories.Add(subdir);
					}
				}
				else
				{
					s.Position = dataBase + e.DataOffset;
					byte[] data = new byte[e.DataSize];
					s.ReadExactly(data);
					dir.Files.Add(new RarcFile(e.Name, data));
				}
			}

			return dir;
		}

		// ── Writing ──────────────────────────────────────────────────────────

		public void Save(string path, bool compress = true)
		{
			using var fs = File.Create(path);
			Save(fs, compress);
		}

		public void Save(Stream dest, bool compress = true)
		{
			using var ms = new MemoryStream();
			WriteRarc(ms);
			ms.Position = 0;

			if (compress)
				s_yaz0.Compress(ms, dest);
			else
				ms.CopyTo(dest);
		}

		private void WriteRarc(Stream dest)
		{
			// Collect all nodes/entries/strings
			var stringTable = new StringTable();
			stringTable.Add("."); stringTable.Add("..");

			var flatNodes = new List<NodeBuild>();
			var flatEntries = new List<EntryBuild>();
			CollectNodes(Root, flatNodes, flatEntries, stringTable, parentNodeIndex: -1);

			// Resolve node indices for directory entries
			foreach (var entry in flatEntries.Where(e => e.IsDir && e.SubdirNodeIndex >= 0))
			{
				// already set during CollectNodes
			}

			// Build data section
			var dataMs = new MemoryStream();
			foreach (var entry in flatEntries.Where(e => !e.IsDir))
			{
				entry.DataOffset = (uint)dataMs.Position;
				dataMs.Write(entry.FileData!);
				// Align to 32 bytes
				int pad = (int)(((dataMs.Position + 31) & ~31) - dataMs.Position);
				for (int i = 0; i < pad; i++) dataMs.WriteByte(0);
			}

			byte[] stringBytes = stringTable.ToBytes();
			byte[] dataBytes = dataMs.ToArray();

			// Offsets (all relative to infoBase = 0x20)
			uint nodeArrayOffset = 0x20; // nodes start right after info block
			uint nodeArraySize = (uint)(flatNodes.Count * 16);
			uint entryArrayOffset = nodeArrayOffset + nodeArraySize;
			uint entryArraySize = (uint)(flatEntries.Count * 20);
			// Align string table start to 32-byte boundary (infoBase itself is 32-aligned).
			uint entryArrayEnd = entryArrayOffset + entryArraySize;
			uint stringTableOff = (entryArrayEnd + 31u) & ~31u;
			uint stringTableSz = (uint)stringBytes.Length;
			uint stringTablePaddedSz = (stringTableSz + 31u) & ~31u;
			uint dataOff = stringTableOff + stringTablePaddedSz;

			uint totalFileSize = 0x20 + dataOff + (uint)dataBytes.Length;

			using var w = new BinaryWriter(dest, Encoding.ASCII, leaveOpen: true);

			// RARC main header (0x20 bytes)
			w.Write(Encoding.ASCII.GetBytes("RARC"));
			WriteU32(w, totalFileSize);
			WriteU32(w, 0x20);         // header size
			WriteU32(w, dataOff);      // data offset from 0x20
			WriteU32(w, (uint)dataBytes.Length);
			WriteU32(w, (uint)dataBytes.Length); // mram
			WriteU32(w, 0);            // aram
			WriteU32(w, 0);            // dvd

			// Info block (0x20 bytes)
			WriteU32(w, (uint)flatNodes.Count);
			WriteU32(w, nodeArrayOffset);
			WriteU32(w, (uint)flatEntries.Count);
			WriteU32(w, entryArrayOffset);
			WriteU32(w, stringTableSz);
			WriteU32(w, stringTableOff);
			WriteU16(w, (ushort)flatEntries.Count); // nextFileId = total entry count (JSystem assigns ID = entry index)
			w.Write((byte)1); // sync file ids
			w.Write(new byte[5]); // padding

			// Node array — layout: type[4], unknown(u16), nameOffset(u16), nameHash(u16), fileCount(u16), firstFileIndex(u32)
			foreach (var node in flatNodes)
			{
				byte[] typeBytes = new byte[4];
				var tb = Encoding.ASCII.GetBytes(node.Type.PadRight(4));
				Array.Copy(tb, typeBytes, Math.Min(4, tb.Length));
				w.Write(typeBytes);
				WriteU16(w, 0);                                              // +4  (always 0 in original)
				WriteU16(w, (ushort)stringTable.GetOffset(node.Name));  // +6  nameOffset
				WriteU16(w, CalcNameHash(node.Name));                    // +8  nameHash — JSystem uses this for fast lookup; 0 breaks findFile
				WriteU16(w, (ushort)node.EntryCount);                    // +10 fileCount
				WriteU32(w, (uint)node.FirstEntryIndex);                 // +12 firstFileIndex (u32)
			}

			// Entry array.
			// File ID = entry's flat-array index (same as JSystem's layout — 0xFFFF for dirs).
			// nextFileId in the info block = total entry count (not just file count).
			for (int i = 0; i < flatEntries.Count; i++)
			{
				var entry = flatEntries[i];
				// id: 0xFFFF for directories, entry-array index for files
				WriteU16(w, entry.IsDir ? (ushort)0xFFFF : (ushort)i);
				WriteU16(w, CalcNameHash(entry.Name)); // JSystem hash; game's findFile checks hash before strcmp
				uint nameOff = (uint)stringTable.GetOffset(entry.Name);
				uint typeFlags = entry.IsDir ? (uint)(0x02 << 24) : (uint)(0x11 << 24);
				WriteU32(w, typeFlags | (nameOff & 0x00FFFFFF));
				WriteU32(w, entry.IsDir ? (uint)entry.SubdirNodeIndex : entry.DataOffset);
				WriteU32(w, entry.IsDir ? 0x10u : (uint)entry.FileData!.Length); // dataSize
				WriteU32(w, 0u); // memSize always 0 (game uses dataSize for allocation)
			}

			// Padding between entry array and string table (32-byte alignment of string table start)
			int entryToStrPad = (int)(stringTableOff - entryArrayEnd);
			if (entryToStrPad > 0) w.Write(new byte[entryToStrPad]);

			// String table
			w.Write(stringBytes);
			int strPad = (int)(stringTablePaddedSz - stringTableSz);
			w.Write(new byte[strPad]);

			// Data section
			w.Write(dataBytes);
		}

		private static int CollectNodes(
			RarcDirectory dir,
			List<NodeBuild> nodes,
			List<EntryBuild> entries,
			StringTable strings,
			int parentNodeIndex)
		{
			int thisNodeIndex = nodes.Count;
			// Node type: root uses "ROOT"; others use first 4 chars of dir name uppercased (e.g. "j3d"→"J3D ").
			// JSystem stores these type tags in the RARC and may use them for node lookup.
			string type = thisNodeIndex == 0 ? "ROOT"
				: dir.Name.ToUpperInvariant().PadRight(4)[..4];
			var node = new NodeBuild { Type = type, Name = dir.Name };
			nodes.Add(node);
			strings.Add(dir.Name);
			strings.Add(".");
			strings.Add("..");

			int firstEntry = entries.Count;

			// JSystem RARC entry ordering: subdirs first, then files, then '.' and '..' last.
			// '.' and '..' must be at the end — JSystem's node scan expects this layout.

			// Subdir placeholders (filled in after recursion)
			var subdirEntryIndices = new List<(int entryIdx, RarcDirectory subdir)>();
			foreach (var subdir in dir.Directories)
			{
				int idx = entries.Count;
				entries.Add(new EntryBuild { IsDir = true, Name = subdir.Name, SubdirNodeIndex = -1 });
				strings.Add(subdir.Name);
				subdirEntryIndices.Add((idx, subdir));
			}

			// Files
			foreach (var file in dir.Files)
			{
				entries.Add(new EntryBuild { IsDir = false, Name = file.Name, FileData = file.Data });
				strings.Add(file.Name);
			}

			// '.' (self) and '..' (parent) always at end.
			// Root's '..' uses SubdirNodeIndex=-1 → written as 0xFFFFFFFF (JSystem's "no parent" sentinel).
			entries.Add(new EntryBuild { IsDir = true, Name = ".", SubdirNodeIndex = thisNodeIndex });
			entries.Add(new EntryBuild { IsDir = true, Name = "..", SubdirNodeIndex = parentNodeIndex >= 0 ? parentNodeIndex : -1 });

			node.FirstEntryIndex = firstEntry;
			node.EntryCount = entries.Count - firstEntry;

			// Recurse into subdirectories and fill in their node indices
			foreach (var (entryIdx, subdir) in subdirEntryIndices)
			{
				int subdirNodeIdx = CollectNodes(subdir, nodes, entries, strings, thisNodeIndex);
				entries[entryIdx].SubdirNodeIndex = subdirNodeIdx;
			}

			return thisNodeIndex;
		}

		// ── Extract / Pack helpers ────────────────────────────────────────────

		public void ExtractTo(string outputDirectory)
		{
			Directory.CreateDirectory(outputDirectory);
			ExtractDirectory(Root, outputDirectory);
		}

		private static void ExtractDirectory(RarcDirectory dir, string path)
		{
			foreach (var subdir in dir.Directories)
			{
				string subdirPath = Path.Combine(path, subdir.Name);
				Directory.CreateDirectory(subdirPath);
				ExtractDirectory(subdir, subdirPath);
			}
			foreach (var file in dir.Files)
			{
				File.WriteAllBytes(Path.Combine(path, file.Name), file.Data);
			}
		}

		public static RarcArchive FromDirectory(string directory)
		{
			var archive = new RarcArchive();
			archive.Root = LoadDirectory(new DirectoryInfo(directory));
			return archive;
		}

		private static RarcDirectory LoadDirectory(DirectoryInfo di)
		{
			var dir = new RarcDirectory(di.Name);
			foreach (var sub in di.GetDirectories().OrderBy(d => d.Name))
				dir.Directories.Add(LoadDirectory(sub));
			foreach (var fi in di.GetFiles().OrderBy(f => f.Name))
				dir.Files.Add(new RarcFile(fi.Name, File.ReadAllBytes(fi.FullName)));
			return dir;
		}

		// ── Binary helpers ────────────────────────────────────────────────────

		// JSystem RARC name hash: hash = hash * 3 + char, for each ASCII byte.
		// Required so the game's JKRArchive::findFile can match entries via hash table lookup.
		private static ushort CalcNameHash(string name)
		{
			uint hash = 0;
			foreach (char c in name)
				hash = hash * 3 + (byte)c;
			return (ushort)hash;
		}

		private static uint ReadU32(Stream s)
		{
			Span<byte> b = stackalloc byte[4];
			s.ReadExactly(b);
			return BinaryPrimitives.ReadUInt32BigEndian(b);
		}

		private static ushort ReadU16(Stream s)
		{
			Span<byte> b = stackalloc byte[2];
			s.ReadExactly(b);
			return BinaryPrimitives.ReadUInt16BigEndian(b);
		}

		private static void WriteU32(BinaryWriter w, uint v)
		{
			Span<byte> b = stackalloc byte[4];
			BinaryPrimitives.WriteUInt32BigEndian(b, v);
			w.Write(b);
		}

		private static void WriteU16(BinaryWriter w, ushort v)
		{
			Span<byte> b = stackalloc byte[2];
			BinaryPrimitives.WriteUInt16BigEndian(b, v);
			w.Write(b);
		}

		// ── Private raw structs ───────────────────────────────────────────────

		private class NodeRaw
		{
			public string Type = "";
			public string Name = "";
			public uint FirstFileIndex;
			public ushort FileCount;
		}

		private class FileEntryRaw
		{
			public ushort Id;
			public bool IsDir;
			public string Name = "";
			public uint DataOffset;
			public uint DataSize;
		}

		private class NodeBuild
		{
			public string Type = "DIR ";
			public string Name = "";
			public int FirstEntryIndex;
			public int EntryCount;
		}

		private class EntryBuild
		{
			public bool IsDir;
			public string Name = "";
			public int SubdirNodeIndex = -1;  // for dirs
			public uint DataOffset;            // for files
			public byte[]? FileData;           // for files
		}

		private class StringTable
		{
			private readonly List<(string s, int offset)> _entries = new();
			private int _offset = 0;

			public void Add(string s)
			{
				if (!_entries.Any(e => e.s == s))
				{
					_entries.Add((s, _offset));
					_offset += Encoding.ASCII.GetByteCount(s) + 1;
				}
			}

			public int GetOffset(string s)
			{
				var e = _entries.FirstOrDefault(x => x.s == s);
				return e == default ? 0 : e.offset;
			}

			public byte[] ToBytes()
			{
				var ms = new MemoryStream();
				foreach (var (s, _) in _entries)
				{
					ms.Write(Encoding.ASCII.GetBytes(s));
					ms.WriteByte(0);
				}
				return ms.ToArray();
			}
		}
	}

	public class RarcDirectory
	{
		public string Name { get; set; }
		public List<RarcDirectory> Directories { get; } = new();
		public List<RarcFile> Files { get; } = new();

		public RarcDirectory(string name) => Name = name;

		public RarcFile? FindFile(string name)
		{
			foreach (var f in Files)
				if (f.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return f;
			foreach (var d in Directories)
			{
				var result = d.FindFile(name);
				if (result != null) return result;
			}
			return null;
		}

		public RarcDirectory? FindDirectory(string name)
		{
			if (Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return this;
			foreach (var d in Directories)
			{
				var result = d.FindDirectory(name);
				if (result != null) return result;
			}
			return null;
		}

		public IEnumerable<RarcFile> AllFiles()
		{
			foreach (var f in Files) yield return f;
			foreach (var d in Directories)
				foreach (var f in d.AllFiles()) yield return f;
		}
	}

	public class RarcFile
	{
		public string Name { get; set; }
		public byte[] Data { get; set; }

		public RarcFile(string name, byte[] data)
		{
			Name = name;
			Data = data;
		}
	}
}