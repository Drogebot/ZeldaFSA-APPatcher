using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace FSAAPPatcher.Archive
{
	/// <summary>
	/// Produces a modified GameCube disc image (.iso/.gcm) by replacing specific files
	/// and rebuilding the FST from scratch.
	///
	/// Algorithm (matches GCFT / gclib approach):
	///  1. Copy system header (0x0000–0x2440) verbatim.
	///  2. Copy apploader at 0x2440 verbatim.
	///  3. Copy main.dol verbatim at its original offset.
	///  4. Rebuild FST: assign new file data offsets in original-order, injecting
	///     replaced files in-place.
	///  5. Write file data section, then write rebuilt FST + string table.
	/// </summary>
	public static class GcmWriter
	{
		private const int Align = 4; // file data alignment inside GCM

		/// <summary>
		/// Build a new disc image at <paramref name="outputPath"/> identical to
		/// <paramref name="sourcePath"/> except the files in <paramref name="replacements"/>
		/// are substituted (paths use forward slashes, case-insensitive).
		/// </summary>
		public static void ReplaceFiles(
			string sourcePath,
			string outputPath,
			IReadOnlyList<(uint Offset, uint Value)> patches,
			IReadOnlyDictionary<string, byte[]> replacements,
			IProgress<string>? progress = null)
		{
			using var src = File.OpenRead(sourcePath);
			using var dst = File.Create(outputPath);
			ReplaceFiles(src, dst, patches, replacements, progress);
		}

		public static void ReplaceFiles(
			Stream source,
			Stream dest,
			IReadOnlyList<(uint Offset, uint Value)> patches,
			IReadOnlyDictionary<string, byte[]> replacements,
			IProgress<string>? progress = null)
		{
			Span<byte> buf4 = stackalloc byte[4];

			// ── Read header metadata ──────────────────────────────────────────
			source.Position = 0x001C;
			source.ReadExactly(buf4);
			if (BinaryPrimitives.ReadUInt32BigEndian(buf4) != 0xC2339F3D)
				throw new InvalidDataException("Not a valid GameCube disc image.");

			// GCM header: 0x0420=DOL offset, 0x0424=FST offset, 0x0428=FST size
			source.Position = 0x0420;
			source.ReadExactly(buf4); uint dolOffset = BinaryPrimitives.ReadUInt32BigEndian(buf4);
			source.ReadExactly(buf4); uint fstOffset = BinaryPrimitives.ReadUInt32BigEndian(buf4);
			source.ReadExactly(buf4); uint fstSize = BinaryPrimitives.ReadUInt32BigEndian(buf4);

			// ── Parse FST ────────────────────────────────────────────────────
			byte[] fstBytes = new byte[fstSize];
			source.Position = fstOffset;
			source.ReadExactly(fstBytes);

			uint totalEntries = BinaryPrimitives.ReadUInt32BigEndian(fstBytes.AsSpan(8, 4));
			uint nameTableStart = totalEntries * 12;

			var entries = ParseFst(fstBytes, totalEntries, nameTableStart);

			// Build full paths (same as GcmReader)
			AssignFullPaths(entries);

			// ── Compute new file offsets ──────────────────────────────────────
			// New data section starts after the FST (we'll write FST at dolOffset + dolSize, aligned).
			// Strategy: keep the same ordering as the original disc (sort by original offset),
			// then assign new offsets sequentially with 4-byte alignment.
			// We don't move the DOL or FST position relative to each other.

			// Copy source bytes before data section verbatim into dest.
			// Find where file data begins: minimum fileOffset among all file entries.
			uint dataStart = uint.MaxValue;
			foreach (var e in entries)
				if (!e.IsDirectory && e.FileOffset > 0 && e.FileOffset < dataStart)
					dataStart = e.FileOffset;

			if (dataStart == uint.MaxValue) dataStart = fstOffset + fstSize;
			dataStart = AlignUp(dataStart, 0x100); // align to 256

			// Sort file entries by original offset to maintain load order
			var fileEntries = entries
				.Where(e => !e.IsDirectory && e.Name != "")
				.OrderBy(e => e.FileOffset)
				.ToList();

			// Assign new offsets
			uint cursor = dataStart;
			var newOffsets = new Dictionary<int, uint>();
			var newSizes = new Dictionary<int, uint>();

			foreach (var e in fileEntries)
			{
				string lowerPath = e.FullPath.ToLowerInvariant();
				var matched = replacements.Keys.FirstOrDefault(
					k => k.Equals(e.FullPath, StringComparison.OrdinalIgnoreCase));
				uint size = matched != null ? (uint)replacements[matched].Length : e.FileSize;

				newOffsets[e.Index] = cursor;
				newSizes[e.Index] = size;
				cursor = AlignUp(cursor + size, Align);
			}

			// ── Build new FST bytes ───────────────────────────────────────────
			byte[] newFst = RebuildFst(entries, newOffsets, newSizes, nameTableStart, fstBytes);
			uint newFstSize = (uint)newFst.Length;

			// ── Write output ─────────────────────────────────────────────────
			// 1. Copy everything up to dataStart verbatim (header + system + dol + old fst region)
			progress?.Report("Copying disc header and system data…");
			source.Position = 0;
			CopyBytes(source, dest, dataStart);

			// 2. Write file data
			progress?.Report($"Writing {fileEntries.Count} files…");
			int written = 0;
			foreach (var e in fileEntries)
			{
				uint newOff = newOffsets[e.Index];
				uint newSz = newSizes[e.Index];

				// Pad to newOff from current position
				long pad = newOff - dest.Position;
				if (pad > 0) WriteZeros(dest, (int)pad);

				var matched = replacements.Keys.FirstOrDefault(
					k => k.Equals(e.FullPath, StringComparison.OrdinalIgnoreCase));
				if (matched != null)
				{
					dest.Write(replacements[matched]);
				}
				else
				{
					source.Position = e.FileOffset;
					CopyBytes(source, dest, e.FileSize);
				}
				written++;
				if (written % 50 == 0)
					progress?.Report($"  {written}/{fileEntries.Count} files written…");
			}

			// 3. Write new FST, then pad tail to a 32KB boundary.
			//    GCN discs are read in 2048-byte DVD sectors. Without trailing padding, the last
			//    FST sector read hits EOF and Windows overlapped I/O returns ERROR_HANDLE_EOF,
			//    which Dolphin treats as a hard failure → DVD init fails → null pointer crash.
			long endPos = dest.Position;
			uint newFstOffset = AlignUp((uint)endPos, 0x100);
			WriteZeros(dest, (int)(newFstOffset - endPos));
			dest.Write(newFst);

			// Pad file end to next 32KB boundary so every sector read stays within bounds.
			long afterFst = dest.Position;
			long paddedEnd = (afterFst + 0x7FFFL) & ~0x7FFFL;
			WriteZeros(dest, (int)(paddedEnd - afterFst));

			// 4. Patch header fields: fstOffset @0x0424, fstSize @0x0428
			dest.Position = 0x0424;
			WriteBE(dest, newFstOffset);
			WriteBE(dest, newFstSize);

			// 5. Apply dol asm patches
			foreach (var (Offset, Value) in patches)
			{
				dest.Position = dolOffset + 0x2600 + (Offset - 0x80021840);
				WriteBE(dest, Value);
			}

			progress?.Report("ISO build complete.");
		}

		// ── FST helpers ──────────────────────────────────────────────────────

		private static List<FstEntry> ParseFst(byte[] fst, uint count, uint nameTableStart)
		{
			var list = new List<FstEntry>((int)count);
			for (int i = 0; i < (int)count; i++)
			{
				int off = i * 12;
				bool isDir = (fst[off] & 0x01) != 0;
				uint nameOff = (uint)(((fst[off + 1] << 16) | (fst[off + 2] << 8) | fst[off + 3]));
				uint field1 = BinaryPrimitives.ReadUInt32BigEndian(fst.AsSpan(off + 4, 4));
				uint field2 = BinaryPrimitives.ReadUInt32BigEndian(fst.AsSpan(off + 8, 4));

				int ns = (int)(nameTableStart + nameOff);
				int ne = Array.IndexOf(fst, (byte)0, ns);
				if (ne < 0) ne = fst.Length;
				string name = Encoding.ASCII.GetString(fst, ns, ne - ns);

				list.Add(new FstEntry
				{
					Index = i,
					IsDirectory = isDir,
					Name = name,
					FileOffset = isDir ? 0 : field1,
					FileSize = isDir ? 0 : field2,
					DirParent = isDir ? (int)field1 : -1,
					DirNext = isDir ? (int)field2 : -1,
				});
			}
			return list;
		}

		private static void AssignFullPaths(List<FstEntry> entries)
		{
			var stack = new Stack<int>();
			stack.Push(0);
			for (int i = 1; i < entries.Count; i++)
			{
				while (stack.Count > 1 && entries[i].Index >= entries[stack.Peek()].DirNext)
					stack.Pop();
				string parentPath = stack.Count > 0 ? entries[stack.Peek()].FullPath : "";
				entries[i].FullPath = parentPath.Length > 0 ? parentPath + "/" + entries[i].Name : entries[i].Name;
				if (entries[i].IsDirectory) stack.Push(i);
			}
		}

		private static byte[] RebuildFst(
			List<FstEntry> entries,
			Dictionary<int, uint> newOffsets,
			Dictionary<int, uint> newSizes,
			uint nameTableStart,
			byte[] originalFst)
		{
			// Copy original FST bytes and patch file offsets/sizes in-place.
			// This preserves directory structure, parent/next indexes, and name table exactly.
			byte[] newFst = (byte[])originalFst.Clone();
			foreach (var e in entries)
			{
				if (e.IsDirectory) continue;
				int off = e.Index * 12;
				BinaryPrimitives.WriteUInt32BigEndian(newFst.AsSpan(off + 4), newOffsets[e.Index]);
				BinaryPrimitives.WriteUInt32BigEndian(newFst.AsSpan(off + 8), newSizes[e.Index]);
			}
			return newFst;
		}

		// ── Stream helpers ───────────────────────────────────────────────────

		private static void CopyBytes(Stream src, Stream dst, long count)
		{
			const int Chunk = 65536;
			var buf = new byte[Chunk];
			long remaining = count;
			while (remaining > 0)
			{
				int toRead = (int)Math.Min(Chunk, remaining);
				int read = src.Read(buf, 0, toRead);
				if (read == 0) break;
				dst.Write(buf, 0, read);
				remaining -= read;
			}
		}

		private static void WriteZeros(Stream dst, int count)
		{
			var zeros = new byte[Math.Min(count, 4096)];
			int remaining = count;
			while (remaining > 0)
			{
				int w = Math.Min(zeros.Length, remaining);
				dst.Write(zeros, 0, w);
				remaining -= w;
			}
		}

		private static void WriteBE(Stream s, uint v)
		{
			Span<byte> b = stackalloc byte[4];
			BinaryPrimitives.WriteUInt32BigEndian(b, v);
			s.Write(b);
		}

		private static uint AlignUp(uint v, uint align) => (v + align - 1) & ~(align - 1);

		private class FstEntry
		{
			public int Index;
			public bool IsDirectory;
			public string Name = "";
			public string FullPath = "";
			public uint FileOffset;
			public uint FileSize;
			public int DirParent;
			public int DirNext;
		}
	}
}