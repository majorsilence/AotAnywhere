using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Xml;

namespace AotAnywhere.AppleTools;

/// <summary>
/// Property lists, as plain .NET values: <see cref="Dictionary{TKey,TValue}"/> (string keys, ordinal),
/// <see cref="List{T}"/>, <see cref="string"/>, <see cref="long"/>, <see cref="double"/>, <see cref="bool"/>,
/// <see cref="byte"/>[] and <see cref="DateTime"/> (UTC). Reads the XML and binary (bplist00) formats;
/// writes XML in the layout Apple's tools emit.
/// </summary>
public static class Plist
{
    public static object Read(byte[] data) =>
        data.AsSpan().StartsWith("bplist00"u8) ? ReadBinary(data) : ReadXml(data);

    public static Dictionary<string, object> ReadDictionary(byte[] data) =>
        Read(data) as Dictionary<string, object> ?? throw new AppleToolException("AotAnywhere: expected a property list whose root is a dictionary.");

    #region XML

    static object ReadXml(byte[] data)
    {
        var doc = new XmlDocument { XmlResolver = null };
        using (var reader = XmlReader.Create(new MemoryStream(data), new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null }))
            doc.Load(reader);
        var root = doc.DocumentElement ?? throw new AppleToolException("AotAnywhere: empty property list.");
        var value = root.ChildNodes.OfType<XmlElement>().FirstOrDefault()
            ?? throw new AppleToolException("AotAnywhere: property list has no root value.");
        return ParseXml(value);
    }

    static object ParseXml(XmlElement e)
    {
        switch (e.Name)
        {
            case "dict":
                var dict = new Dictionary<string, object>(StringComparer.Ordinal);
                var children = e.ChildNodes.OfType<XmlElement>().ToList();
                for (int i = 0; i + 1 < children.Count; i += 2)
                {
                    if (children[i].Name != "key")
                        throw new AppleToolException("AotAnywhere: malformed property list dictionary.");
                    dict[children[i].InnerText] = ParseXml(children[i + 1]);
                }
                return dict;
            case "array":
                return e.ChildNodes.OfType<XmlElement>().Select(ParseXml).ToList();
            case "string":
                return e.InnerText;
            case "integer":
                return long.Parse(e.InnerText.Trim(), CultureInfo.InvariantCulture);
            case "real":
                return double.Parse(e.InnerText.Trim(), CultureInfo.InvariantCulture);
            case "true":
                return true;
            case "false":
                return false;
            case "data":
                return Convert.FromBase64String(new string(e.InnerText.Where(c => !char.IsWhiteSpace(c)).ToArray()));
            case "date":
                return DateTime.Parse(e.InnerText.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
            default:
                throw new AppleToolException($"AotAnywhere: unsupported property list element <{e.Name}>.");
        }
    }

    /// <summary>XML, laid out like Apple's writers (tab indentation, base64 on its own line).</summary>
    public static byte[] WriteXml(object value)
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");
        sb.Append("<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">\n");
        sb.Append("<plist version=\"1.0\">\n");
        WriteXmlValue(sb, value, 0);
        sb.Append("</plist>\n");
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    static void WriteXmlValue(StringBuilder sb, object value, int depth)
    {
        string indent = new('\t', depth);
        switch (value)
        {
            case Dictionary<string, object> dict when dict.Count == 0:
                sb.Append(indent).Append("<dict/>\n");
                break;
            case Dictionary<string, object> dict:
                sb.Append(indent).Append("<dict>\n");
                foreach (var kv in dict.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                {
                    sb.Append(indent).Append('\t').Append("<key>").Append(Escape(kv.Key)).Append("</key>\n");
                    WriteXmlValue(sb, kv.Value, depth + 1);
                }
                sb.Append(indent).Append("</dict>\n");
                break;
            case List<object> list when list.Count == 0:
                sb.Append(indent).Append("<array/>\n");
                break;
            case List<object> list:
                sb.Append(indent).Append("<array>\n");
                foreach (var item in list)
                    WriteXmlValue(sb, item, depth + 1);
                sb.Append(indent).Append("</array>\n");
                break;
            case string s:
                sb.Append(indent).Append("<string>").Append(Escape(s)).Append("</string>\n");
                break;
            case bool b:
                sb.Append(indent).Append(b ? "<true/>\n" : "<false/>\n");
                break;
            case long or int:
                sb.Append(indent).Append("<integer>").Append(Convert.ToInt64(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture)).Append("</integer>\n");
                break;
            case double d:
                sb.Append(indent).Append("<real>").Append(d.ToString("R", CultureInfo.InvariantCulture)).Append("</real>\n");
                break;
            case byte[] data:
                sb.Append(indent).Append("<data>\n").Append(indent).Append(Convert.ToBase64String(data)).Append('\n').Append(indent).Append("</data>\n");
                break;
            case DateTime dt:
                sb.Append(indent).Append("<date>").Append(dt.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)).Append("</date>\n");
                break;
            default:
                throw new AppleToolException($"AotAnywhere: cannot write a {value.GetType().Name} to a property list.");
        }
    }

    static string Escape(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    #endregion

    #region Binary (bplist00)

    static object ReadBinary(byte[] data)
    {
        if (data.Length < 40)
            throw new AppleToolException("AotAnywhere: truncated binary property list.");
        var trailer = data.AsSpan(data.Length - 32);
        int offsetSize = trailer[6];
        int refSize = trailer[7];
        long numObjects = (long)BinaryPrimitives.ReadUInt64BigEndian(trailer.Slice(8));
        long topObject = (long)BinaryPrimitives.ReadUInt64BigEndian(trailer.Slice(16));
        long tableOffset = (long)BinaryPrimitives.ReadUInt64BigEndian(trailer.Slice(24));

        long[] offsets = new long[numObjects];
        for (long i = 0; i < numObjects; i++)
            offsets[i] = (long)ReadUInt(data, (int)(tableOffset + i * offsetSize), offsetSize);

        var reader = new BinaryReader(data, offsets, refSize);
        return reader.Object(topObject, 0);
    }

    static ulong ReadUInt(byte[] data, int at, int size)
    {
        ulong v = 0;
        for (int i = 0; i < size; i++)
            v = (v << 8) | data[at + i];
        return v;
    }

    sealed class BinaryReader(byte[] data, long[] offsets, int refSize)
    {
        public object Object(long index, int depth)
        {
            if (depth > 64 || index < 0 || index >= offsets.Length)
                throw new AppleToolException("AotAnywhere: malformed binary property list.");
            int at = (int)offsets[index];
            byte marker = data[at];
            int type = marker >> 4, info = marker & 0xf;
            switch (type)
            {
                case 0x0:
                    return info switch
                    {
                        0x8 => false,
                        0x9 => true,
                        _ => throw new AppleToolException("AotAnywhere: unsupported binary property list value."),
                    };
                case 0x1:
                {
                    int n = 1 << info;
                    if (n > 8)
                        throw new AppleToolException("AotAnywhere: 128-bit integers are not supported.");
                    return (long)ReadUInt(data, at + 1, n);
                }
                case 0x2:
                    return info == 2
                        ? (double)BitConverter.Int32BitsToSingle((int)ReadUInt(data, at + 1, 4))
                        : BitConverter.Int64BitsToDouble((long)ReadUInt(data, at + 1, 8));
                case 0x3:
                    return new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(BitConverter.Int64BitsToDouble((long)ReadUInt(data, at + 1, 8)));
                case 0x4:
                {
                    var (len, start) = Length(at, info);
                    return data.AsSpan(start, (int)len).ToArray();
                }
                case 0x5:
                {
                    var (len, start) = Length(at, info);
                    return Encoding.ASCII.GetString(data, start, (int)len);
                }
                case 0x6:
                {
                    var (len, start) = Length(at, info);
                    return Encoding.BigEndianUnicode.GetString(data, start, (int)len * 2);
                }
                case 0xA:
                {
                    var (len, start) = Length(at, info);
                    var list = new List<object>((int)len);
                    for (int i = 0; i < len; i++)
                        list.Add(Object(Ref(start + i * refSize), depth + 1));
                    return list;
                }
                case 0xD:
                {
                    var (len, start) = Length(at, info);
                    var dict = new Dictionary<string, object>(StringComparer.Ordinal);
                    for (int i = 0; i < len; i++)
                    {
                        var key = Object(Ref(start + i * refSize), depth + 1) as string
                            ?? throw new AppleToolException("AotAnywhere: binary property list dictionary key is not a string.");
                        dict[key] = Object(Ref(start + (int)(len + i) * refSize), depth + 1);
                    }
                    return dict;
                }
                default:
                    throw new AppleToolException($"AotAnywhere: unsupported binary property list object type 0x{type:x}.");
            }
        }

        long Ref(int at) => (long)ReadUInt(data, at, refSize);

        // Counts >= 15 are stored as a following integer object.
        (long Length, int Start) Length(int at, int info)
        {
            if (info != 0xf)
                return (info, at + 1);
            int n = 1 << (data[at + 1] & 0xf);
            return ((long)ReadUInt(data, at + 2, n), at + 2 + n);
        }
    }

    #endregion
}
