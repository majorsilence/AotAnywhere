using System.Text;

namespace AotAnywhere.AppleTools;

/// <summary>
/// UIKit's compiled-nib container (<c>NIBArchive</c>): a keyed object graph - objects (a class name
/// and a run of key/value items), with values that are numbers, booleans, data blobs, null, or
/// references to other objects. Layout as implemented by the MIT-licensed WinObjC xib2nib
/// (tools/vsimporter/xib2nib/src/NIBWriter.cpp): header, then class names, keys, items and objects,
/// with counts and offsets in the header and variable-length integers in the tables.
/// </summary>
public sealed class NibArchive
{
    public sealed class Obj(string className)
    {
        public string ClassName { get; } = className;
        public List<(string Key, object? Value)> Items { get; } = [];

        public Obj Add(string key, object? value)
        {
            Items.Add((key, value));
            return this;
        }

        public object? this[string key] => Items.FirstOrDefault(i => i.Key == key).Value;
    }

    /// <summary>A double-precision float value (tag 7); plain <see cref="float"/> is tag 6.</summary>
    public readonly record struct Double(double Value);

    public List<Obj> Objects { get; } = [];

    /// <summary>The root object (the first written).</summary>
    public Obj Root => Objects[0];

    const byte Int8 = 0, Int16 = 1, Int32 = 2, Int64 = 3, False = 4, True = 5, Float = 6, DoubleTag = 7, Data = 8, Null = 9, Ref = 10;

    // --- writing ---------------------------------------------------------------------------

    /// <summary>
    /// Serializes the graph reachable from <paramref name="root"/>, in depth-first order of first
    /// reference (the root is object 0, as UIKit expects).
    /// </summary>
    public static byte[] Write(Obj root)
    {
        var objects = new List<Obj>();
        var index = new Dictionary<Obj, int>(ReferenceEqualityComparer.Instance);
        void Visit(Obj o)
        {
            if (index.ContainsKey(o))
                return;
            index[o] = objects.Count;
            objects.Add(o);
            foreach (var (_, v) in o.Items)
                if (v is Obj child)
                    Visit(child);
        }
        Visit(root);

        var classNames = new List<string>();
        var keyNames = new List<string>();
        int ClassIndex(string n) { int i = classNames.IndexOf(n); if (i < 0) { i = classNames.Count; classNames.Add(n); } return i; }
        int KeyIndex(string n) { int i = keyNames.IndexOf(n); if (i < 0) { i = keyNames.Count; keyNames.Add(n); } return i; }
        foreach (var o in objects)
            ClassIndex(o.ClassName);
        foreach (var o in objects)
            foreach (var (k, _) in o.Items)
                KeyIndex(k);

        var classes = new MemoryStream();
        foreach (var n in classNames)
        {
            var bytes = Encoding.UTF8.GetBytes(n + "\0");
            WriteVarInt(classes, bytes.Length);
            WriteVarInt(classes, 0); // number of extra int32s (none)
            classes.Write(bytes);
        }
        var keys = new MemoryStream();
        foreach (var n in keyNames)
        {
            var bytes = Encoding.UTF8.GetBytes(n);
            WriteVarInt(keys, bytes.Length);
            keys.Write(bytes);
        }
        var items = new MemoryStream();
        var objs = new MemoryStream();
        int itemCount = 0;
        foreach (var o in objects)
        {
            WriteVarInt(objs, classNames.IndexOf(o.ClassName));
            WriteVarInt(objs, itemCount);
            WriteVarInt(objs, o.Items.Count);
            foreach (var (k, v) in o.Items)
            {
                WriteVarInt(items, keyNames.IndexOf(k));
                WriteValue(items, v, index);
                itemCount++;
            }
        }

        const int headerSize = 10 + 4 * 10;
        int objectsOffset = headerSize, keysOffset = objectsOffset + (int)objs.Length,
            itemsOffset = keysOffset + (int)keys.Length, classesOffset = itemsOffset + (int)items.Length;
        var output = new MemoryStream();
        output.Write("NIBArchive"u8);
        foreach (int v in new[] { 1, 10, objects.Count, objectsOffset, keyNames.Count, keysOffset, itemCount, itemsOffset, classNames.Count, classesOffset })
            output.Write(BitConverter.GetBytes(v));
        objs.WriteTo(output);
        keys.WriteTo(output);
        items.WriteTo(output);
        classes.WriteTo(output);
        return output.ToArray();
    }

    static void WriteValue(Stream s, object? v, Dictionary<Obj, int> index)
    {
        switch (v)
        {
            case null: s.WriteByte(Null); break;
            case bool b: s.WriteByte(b ? True : False); break;
            case Obj o: s.WriteByte(Ref); s.Write(BitConverter.GetBytes(index[o])); break;
            case Double d: s.WriteByte(DoubleTag); s.Write(BitConverter.GetBytes(d.Value)); break;
            case float f: s.WriteByte(Float); s.Write(BitConverter.GetBytes(f)); break;
            case byte[] data: s.WriteByte(Data); WriteVarInt(s, data.Length); s.Write(data); break;
            case int i when i is >= sbyte.MinValue and <= sbyte.MaxValue: s.WriteByte(Int8); s.WriteByte((byte)(sbyte)i); break;
            case int i when i is >= short.MinValue and <= short.MaxValue: s.WriteByte(Int16); s.Write(BitConverter.GetBytes((short)i)); break;
            case int i: s.WriteByte(Int32); s.Write(BitConverter.GetBytes(i)); break;
            case long l: s.WriteByte(Int64); s.Write(BitConverter.GetBytes(l)); break;
            default: throw new ArgumentException($"unsupported NIB value {v.GetType()}");
        }
    }

    // Little-endian base-128 with the high bit marking the LAST byte (UIKit's variant).
    static void WriteVarInt(Stream s, int value)
    {
        uint v = (uint)value;
        while (v >= 0x80)
        {
            s.WriteByte((byte)(v & 0x7f));
            v >>= 7;
        }
        s.WriteByte((byte)(v | 0x80));
    }

    // --- reading (tests and diagnostics) ---------------------------------------------------

    public static NibArchive Read(byte[] data)
    {
        if (data.Length < 50 || Encoding.ASCII.GetString(data, 0, 10) != "NIBArchive")
            throw new AppleToolException("AotAnywhere: not a NIBArchive.");
        int H(int i) => BitConverter.ToInt32(data, 10 + i * 4);
        int objectCount = H(2), objectsOffset = H(3), keyCount = H(4), keysOffset = H(5), itemsOffset = H(7), classCount = H(8), classesOffset = H(9);

        int pos = classesOffset;
        var classes = new List<string>();
        for (int i = 0; i < classCount; i++)
        {
            int len = ReadVarInt(data, ref pos);
            int extra = ReadVarInt(data, ref pos);
            pos += extra * 4;
            classes.Add(Encoding.UTF8.GetString(data, pos, len).TrimEnd('\0'));
            pos += len;
        }
        pos = keysOffset;
        var keys = new List<string>();
        for (int i = 0; i < keyCount; i++)
        {
            int len = ReadVarInt(data, ref pos);
            keys.Add(Encoding.UTF8.GetString(data, pos, len));
            pos += len;
        }

        var archive = new NibArchive();
        var raw = new List<(int Class, int First, int Count)>();
        pos = objectsOffset;
        for (int i = 0; i < objectCount; i++)
        {
            raw.Add((ReadVarInt(data, ref pos), ReadVarInt(data, ref pos), ReadVarInt(data, ref pos)));
            archive.Objects.Add(new Obj(classes[raw[i].Class]));
        }

        // Items are stored in object order; read them all, resolving references afterwards.
        pos = itemsOffset;
        var items = new List<(string Key, object? Value, int Ref)>();
        int total = raw.Count == 0 ? 0 : raw.Max(r => r.First + r.Count);
        for (int i = 0; i < total; i++)
        {
            string key = keys[ReadVarInt(data, ref pos)];
            byte tag = data[pos++];
            object? value = null;
            int reference = -1;
            switch (tag)
            {
                case Int8: value = (int)(sbyte)data[pos]; pos += 1; break;
                case Int16: value = (int)BitConverter.ToInt16(data, pos); pos += 2; break;
                case Int32: value = BitConverter.ToInt32(data, pos); pos += 4; break;
                case Int64: value = BitConverter.ToInt64(data, pos); pos += 8; break;
                case False: value = false; break;
                case True: value = true; break;
                case Float: value = BitConverter.ToSingle(data, pos); pos += 4; break;
                case DoubleTag: value = new Double(BitConverter.ToDouble(data, pos)); pos += 8; break;
                case Data: { int len = ReadVarInt(data, ref pos); value = data.AsSpan(pos, len).ToArray(); pos += len; break; }
                case Null: break;
                case Ref: reference = BitConverter.ToInt32(data, pos); pos += 4; break;
                default: throw new AppleToolException($"AotAnywhere: unknown NIB value type {tag}.");
            }
            items.Add((key, value, reference));
        }
        for (int i = 0; i < raw.Count; i++)
            for (int j = raw[i].First; j < raw[i].First + raw[i].Count; j++)
                archive.Objects[i].Add(items[j].Key, items[j].Ref >= 0 ? archive.Objects[items[j].Ref] : items[j].Value);
        return archive;
    }

    static int ReadVarInt(byte[] data, ref int pos)
    {
        int result = 0, shift = 0;
        while (true)
        {
            byte b = data[pos++];
            result |= (b & 0x7f) << shift;
            if ((b & 0x80) != 0)
                return result;
            shift += 7;
        }
    }

    /// <summary>A readable dump of the graph from the root (for tests and debugging).</summary>
    public string Dump()
    {
        var sb = new StringBuilder();
        var seen = new HashSet<Obj>(ReferenceEqualityComparer.Instance);
        void Write(Obj o, int depth)
        {
            if (!seen.Add(o))
            {
                sb.Append("@").Append(Objects.IndexOf(o)).Append(' ').AppendLine(o.ClassName);
                return;
            }
            sb.Append('#').Append(Objects.IndexOf(o)).Append(' ').AppendLine(o.ClassName);
            foreach (var (k, v) in o.Items)
            {
                sb.Append(new string(' ', depth * 2 + 2)).Append(k).Append(" = ");
                if (v is Obj child)
                    Write(child, depth + 1);
                else
                    sb.AppendLine(v switch
                    {
                        null => "nil",
                        byte[] b => b.All(c => c is >= 0x20 and < 0x7f) && b.Length > 0 ? $"\"{Encoding.ASCII.GetString(b)}\"" : $"<{Convert.ToHexString(b)}>",
                        Double d => $"{d.Value}d",
                        float f => $"{f}f",
                        _ => v.ToString(),
                    });
            }
        }
        if (Objects.Count > 0)
            Write(Root, 0);
        return sb.ToString();
    }
}
