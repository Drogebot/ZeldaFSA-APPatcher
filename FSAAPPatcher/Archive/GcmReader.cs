using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace FSAAPPatcher.Archive
{
	/// <summary>
	/// Reads files directly from a GameCube disc image (GCM / .iso format).
	/// The GCM filesystem uses a flat FST (File System Table) rather than ISO9660.
	/// </summary>
	public class GcmReader : IDisposable
	{
		private readonly Stream _disc;
		private readonly List<GcmEntry> _entries = new();
		private string[] _nameTable = Array.Empty<string>();
		private uint _dolOffset;
		private uint _fstOffset;

		public IReadOnlyList<GcmEntry> Entries => _entries;

		private GcmReader(Stream disc) => _disc = disc;

		public static GcmReader Open(string path)
		{
			var fs = File.OpenRead(path);
			var reader = new GcmReader(fs);
			reader.ParseFst();
			return reader;
		}

		private void ParseFst()
		{
			Span<byte> buf4 = stackalloc byte[4];

			// Verify GCN magic at 0x001C
			_disc.Position = 0x001C;
			_disc.ReadExactly(buf4);
			uint magic = BinaryPrimitives.ReadUInt32BigEndian(buf4);
			if (magic != 0xC2339F3D)
				throw new InvalidDataException("Not a valid GameCube disc image (magic mismatch).");

			// FST offset and size are at 0x0424/0x0428
			_disc.Position = 0x0420;
			_disc.ReadExactly(buf4);
			this._dolOffset = BinaryPrimitives.ReadUInt32BigEndian(buf4);
			_disc.ReadExactly(buf4);
			uint fstOffset = BinaryPrimitives.ReadUInt32BigEndian(buf4);
			this._fstOffset = fstOffset;
			_disc.ReadExactly(buf4);
			uint fstSize = BinaryPrimitives.ReadUInt32BigEndian(buf4);

			// Read the whole FST
			byte[] fst = new byte[fstSize];
			_disc.Position = fstOffset;
			_disc.ReadExactly(fst);

			// Root entry is at offset 0; its numEntries field gives total entry count
			uint totalEntries = BinaryPrimitives.ReadUInt32BigEndian(fst.AsSpan(8, 4));
			uint nameTableOffset = totalEntries * 12;

			// Build null-terminated string lookup from name table
			_nameTable = new string[totalEntries];
			for (uint i = 0; i < totalEntries; i++)
			{
				uint entryOff = i * 12;
				bool isDir = (fst[entryOff] & 0x01) != 0;
				uint nameOff = (uint)(((fst[entryOff + 1] << 16) | (fst[entryOff + 2] << 8) | fst[entryOff + 3]));
				uint dataOrParent = BinaryPrimitives.ReadUInt32BigEndian(fst.AsSpan((int)entryOff + 4, 4));
				uint sizeOrNext = BinaryPrimitives.ReadUInt32BigEndian(fst.AsSpan((int)entryOff + 8, 4));

				int nameStart = (int)(nameTableOffset + nameOff);
				int nameEnd = Array.IndexOf(fst, (byte)0, nameStart);
				if (nameEnd < 0) nameEnd = fst.Length;
				string name = Encoding.ASCII.GetString(fst, nameStart, nameEnd - nameStart);

				_entries.Add(new GcmEntry
				{
					Index = (int)i,
					IsDirectory = isDir,
					Name = name,
					FileOffset = isDir ? 0 : dataOrParent,
					FileSize = isDir ? 0 : sizeOrNext,
					ParentIndex = isDir ? (int)dataOrParent : -1,
					NextSiblingIndex = isDir ? (int)sizeOrNext : -1,
				});
			}

			// Build full paths
			BuildPaths();
		}

		private void BuildPaths()
		{
			// Directory entry stores parentIndex and nextSiblingIndex.
			// Walk the flat list; for each entry compute its parent chain.
			var parentStack = new Stack<int>();
			parentStack.Push(0); // root

			for (int i = 1; i < _entries.Count; i++)
			{
				// Pop directories that no longer contain this entry
				while (parentStack.Count > 1)
				{
					var topDir = _entries[parentStack.Peek()];
					if (topDir.IsDirectory && i >= topDir.NextSiblingIndex)
						parentStack.Pop();
					else break;
				}

				var e = _entries[i];
				string parentPath = parentStack.Count > 0 ? _entries[parentStack.Peek()].FullPath : "";
				e.FullPath = parentPath.Length > 0 ? parentPath + "/" + e.Name : e.Name;

				if (e.IsDirectory)
					parentStack.Push(i);
			}
		}

		/// <summary>Find a file by full path (case-insensitive, forward slashes).</summary>
		public GcmEntry? FindFile(string path)
		{
			foreach (var e in _entries)
				if (!e.IsDirectory && e.FullPath.Equals(path, StringComparison.OrdinalIgnoreCase))
					return e;
			return null;
		}

		/// <summary>Find all files whose path matches a prefix directory (case-insensitive).</summary>
		public IEnumerable<GcmEntry> ListFiles(string directoryPath)
		{
			string prefix = directoryPath.TrimEnd('/') + "/";
			foreach (var e in _entries)
				if (!e.IsDirectory && e.FullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
					yield return e;
		}

		/// <summary>Read a file entry's bytes from the disc image.</summary>
		public byte[] ReadFile(GcmEntry entry)
		{
			if (entry.IsDirectory) throw new InvalidOperationException("Cannot read a directory entry.");
			var buf = new byte[entry.FileSize];
			_disc.Position = entry.FileOffset;
			_disc.ReadExactly(buf);
			return buf;
		}

		public Stream OpenDol()
		{
			return new SubStream(_disc, _dolOffset, _fstOffset - _dolOffset);
		}

		public Stream OpenFile(GcmEntry entry)
		{
			if (entry.IsDirectory) throw new InvalidOperationException("Cannot read a directory entry.");
			return new SubStream(_disc, entry.FileOffset, entry.FileSize);
		}

		public void Dispose() => _disc.Dispose();

		// ── Minimal SubStream so callers can stream without loading all bytes ──

		private sealed class SubStream : Stream
		{
			private readonly Stream _parent;
			private readonly long _start;
			private readonly long _length;
			private long _position;

			public SubStream(Stream parent, long start, long length)
			{
				_parent = parent; _start = start; _length = length;
			}

			public override bool CanRead => true;
			public override bool CanSeek => true;
			public override bool CanWrite => false;
			public override long Length => _length;
			public override long Position
			{
				get => _position;
				set => _position = value;
			}

			public override int Read(byte[] buffer, int offset, int count)
			{
				long available = _length - _position;
				if (available <= 0) return 0;
				int toRead = (int)Math.Min(count, available);
				lock (_parent)
				{
					_parent.Position = _start + _position;
					int read = _parent.Read(buffer, offset, toRead);
					_position += read;
					return read;
				}
			}

			public override long Seek(long offset, SeekOrigin origin) => _position = origin switch
			{
				SeekOrigin.Begin => offset,
				SeekOrigin.Current => _position + offset,
				SeekOrigin.End => _length + offset,
				_ => throw new ArgumentOutOfRangeException()
			};

			public override void Flush() { }
			public override void SetLength(long value) => throw new NotSupportedException();
			public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
		}
	}

	public class GcmEntry
	{
		public int Index;
		public bool IsDirectory;
		public string Name = "";
		public string FullPath = "";
		public uint FileOffset;
		public uint FileSize;
		public int ParentIndex;
		public int NextSiblingIndex;
	}
}