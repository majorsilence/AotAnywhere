using System.Buffers.Binary;
using System.Text;

namespace AotAnywhere.AppleTools;

/// <summary>
/// The NeXTSTEP "BOMStore" container that compiled asset catalogs (<c>Assets.car</c>) live in.
/// Layout as described by the BSD-licensed facebook/xcbuild (Libraries/libbom/Headers/bom/bom_format.h, bom_tree.c):
/// a header, numbered data blocks found through an index table, and named variables that point
/// at blocks. A variable may hold a B+ tree of key/value blocks; trees written here are one leaf
/// with the keys in byte order. All BOM fields are big-endian.
/// </summary>
public sealed class Bom
{
    readonly List<byte[]> _blocks = [[]]; // block 0 is the null block
    readonly List<(string Name, int Block)> _variables = [];

    public int AddBlock(byte[] data)
    {
        _blocks.Add(data);
        return _blocks.Count - 1;
    }

    public void AddVariable(string name, byte[] data) => _variables.Add((name, AddBlock(data)));

    public void AddTree(string name, IEnumerable<(byte[] Key, byte[] Value)> entries, int nodeSize = 4096)
    {
        var sorted = entries.OrderBy(e => e.Key, ByteOrder.Instance).ToList();
        // A leaf is the node header and the (value, key) block pairs. When every key has the same
        // length (rendition keys), that length is recorded in the tree and the leaf also carries, after
        // 4 zero bytes, a copy of every key back to back - CoreUI compares against those, not the key
        // blocks - so the node is the tree's node size plus the inline keys. Trees of variable-length
        // keys (names) record 0xffffffff and have no inline copy. (Neither detail is in xcbuild's
        // writer; both were found by checking against Apple-compiled catalogs.)
        if (16 + 8 * sorted.Count > nodeSize)
            throw new ArgumentException($"BOM tree {name} has too many entries for one leaf");
        bool fixedKeys = sorted.Count > 0 && sorted.All(e => e.Key.Length == sorted[0].Key.Length);
        var leaf = new byte[nodeSize + (fixedKeys ? sorted.Sum(e => e.Key.Length) : 0)];
        BinaryPrimitives.WriteUInt16BigEndian(leaf, 1); // is_leaf
        BinaryPrimitives.WriteUInt16BigEndian(leaf.AsSpan(2), checked((ushort)sorted.Count));
        for (int i = 0; i < sorted.Count; i++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(leaf.AsSpan(12 + 8 * i), (uint)AddBlock(sorted[i].Value));
            BinaryPrimitives.WriteUInt32BigEndian(leaf.AsSpan(16 + 8 * i), (uint)AddBlock(sorted[i].Key));
        }
        if (fixedKeys)
        {
            int inline = 16 + 8 * sorted.Count;
            foreach (var (key, _) in sorted)
            {
                key.CopyTo(leaf, inline);
                inline += key.Length;
            }
        }
        int leafBlock = AddBlock(leaf);

        var tree = new byte[29];
        "tree"u8.CopyTo(tree);
        BinaryPrimitives.WriteUInt32BigEndian(tree.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt32BigEndian(tree.AsSpan(8), (uint)leafBlock);
        BinaryPrimitives.WriteUInt32BigEndian(tree.AsSpan(12), (uint)nodeSize);
        BinaryPrimitives.WriteUInt32BigEndian(tree.AsSpan(16), (uint)sorted.Count);
        // Byte 20 is a flag (0 here); then the key length, or 0xffffffff for variable-length keys.
        BinaryPrimitives.WriteUInt32BigEndian(tree.AsSpan(21), fixedKeys ? (uint)sorted[0].Key.Length : 0xffffffff);
        _variables.Add((name, AddBlock(tree)));
    }

    /// <summary>Header (padded to 512 bytes, as Apple's files are), data blocks, variables, then the index table.</summary>
    public byte[] Write()
    {
        var output = new MemoryStream();
        output.Write(new byte[512]);
        var addresses = new (int Address, int Length)[_blocks.Count];
        for (int i = 1; i < _blocks.Count; i++)
        {
            addresses[i] = ((int)output.Length, _blocks[i].Length);
            output.Write(_blocks[i]);
            while (output.Length % 4 != 0)
                output.WriteByte(0);
        }

        int variablesOffset = (int)output.Length;
        var u32 = new byte[4];
        void U32(int v) { BinaryPrimitives.WriteUInt32BigEndian(u32, (uint)v); output.Write(u32); }
        U32(_variables.Count);
        foreach (var (name, block) in _variables)
        {
            U32(block);
            var bytes = Encoding.ASCII.GetBytes(name);
            output.WriteByte((byte)bytes.Length);
            output.Write(bytes);
        }
        int variablesLength = (int)output.Length - variablesOffset;
        while (output.Length % 4 != 0)
            output.WriteByte(0);

        int indexOffset = (int)output.Length;
        U32(_blocks.Count);
        foreach (var (address, length) in addresses)
        {
            U32(address);
            U32(length);
        }
        // An empty free list, with room for two entries, as every BOM file has.
        U32(0);
        for (int i = 0; i < 4; i++)
            U32(0);
        int indexLength = (int)output.Length - indexOffset;

        var data = output.ToArray();
        "BOMStore"u8.CopyTo(data);
        var header = data.AsSpan(8);
        BinaryPrimitives.WriteUInt32BigEndian(header, 1);
        BinaryPrimitives.WriteUInt32BigEndian(header[4..], (uint)(_blocks.Count - 1));
        BinaryPrimitives.WriteUInt32BigEndian(header[8..], (uint)indexOffset);
        BinaryPrimitives.WriteUInt32BigEndian(header[12..], (uint)indexLength);
        BinaryPrimitives.WriteUInt32BigEndian(header[16..], (uint)variablesOffset);
        BinaryPrimitives.WriteUInt32BigEndian(header[20..], (uint)variablesLength);
        return data;
    }

    // --- reading (tests and diagnostics) ---------------------------------------------------

    public sealed class Reader
    {
        readonly byte[] _data;
        readonly (int Address, int Length)[] _index;
        public Dictionary<string, int> Variables { get; } = new(StringComparer.Ordinal);

        public Reader(byte[] data)
        {
            if (data.Length < 32 || Encoding.ASCII.GetString(data, 0, 8) != "BOMStore")
                throw new AppleToolException("AotAnywhere: not a BOM file.");
            _data = data;
            int indexOffset = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(16));
            int variablesOffset = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(24));
            int count = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(indexOffset));
            _index = new (int, int)[count];
            for (int i = 0; i < count; i++)
                _index[i] = ((int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(indexOffset + 4 + 8 * i)),
                             (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(indexOffset + 8 + 8 * i)));
            int vars = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(variablesOffset));
            int pos = variablesOffset + 4;
            for (int i = 0; i < vars; i++)
            {
                int block = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos));
                int length = data[pos + 4];
                Variables[Encoding.ASCII.GetString(data, pos + 5, length)] = block;
                pos += 5 + length;
            }
        }

        public byte[] Block(int i) => _data.AsSpan(_index[i].Address, _index[i].Length).ToArray();

        public byte[] Variable(string name) => Block(Variables[name]);

        /// <summary>The entries of a tree variable, walking every leaf.</summary>
        public List<(byte[] Key, byte[] Value)> Tree(string name)
        {
            var tree = Variable(name);
            if (Encoding.ASCII.GetString(tree, 0, 4) != "tree")
                throw new AppleToolException($"AotAnywhere: BOM variable {name} is not a tree.");
            var result = new List<(byte[], byte[])>();
            var node = Block((int)BinaryPrimitives.ReadUInt32BigEndian(tree.AsSpan(8)));
            if (BinaryPrimitives.ReadUInt16BigEndian(node) == 0)
                node = Block((int)BinaryPrimitives.ReadUInt32BigEndian(node.AsSpan(12))); // first child of a branch
            while (true)
            {
                int count = BinaryPrimitives.ReadUInt16BigEndian(node.AsSpan(2));
                for (int i = 0; i < count; i++)
                    result.Add((Block((int)BinaryPrimitives.ReadUInt32BigEndian(node.AsSpan(16 + 8 * i))),
                                Block((int)BinaryPrimitives.ReadUInt32BigEndian(node.AsSpan(12 + 8 * i)))));
                int forward = (int)BinaryPrimitives.ReadUInt32BigEndian(node.AsSpan(4));
                if (forward == 0)
                    return result;
                node = Block(forward);
            }
        }
    }

    sealed class ByteOrder : IComparer<byte[]>
    {
        public static readonly ByteOrder Instance = new();
        public int Compare(byte[]? x, byte[]? y) => x.AsSpan().SequenceCompareTo(y);
    }
}
