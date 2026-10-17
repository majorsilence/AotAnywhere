using System.Text;

namespace AotAnywhere.AppleTools;

/// <summary>
/// The DER form of an entitlements plist that iOS 15+ requires in code signatures
/// (code-signature slot 7, blob magic 0xfade7172), as codesign's
/// <c>--generate-entitlement-der</c> writes it:
/// <code>
/// [APPLICATION 16] { INTEGER 1, dict }
/// dict   = [CONTEXT 16] { SEQUENCE { UTF8String key, value }... }   (keys in bytewise order)
/// value  = BOOLEAN | INTEGER | UTF8String | SEQUENCE { value... } (array) | dict
/// </code>
/// An empty dictionary encodes as <c>70 05 02 01 01 b0 00</c>.
/// </summary>
public static class EntitlementsDer
{
    public static byte[] Encode(Dictionary<string, object> entitlements)
    {
        var body = new List<byte>();
        body.AddRange(Tlv(0x02, [0x01]));        // version 1
        body.AddRange(EncodeValue(entitlements));
        return Tlv(0x70, body);
    }

    static byte[] EncodeValue(object value) => value switch
    {
        bool b => Tlv(0x01, [b ? (byte)0xff : (byte)0x00]),
        long or int => Tlv(0x02, Integer(Convert.ToInt64(value))),
        string s => Tlv(0x0c, Encoding.UTF8.GetBytes(s)),
        List<object> list => Tlv(0x30, list.SelectMany(EncodeValue).ToArray()),
        Dictionary<string, object> dict => Tlv(0xb0, dict
            .OrderBy(kv => Encoding.UTF8.GetBytes(kv.Key), ByteOrder.Instance)
            .SelectMany(kv => Tlv(0x30, [.. Tlv(0x0c, Encoding.UTF8.GetBytes(kv.Key)), .. EncodeValue(kv.Value)]))
            .ToArray()),
        _ => throw new AppleToolException($"AotAnywhere: entitlement values of type {value.GetType().Name} cannot be DER-encoded."),
    };

    // Minimal two's-complement big-endian.
    static byte[] Integer(long v)
    {
        var bytes = BitConverter.GetBytes(v);
        if (BitConverter.IsLittleEndian)
            Array.Reverse(bytes);
        int start = 0;
        while (start < 7 && ((bytes[start] == 0x00 && (bytes[start + 1] & 0x80) == 0) || (bytes[start] == 0xff && (bytes[start + 1] & 0x80) != 0)))
            start++;
        return bytes[start..];
    }

    static byte[] Tlv(byte tag, IReadOnlyCollection<byte> content)
    {
        var result = new List<byte>(content.Count + 6) { tag };
        int len = content.Count;
        if (len < 0x80)
        {
            result.Add((byte)len);
        }
        else
        {
            var lenBytes = new List<byte>();
            for (int l = len; l > 0; l >>= 8)
                lenBytes.Insert(0, (byte)(l & 0xff));
            result.Add((byte)(0x80 | lenBytes.Count));
            result.AddRange(lenBytes);
        }
        result.AddRange(content);
        return result.ToArray();
    }

    sealed class ByteOrder : IComparer<byte[]>
    {
        public static readonly ByteOrder Instance = new();
        public int Compare(byte[]? x, byte[]? y) => x.AsSpan().SequenceCompareTo(y);
    }
}
