namespace AotAnywhere.AppleTools.Xip;

/// <summary>
/// A managed LZMA2 decoder (the only filter Apple's .xip chunks use), following the LZMA SDK's
/// reference decoder. Decodes into a caller-provided buffer of the known output size, which also
/// serves as the dictionary: every pbzx chunk is an independent stream of known length.
/// </summary>
public sealed class Lzma2Decoder
{
    const int NumStates = 12;
    const int NumPosBitsMax = 4;
    const int NumLenToPosStates = 4;
    const int NumAlignBits = 4;
    const int StartPosModelIndex = 4;
    const int EndPosModelIndex = 14;
    const int NumFullDistances = 1 << (EndPosModelIndex >> 1);
    const int MatchMinLen = 2;

    readonly byte[] _out;
    int _pos;

    // Probability models.
    readonly ushort[] _isMatch = new ushort[NumStates << NumPosBitsMax];
    readonly ushort[] _isRep = new ushort[NumStates];
    readonly ushort[] _isRepG0 = new ushort[NumStates];
    readonly ushort[] _isRepG1 = new ushort[NumStates];
    readonly ushort[] _isRepG2 = new ushort[NumStates];
    readonly ushort[] _isRep0Long = new ushort[NumStates << NumPosBitsMax];
    readonly ushort[][] _posSlot = Enumerable.Range(0, NumLenToPosStates).Select(_ => new ushort[1 << 6]).ToArray();
    readonly ushort[] _specPos = new ushort[NumFullDistances - EndPosModelIndex + 1];
    readonly ushort[] _align = new ushort[1 << NumAlignBits];
    readonly LenDecoder _len = new();
    readonly LenDecoder _repLen = new();
    ushort[] _literal = [];

    int _lc, _lp, _pb;
    bool _haveProps;
    int _state;
    uint _rep0, _rep1, _rep2, _rep3;

    Lzma2Decoder(byte[] output) => _out = output;

    /// <summary>Decodes one LZMA2 stream (ending in a 0x00 control byte) from <paramref name="input"/> at <paramref name="offset"/>, appending to <paramref name="output"/> from <paramref name="outPos"/>; returns the new output position and advances offset past the stream.</summary>
    public static int Decode(ReadOnlySpan<byte> input, ref int offset, byte[] output, int outPos)
    {
        var d = new Lzma2Decoder(output) { _pos = outPos };
        d.Run(input, ref offset);
        return d._pos;
    }

    void Run(ReadOnlySpan<byte> input, ref int offset)
    {
        bool needDictReset = true;
        while (true)
        {
            byte control = Byte(input, offset++);
            if (control == 0x00)
                return;

            if (control < 0x80)
            {
                // 0x01: uncompressed, dictionary reset; 0x02: uncompressed, no reset.
                if (control > 0x02)
                    throw new XipFormatException("invalid LZMA2 control byte");
                if (control == 0x01)
                    needDictReset = false;
                else if (needDictReset)
                    throw new XipFormatException("LZMA2 stream does not start with a dictionary reset");
                int size = ((Byte(input, offset) << 8) | Byte(input, offset + 1)) + 1;
                offset += 2;
                Need(input, offset, size);
                Room(size);
                input.Slice(offset, size).CopyTo(_out.AsSpan(_pos));
                offset += size;
                _pos += size;
                continue;
            }

            int unpacked = ((control & 0x1f) << 16) + (Byte(input, offset) << 8) + Byte(input, offset + 1) + 1;
            int packed = (Byte(input, offset + 2) << 8) + Byte(input, offset + 3) + 1;
            offset += 4;

            int reset = (control >> 5) & 3;   // 0: none, 1: state, 2: state + props, 3: state + props + dict
            if (reset == 3)
                needDictReset = false;
            else if (needDictReset)
                throw new XipFormatException("LZMA2 stream does not start with a dictionary reset");
            if (reset >= 2)
            {
                SetProps(Byte(input, offset++));
            }
            else if (!_haveProps)
            {
                throw new XipFormatException("LZMA2 chunk without properties");
            }
            if (reset >= 1)
                ResetState();

            Need(input, offset, packed);
            Room(unpacked);
            var rc = new RangeDecoder(input.Slice(offset, packed));
            DecodeLzma(ref rc, _pos + unpacked);
            offset += packed;
        }
    }

    void SetProps(byte props)
    {
        if (props >= 9 * 5 * 5)
            throw new XipFormatException("invalid LZMA properties");
        _lc = props % 9;
        props /= 9;
        _lp = props % 5;
        _pb = props / 5;
        if (_lc + _lp > 4)
            throw new XipFormatException("invalid LZMA2 lc/lp");
        _literal = new ushort[0x300 << (_lc + _lp)];
        _haveProps = true;
    }

    void ResetState()
    {
        Init(_isMatch); Init(_isRep); Init(_isRepG0); Init(_isRepG1); Init(_isRepG2); Init(_isRep0Long);
        foreach (var p in _posSlot)
            Init(p);
        Init(_specPos); Init(_align); Init(_literal);
        _len.Reset();
        _repLen.Reset();
        _state = 0;
        _rep0 = _rep1 = _rep2 = _rep3 = 0;
    }

    static void Init(ushort[] probs) => Array.Fill(probs, (ushort)1024);

    void DecodeLzma(ref RangeDecoder rc, int end)
    {
        int pbMask = (1 << _pb) - 1;
        int lpMask = (1 << _lp) - 1;
        while (_pos < end)
        {
            int posState = _pos & pbMask;
            if (rc.Bit(ref _isMatch[(_state << NumPosBitsMax) + posState]) == 0)
            {
                int prev = _pos > 0 ? _out[_pos - 1] : 0;
                int litBase = 0x300 * (((_pos & lpMask) << _lc) + (prev >> (8 - _lc)));
                int symbol = 1;
                if (_state >= 7)
                {
                    int matchByte = Back(_rep0);
                    do
                    {
                        int matchBit = (matchByte >> 7) & 1;
                        matchByte <<= 1;
                        int bit = rc.Bit(ref _literal[litBase + ((1 + matchBit) << 8) + symbol]);
                        symbol = (symbol << 1) | bit;
                        if (matchBit != bit)
                            break;
                    } while (symbol < 0x100);
                }
                while (symbol < 0x100)
                    symbol = (symbol << 1) | rc.Bit(ref _literal[litBase + symbol]);
                _out[_pos++] = (byte)symbol;
                _state = _state < 4 ? 0 : _state < 10 ? _state - 3 : _state - 6;
                continue;
            }

            int len;
            if (rc.Bit(ref _isRep[_state]) == 0)
            {
                _rep3 = _rep2;
                _rep2 = _rep1;
                _rep1 = _rep0;
                len = _len.Decode(ref rc, posState);
                _state = _state < 7 ? 7 : 10;
                _rep0 = DecodeDistance(ref rc, len);
                if (_rep0 == 0xffffffff)
                    throw new XipFormatException("unexpected LZMA end marker in LZMA2 chunk");
            }
            else
            {
                if (rc.Bit(ref _isRepG0[_state]) == 0)
                {
                    if (rc.Bit(ref _isRep0Long[(_state << NumPosBitsMax) + posState]) == 0)
                    {
                        // Short rep: one byte at rep0.
                        _state = _state < 7 ? 9 : 11;
                        if (_pos == 0)
                            throw new XipFormatException("LZMA distance before start of data");
                        _out[_pos] = (byte)Back(_rep0);
                        _pos++;
                        continue;
                    }
                }
                else
                {
                    uint dist;
                    if (rc.Bit(ref _isRepG1[_state]) == 0)
                    {
                        dist = _rep1;
                    }
                    else
                    {
                        if (rc.Bit(ref _isRepG2[_state]) == 0)
                        {
                            dist = _rep2;
                        }
                        else
                        {
                            dist = _rep3;
                            _rep3 = _rep2;
                        }
                        _rep2 = _rep1;
                    }
                    _rep1 = _rep0;
                    _rep0 = dist;
                }
                len = _repLen.Decode(ref rc, posState);
                _state = _state < 7 ? 8 : 11;
            }

            len += MatchMinLen;
            if (_rep0 >= _pos)
                throw new XipFormatException("LZMA distance before start of data");
            if (_pos + len > end)
                throw new XipFormatException("LZMA match runs past the chunk");
            int src = _pos - (int)_rep0 - 1;
            for (int i = 0; i < len; i++)
                _out[_pos++] = _out[src + i];
        }
    }

    int Back(uint distance) => _out[_pos - (int)distance - 1];

    uint DecodeDistance(ref RangeDecoder rc, int len)
    {
        int lenState = Math.Min(len, NumLenToPosStates - 1);
        int posSlot = rc.BitTree(_posSlot[lenState], 6);
        if (posSlot < StartPosModelIndex)
            return (uint)posSlot;

        int numDirectBits = (posSlot >> 1) - 1;
        uint dist = (uint)((2 | (posSlot & 1)) << numDirectBits);
        if (posSlot < EndPosModelIndex)
        {
            dist += rc.ReverseBitTree(_specPos, (int)dist - posSlot, numDirectBits);
        }
        else
        {
            dist += rc.DirectBits(numDirectBits - NumAlignBits) << NumAlignBits;
            dist += rc.ReverseBitTree(_align, 0, NumAlignBits);
        }
        return dist;
    }

    void Room(int n)
    {
        if (_pos + n > _out.Length)
            throw new XipFormatException("LZMA2 stream is larger than its declared size");
    }

    static byte Byte(ReadOnlySpan<byte> input, int at) =>
        at < input.Length ? input[at] : throw new XipFormatException("truncated LZMA2 stream");

    static void Need(ReadOnlySpan<byte> input, int at, int n)
    {
        if (at + n > input.Length)
            throw new XipFormatException("truncated LZMA2 stream");
    }

    sealed class LenDecoder
    {
        ushort _choice, _choice2;
        readonly ushort[][] _low = Enumerable.Range(0, 1 << NumPosBitsMax).Select(_ => new ushort[1 << 3]).ToArray();
        readonly ushort[][] _mid = Enumerable.Range(0, 1 << NumPosBitsMax).Select(_ => new ushort[1 << 3]).ToArray();
        readonly ushort[] _high = new ushort[1 << 8];

        public void Reset()
        {
            _choice = _choice2 = 1024;
            foreach (var p in _low)
                Init(p);
            foreach (var p in _mid)
                Init(p);
            Init(_high);
        }

        public int Decode(ref RangeDecoder rc, int posState)
        {
            if (rc.Bit(ref _choice) == 0)
                return rc.BitTree(_low[posState], 3);
            if (rc.Bit(ref _choice2) == 0)
                return 8 + rc.BitTree(_mid[posState], 3);
            return 16 + rc.BitTree(_high, 8);
        }
    }

    ref struct RangeDecoder
    {
        readonly ReadOnlySpan<byte> _in;
        int _at;
        uint _range, _code;

        public RangeDecoder(ReadOnlySpan<byte> input)
        {
            if (input.Length < 5 || input[0] != 0)
                throw new XipFormatException("corrupt LZMA range coder header");
            _in = input;
            _range = 0xffffffff;
            _code = (uint)((input[1] << 24) | (input[2] << 16) | (input[3] << 8) | input[4]);
            _at = 5;
        }

        byte Next() => _at < _in.Length ? _in[_at++] : throw new XipFormatException("truncated LZMA chunk");

        void Normalize()
        {
            if (_range < (1u << 24))
            {
                _range <<= 8;
                _code = (_code << 8) | Next();
            }
        }

        public int Bit(ref ushort prob)
        {
            uint bound = (_range >> 11) * prob;
            int bit;
            if (_code < bound)
            {
                _range = bound;
                prob += (ushort)((2048 - prob) >> 5);
                bit = 0;
            }
            else
            {
                _range -= bound;
                _code -= bound;
                prob -= (ushort)(prob >> 5);
                bit = 1;
            }
            Normalize();
            return bit;
        }

        public int BitTree(ushort[] probs, int numBits)
        {
            int m = 1;
            for (int i = 0; i < numBits; i++)
                m = (m << 1) + Bit(ref probs[m]);
            return m - (1 << numBits);
        }

        public uint ReverseBitTree(ushort[] probs, int offset, int numBits)
        {
            int m = 1;
            uint symbol = 0;
            for (int i = 0; i < numBits; i++)
            {
                int bit = Bit(ref probs[offset + m]);
                m = (m << 1) + bit;
                symbol |= (uint)bit << i;
            }
            return symbol;
        }

        public uint DirectBits(int numBits)
        {
            uint result = 0;
            for (int i = 0; i < numBits; i++)
            {
                _range >>= 1;
                _code -= _range;
                uint t = 0u - (_code >> 31);
                _code += _range & t;
                Normalize();
                result = (result << 1) + (t + 1);
            }
            return result;
        }
    }
}

public sealed class XipFormatException(string message) : AppleToolException("AotAnywhere: invalid Xcode .xip: " + message);
