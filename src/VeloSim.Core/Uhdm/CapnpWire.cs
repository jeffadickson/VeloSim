using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace VeloSim.Uhdm;

/// <summary>
/// Pure .NET 11 Cap'n Proto packed stream encoder and decoder (zero unsafe, NativeAOT-clean).
/// Implements the standard Cap'n Proto packing specification used by UHDM Serializer::Save/Restore.
/// </summary>
public static class CapnpPackedStream
{
    public const int DefaultMaxUnpackedBytes = 128 * 1024 * 1024;

    public static byte[] Unpack(ReadOnlySpan<byte> packed, int maxUnpackedBytes = DefaultMaxUnpackedBytes)
    {
        if (maxUnpackedBytes <= 0 || maxUnpackedBytes > 512 * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(maxUnpackedBytes));
        if (packed.Length == 0)
            throw new InvalidDataException("Empty Cap'n Proto packed stream.");

        int initialCap = (int)Math.Min((long)Math.Max(packed.Length * 4, 256), maxUnpackedBytes);
        byte[] buffer = GC.AllocateUninitializedArray<byte>(initialCap);
        int outPos = 0;
        int i = 0;

        while (i < packed.Length)
        {
            byte tag = packed[i++];
            if (tag == 0x00)
            {
                if (i >= packed.Length)
                    throw new InvalidDataException("Truncated Cap'n Proto zero-word run count.");
                int addBytes = (packed[i++] + 1) * 8;
                EnsureCapacity(ref buffer, outPos, addBytes, maxUnpackedBytes);
                buffer.AsSpan(outPos, addBytes).Clear();
                outPos += addBytes;
            }
            else if (tag == 0xFF)
            {
                if (i + 8 >= packed.Length)
                    throw new InvalidDataException("Truncated Cap'n Proto verbatim word or count.");
                int extraWords = packed[i + 8];
                int totalBytes = (1 + extraWords) * 8;
                if (i + 9 + extraWords * 8 > packed.Length)
                    throw new InvalidDataException("Truncated Cap'n Proto verbatim run payload.");
                EnsureCapacity(ref buffer, outPos, totalBytes, maxUnpackedBytes);
                packed.Slice(i, 8).CopyTo(buffer.AsSpan(outPos, 8));
                i += 9;
                outPos += 8;
                if (extraWords > 0)
                {
                    int extraBytes = extraWords * 8;
                    packed.Slice(i, extraBytes).CopyTo(buffer.AsSpan(outPos, extraBytes));
                    i += extraBytes;
                    outPos += extraBytes;
                }
            }
            else
            {
                EnsureCapacity(ref buffer, outPos, 8, maxUnpackedBytes);
                int remaining = packed.Length - i;
                if (remaining < System.Numerics.BitOperations.PopCount(tag))
                    throw new InvalidDataException("Truncated Cap'n Proto packed word.");
                buffer[outPos + 0] = (tag & 0x01) != 0 ? packed[i++] : (byte)0;
                buffer[outPos + 1] = (tag & 0x02) != 0 ? packed[i++] : (byte)0;
                buffer[outPos + 2] = (tag & 0x04) != 0 ? packed[i++] : (byte)0;
                buffer[outPos + 3] = (tag & 0x08) != 0 ? packed[i++] : (byte)0;
                buffer[outPos + 4] = (tag & 0x10) != 0 ? packed[i++] : (byte)0;
                buffer[outPos + 5] = (tag & 0x20) != 0 ? packed[i++] : (byte)0;
                buffer[outPos + 6] = (tag & 0x40) != 0 ? packed[i++] : (byte)0;
                buffer[outPos + 7] = (tag & 0x80) != 0 ? packed[i++] : (byte)0;
                outPos += 8;
            }
        }

        if (outPos == buffer.Length) return buffer;
        byte[] exact = GC.AllocateUninitializedArray<byte>(outPos);
        Buffer.BlockCopy(buffer, 0, exact, 0, outPos);
        return exact;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void EnsureCapacity(ref byte[] buffer, int outPos, int addBytes, int maxUnpackedBytes)
    {
        long needed = (long)outPos + addBytes;
        if (needed > maxUnpackedBytes)
            throw new InvalidDataException("Cap'n Proto unpacked stream exceeds size limit.");
        if (needed <= buffer.Length) return;
        long next = Math.Max(needed, (long)buffer.Length * 2);
        int newLen = (int)Math.Min(next, maxUnpackedBytes);
        byte[] grown = GC.AllocateUninitializedArray<byte>(newLen);
        Buffer.BlockCopy(buffer, 0, grown, 0, outPos);
        buffer = grown;
    }

    public static byte[] Pack(ReadOnlySpan<byte> unpacked)
    {
        if (unpacked.Length == 0 || (unpacked.Length & 7) != 0)
            throw new ArgumentException("Unpacked Cap'n Proto message must be a non-empty multiple of 8 bytes.", nameof(unpacked));

        var output = new MemoryStream(unpacked.Length);
        int wordCount = unpacked.Length / 8;
        int w = 0;

        while (w < wordCount)
        {
            var slice = unpacked.Slice(w * 8, 8);
            byte tag = ComputeTag(slice);
            output.WriteByte(tag);

            if (tag == 0x00)
            {
                int run = 0;
                w++;
                while (w < wordCount && run < 255 && ComputeTag(unpacked.Slice(w * 8, 8)) == 0x00)
                {
                    run++;
                    w++;
                }
                output.WriteByte((byte)run);
            }
            else if (tag == 0xFF)
            {
                output.Write(slice);
                w++;
                int start = w;
                int run = 0;
                while (w < wordCount && run < 255 && PopCount(ComputeTag(unpacked.Slice(w * 8, 8))) >= 6)
                {
                    run++;
                    w++;
                }
                output.WriteByte((byte)run);
                if (run > 0)
                    output.Write(unpacked.Slice(start * 8, run * 8));
            }
            else
            {
                for (int b = 0; b < 8; b++)
                {
                    if ((tag & (1 << b)) != 0)
                        output.WriteByte(slice[b]);
                }
                w++;
            }
        }

        return output.ToArray();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte ComputeTag(ReadOnlySpan<byte> word)
    {
        byte tag = 0;
        if (word[0] != 0) tag |= 0x01;
        if (word[1] != 0) tag |= 0x02;
        if (word[2] != 0) tag |= 0x04;
        if (word[3] != 0) tag |= 0x08;
        if (word[4] != 0) tag |= 0x10;
        if (word[5] != 0) tag |= 0x20;
        if (word[6] != 0) tag |= 0x40;
        if (word[7] != 0) tag |= 0x80;
        return tag;
    }

    private static int PopCount(byte value) => System.Numerics.BitOperations.PopCount(value);
}

[InlineArray(8)]
internal struct InlineSegmentInt8
{
    private int element0;
}

/// <summary>
/// Pure safe .NET 11 Cap'n Proto binary message reader with strict traversal and nesting limits.
/// Uses an <see cref="InlineArrayAttribute"/> inline segment table for &lt;= 8 segments (zero heap array overhead).
/// </summary>
public sealed class CapnpMessageReader
{
    public const long DefaultTraversalLimitWords = 64L * 1024 * 1024;
    public const int DefaultNestingLimit = 128;

    private readonly byte[] buffer;
    private readonly int segmentCount;
    private InlineSegmentInt8 inlineOffsets;
    private InlineSegmentInt8 inlineWordLengths;
    private readonly int[]? overflowOffsets;
    private readonly int[]? overflowWordLengths;
    private long remainingWords;
    private readonly int nestingLimit;

    private CapnpMessageReader(byte[] buffer, int segmentCount, InlineSegmentInt8 inlineOffsets, InlineSegmentInt8 inlineWordLengths,
        int[]? overflowOffsets, int[]? overflowWordLengths, long traversalLimitWords, int nestingLimit)
    {
        this.buffer = buffer;
        this.segmentCount = segmentCount;
        this.inlineOffsets = inlineOffsets;
        this.inlineWordLengths = inlineWordLengths;
        this.overflowOffsets = overflowOffsets;
        this.overflowWordLengths = overflowWordLengths;
        remainingWords = traversalLimitWords;
        this.nestingLimit = nestingLimit;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int SegmentOffset(int segment) => overflowOffsets is null ? inlineOffsets[segment] : overflowOffsets[segment];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int SegmentWordLength(int segment) => overflowWordLengths is null ? inlineWordLengths[segment] : overflowWordLengths[segment];

    public static CapnpMessageReader FromPacked(ReadOnlySpan<byte> packed, int maxUnpackedBytes = CapnpPackedStream.DefaultMaxUnpackedBytes,
        long traversalLimitWords = DefaultTraversalLimitWords, int nestingLimit = DefaultNestingLimit)
    {
        byte[] unpacked = CapnpPackedStream.Unpack(packed, maxUnpackedBytes);
        return FromUnpacked(unpacked, traversalLimitWords, nestingLimit);
    }

    public static CapnpMessageReader FromUnpacked(byte[] unpacked, long traversalLimitWords = DefaultTraversalLimitWords, int nestingLimit = DefaultNestingLimit)
    {
        ArgumentNullException.ThrowIfNull(unpacked);
        if (traversalLimitWords <= 0) throw new ArgumentOutOfRangeException(nameof(traversalLimitWords));
        if (nestingLimit is <= 0 or > 1024) throw new ArgumentOutOfRangeException(nameof(nestingLimit));
        if (unpacked.Length < 8 || (unpacked.Length & 7) != 0)
            throw new InvalidDataException("Cap'n Proto message must be at least 8 bytes and word-aligned.");

        uint segmentCountMinusOne = BinaryPrimitives.ReadUInt32LittleEndian(unpacked.AsSpan(0, 4));
        if (segmentCountMinusOne >= 4096)
            throw new InvalidDataException("Cap'n Proto segment count exceeds limit.");
        int segCount = (int)segmentCountMinusOne + 1;

        long headerInt32Count = 1L + segCount;
        long headerBytes = ((headerInt32Count + 1) / 2) * 8;
        if (headerBytes > unpacked.Length)
            throw new InvalidDataException("Truncated Cap'n Proto segment table.");

        InlineSegmentInt8 inOff = default;
        InlineSegmentInt8 inLen = default;
        int[]? ovOff = segCount > 8 ? new int[segCount] : null;
        int[]? ovLen = segCount > 8 ? new int[segCount] : null;

        long cursor = headerBytes;
        for (int s = 0; s < segCount; s++)
        {
            uint words = BinaryPrimitives.ReadUInt32LittleEndian(unpacked.AsSpan(4 + s * 4, 4));
            long byteLen = (long)words * 8;
            if (words > int.MaxValue / 8 || cursor + byteLen > unpacked.Length)
                throw new InvalidDataException($"Cap'n Proto segment {s} exceeds message bounds.");
            if (ovOff is null)
            {
                inOff[s] = (int)cursor;
                inLen[s] = (int)words;
            }
            else
            {
                ovOff[s] = (int)cursor;
                ovLen![s] = (int)words;
            }
            cursor += byteLen;
        }

        int seg0Words = ovLen is null ? inLen[0] : ovLen[0];
        if (segCount == 0 || seg0Words < 1)
            throw new InvalidDataException("Cap'n Proto root segment is empty.");

        return new CapnpMessageReader(unpacked, segCount, inOff, inLen, ovOff, ovLen, traversalLimitWords, nestingLimit);
    }

    public CapnpStruct GetRootStruct()
    {
        return ReadStructAtPointer(0, 0, 0);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ulong ReadWord(int segment, int wordIndex)
    {
        if ((uint)segment >= (uint)segmentCount)
            throw new InvalidDataException("Cap'n Proto segment index out of range.");
        if ((uint)wordIndex >= (uint)SegmentWordLength(segment))
            throw new InvalidDataException("Cap'n Proto word index out of range.");
        int byteOffset = SegmentOffset(segment) + wordIndex * 8;
        return BinaryPrimitives.ReadUInt64LittleEndian(buffer.AsSpan(byteOffset, 8));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ReadOnlySpan<byte> SliceBytes(int segment, int startWord, int byteLength)
    {
        if ((uint)segment >= (uint)segmentCount)
            throw new InvalidDataException("Cap'n Proto segment index out of range.");
        if (startWord < 0 || byteLength < 0)
            throw new InvalidDataException("Cap'n Proto negative slice offset or length.");
        long segBytes = (long)SegmentWordLength(segment) * 8;
        long startByte = (long)startWord * 8;
        if (startByte + byteLength > segBytes)
            throw new InvalidDataException("Cap'n Proto slice exceeds segment bounds.");
        return buffer.AsSpan(SegmentOffset(segment) + (int)startByte, byteLength);
    }

    internal void AccountWords(long words)
    {
        if (words < 0) throw new InvalidDataException("Negative Cap'n Proto word count.");
        remainingWords -= Math.Max(1, words);
        if (remainingWords < 0)
            throw new InvalidDataException("Cap'n Proto traversal word budget exceeded.");
    }

    private (int Segment, int TargetWord, ulong PointerWord) ResolveFar(int segment, int pointerWordIndex)
    {
        ulong raw = ReadWord(segment, pointerWordIndex);
        int kind = (int)(raw & 0x3);
        if (kind != 2)
            return (segment, pointerWordIndex, raw);

        bool isDoubleFar = ((raw >> 2) & 1) != 0;
        int padOffset = (int)((raw >> 3) & 0x1FFFFFFF);
        int targetSegment = (int)(raw >> 32);
        AccountWords(1);

        if (!isDoubleFar)
        {
            ulong landing = ReadWord(targetSegment, padOffset);
            if ((landing & 0x3) == 2)
                throw new InvalidDataException("Nested Cap'n Proto far pointer is invalid.");
            return (targetSegment, padOffset, landing);
        }

        ulong farToContent = ReadWord(targetSegment, padOffset);
        ulong tagWord = ReadWord(targetSegment, padOffset + 1);
        if ((farToContent & 0x3) != 2 || ((farToContent >> 2) & 1) != 0)
            throw new InvalidDataException("Invalid Cap'n Proto double-far landing pad.");
        int contentWord = (int)((farToContent >> 3) & 0x1FFFFFFF);
        int contentSegment = (int)(farToContent >> 32);
        // Synthesize a pointer anchored at contentWord - 1 with offset 0 so callers compute target = contentWord.
        ulong zeroOffsetTag = (tagWord & ~0xFFFFFFFCUL);
        return (contentSegment, contentWord - 1, zeroOffsetTag);
    }

    internal CapnpStruct ReadStructAtPointer(int segment, int pointerWordIndex, int depth)
    {
        if (depth >= nestingLimit)
            throw new InvalidDataException("Cap'n Proto nesting limit exceeded.");
        var (seg, ptrWordIdx, raw) = ResolveFar(segment, pointerWordIndex);
        if (raw == 0)
            return CapnpStruct.Empty(this, depth + 1);
        if ((raw & 0x3) != 0)
            throw new InvalidDataException($"Expected Cap'n Proto struct pointer, got kind {raw & 0x3}.");

        int offset = ((int)(uint)raw) >> 2;
        int dataWords = (ushort)(raw >> 32);
        int pointerCount = (ushort)(raw >> 48);
        int targetWord = checked(ptrWordIdx + 1 + offset);
        int totalWords = dataWords + pointerCount;
        if (targetWord < 0 || targetWord + totalWords > SegmentWordLength(seg))
            throw new InvalidDataException("Cap'n Proto struct extends outside segment bounds.");
        AccountWords(totalWords);
        return new CapnpStruct(this, seg, targetWord, dataWords, pointerCount, depth + 1);
    }

    internal CapnpList ReadListAtPointer(int segment, int pointerWordIndex, int depth)
    {
        if (depth >= nestingLimit)
            throw new InvalidDataException("Cap'n Proto nesting limit exceeded.");
        var (seg, ptrWordIdx, raw) = ResolveFar(segment, pointerWordIndex);
        if (raw == 0)
            return CapnpList.Empty(this, depth + 1);
        if ((raw & 0x3) != 1)
            throw new InvalidDataException($"Expected Cap'n Proto list pointer, got kind {raw & 0x3}.");

        int offset = ((int)(uint)raw) >> 2;
        int elementSize = (int)((raw >> 32) & 0x7);
        int elementCount = (int)((raw >> 35) & 0x1FFFFFFF);
        int targetWord = checked(ptrWordIdx + 1 + offset);

        if (elementSize == 7)
        {
            // Composite list: targetWord is a struct tag word whose offset field holds the element count.
            ulong tag = ReadWord(seg, targetWord);
            if ((tag & 0x3) != 0)
                throw new InvalidDataException("Invalid Cap'n Proto composite list tag word.");
            int count = (int)((tag >> 2) & 0x3FFFFFFF);
            int dataWords = (ushort)(tag >> 32);
            int pointerCount = (ushort)(tag >> 48);
            long wordsRequired = (long)count * (dataWords + pointerCount);
            if (wordsRequired != elementCount)
                throw new InvalidDataException("Cap'n Proto composite list word count mismatch.");
            int firstElementWord = targetWord + 1;
            if (firstElementWord < 0 || firstElementWord + wordsRequired > SegmentWordLength(seg))
                throw new InvalidDataException("Cap'n Proto composite list exceeds segment bounds.");
            AccountWords(1 + wordsRequired);
            return new CapnpList(this, seg, firstElementWord, elementSize, count, dataWords, pointerCount, depth + 1);
        }

        long listWords = elementSize switch
        {
            0 => 0,
            1 => ((long)elementCount + 63) / 64,
            2 => ((long)elementCount + 7) / 8,
            3 => ((long)elementCount + 3) / 4,
            4 => ((long)elementCount + 1) / 2,
            5 => elementCount,
            6 => elementCount,
            _ => throw new InvalidDataException("Unknown Cap'n Proto list element size.")
        };
        if (targetWord < 0 || targetWord + listWords > SegmentWordLength(seg))
            throw new InvalidDataException("Cap'n Proto list exceeds segment bounds.");
        AccountWords(listWords);
        return new CapnpList(this, seg, targetWord, elementSize, elementCount, 0, 0, depth + 1);
    }
}

public readonly struct CapnpStruct
{
    private readonly CapnpMessageReader? reader;
    private readonly int segment;
    private readonly int dataWordStart;
    public int DataWords { get; }
    public int PointerCount { get; }
    private readonly int depth;
    public bool IsNull => reader is null || (DataWords == 0 && PointerCount == 0 && dataWordStart < 0);

    internal CapnpStruct(CapnpMessageReader reader, int segment, int dataWordStart, int dataWords, int pointerCount, int depth)
    {
        this.reader = reader;
        this.segment = segment;
        this.dataWordStart = dataWordStart;
        DataWords = dataWords;
        PointerCount = pointerCount;
        this.depth = depth;
    }

    internal static CapnpStruct Empty(CapnpMessageReader reader, int depth) => new(reader, 0, -1, 0, 0, depth);

    public bool ReadBool(int bitOffset)
    {
        if (reader is null || bitOffset < 0) return false;
        int word = bitOffset >> 6;
        if (word >= DataWords) return false;
        ulong w = reader.ReadWord(segment, dataWordStart + word);
        return ((w >> (bitOffset & 63)) & 1UL) != 0;
    }

    public ushort ReadUInt16(int byteOffset)
    {
        if (reader is null || byteOffset < 0 || byteOffset + 2 > DataWords * 8) return 0;
        var span = reader.SliceBytes(segment, dataWordStart, DataWords * 8);
        return BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(byteOffset, 2));
    }

    public uint ReadUInt32(int byteOffset)
    {
        if (reader is null || byteOffset < 0 || byteOffset + 4 > DataWords * 8) return 0;
        var span = reader.SliceBytes(segment, dataWordStart, DataWords * 8);
        return BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(byteOffset, 4));
    }

    public ulong ReadUInt64(int byteOffset)
    {
        if (reader is null || byteOffset < 0 || byteOffset + 8 > DataWords * 8) return 0;
        var span = reader.SliceBytes(segment, dataWordStart, DataWords * 8);
        return BinaryPrimitives.ReadUInt64LittleEndian(span.Slice(byteOffset, 8));
    }

    public long ReadInt64(int byteOffset) => unchecked((long)ReadUInt64(byteOffset));

    public CapnpStruct ReadStruct(int pointerIndex)
    {
        if (reader is null || pointerIndex < 0 || pointerIndex >= PointerCount)
            return reader is null ? default : Empty(reader, depth + 1);
        return reader.ReadStructAtPointer(segment, dataWordStart + DataWords + pointerIndex, depth);
    }

    public CapnpList ReadList(int pointerIndex)
    {
        if (reader is null || pointerIndex < 0 || pointerIndex >= PointerCount)
            return reader is null ? default : CapnpList.Empty(reader, depth + 1);
        return reader.ReadListAtPointer(segment, dataWordStart + DataWords + pointerIndex, depth);
    }

    public string ReadText(int pointerIndex) => ReadList(pointerIndex).AsText();
}

public readonly struct CapnpList
{
    private readonly CapnpMessageReader? reader;
    private readonly int segment;
    private readonly int firstWord;
    private readonly int elementSize;
    private readonly int structDataWords;
    private readonly int structPointerCount;
    private readonly int depth;

    public int Count { get; }

    internal CapnpList(CapnpMessageReader reader, int segment, int firstWord, int elementSize, int count, int structDataWords, int structPointerCount, int depth)
    {
        this.reader = reader;
        this.segment = segment;
        this.firstWord = firstWord;
        this.elementSize = elementSize;
        Count = count;
        this.structDataWords = structDataWords;
        this.structPointerCount = structPointerCount;
        this.depth = depth;
    }

    internal static CapnpList Empty(CapnpMessageReader reader, int depth) => new(reader, 0, 0, 0, 0, 0, 0, depth);

    public string AsText()
    {
        if (reader is null || Count == 0) return string.Empty;
        if (elementSize != 2)
            throw new InvalidDataException($"Expected byte list for Cap'n Proto Text, got element size {elementSize}.");
        var bytes = reader.SliceBytes(segment, firstWord, Count);
        int len = bytes.Length > 0 && bytes[^1] == 0 ? bytes.Length - 1 : bytes.Length;
        return Encoding.UTF8.GetString(bytes.Slice(0, len));
    }

    public ulong ReadUInt64(int index)
    {
        if (reader is null || (uint)index >= (uint)Count)
            throw new ArgumentOutOfRangeException(nameof(index));
        if (elementSize != 5)
            throw new InvalidDataException($"Expected 8-byte Cap'n Proto list, got element size {elementSize}.");
        return reader.ReadWord(segment, firstWord + index);
    }

    public string ReadText(int index)
    {
        if (reader is null || (uint)index >= (uint)Count)
            throw new ArgumentOutOfRangeException(nameof(index));
        if (elementSize != 6)
            throw new InvalidDataException($"Expected pointer list for List(Text), got element size {elementSize}.");
        return reader.ReadListAtPointer(segment, firstWord + index, depth).AsText();
    }

    public CapnpStruct ReadStruct(int index)
    {
        if (reader is null || (uint)index >= (uint)Count)
            throw new ArgumentOutOfRangeException(nameof(index));
        if (elementSize == 7)
        {
            int stride = structDataWords + structPointerCount;
            return new CapnpStruct(reader, segment, firstWord + index * stride, structDataWords, structPointerCount, depth);
        }
        if (elementSize == 6)
            return reader.ReadStructAtPointer(segment, firstWord + index, depth);
        if (elementSize == 0)
            return CapnpStruct.Empty(reader, depth);
        if (elementSize == 5)
            return new CapnpStruct(reader, segment, firstWord + index, 1, 0, depth);
        throw new InvalidDataException($"Unsupported Cap'n Proto struct list element size {elementSize}.");
    }
}

/// <summary>
/// Pure safe .NET 11 single-segment Cap'n Proto binary message writer for serializing UHDM databases.
/// </summary>
public sealed class CapnpMessageWriter
{
    private readonly List<ulong> words = [0UL]; // Word 0 is the root struct pointer.

    public int AllocateStruct(int pointerWordIndex, int dataWords, int pointerCount)
    {
        int startWord = words.Count;
        int totalWords = dataWords + pointerCount;
        for (int i = 0; i < totalWords; i++)
            words.Add(0UL);
        WriteStructPointer(pointerWordIndex, startWord, dataWords, pointerCount);
        return startWord;
    }

    public int AllocateCompositeStructList(int pointerWordIndex, int count, int dataWords, int pointerCount)
    {
        if (count == 0)
        {
            words[pointerWordIndex] = 0UL;
            return -1;
        }
        int tagWordIndex = words.Count;
        int totalElementWords = checked(count * (dataWords + pointerCount));
        ulong tag = ((ulong)(uint)count << 2) | ((ulong)(ushort)dataWords << 32) | ((ulong)(ushort)pointerCount << 48);
        words.Add(tag);
        for (int i = 0; i < totalElementWords; i++)
            words.Add(0UL);
        WriteListPointer(pointerWordIndex, tagWordIndex, 7, totalElementWords);
        return tagWordIndex + 1;
    }

    public int AllocateUInt64List(int pointerWordIndex, ReadOnlySpan<ulong> values)
    {
        if (values.Length == 0)
        {
            words[pointerWordIndex] = 0UL;
            return -1;
        }
        int startWord = words.Count;
        foreach (ulong v in values)
            words.Add(v);
        WriteListPointer(pointerWordIndex, startWord, 5, values.Length);
        return startWord;
    }

    public int AllocatePointerList(int pointerWordIndex, int count)
    {
        if (count == 0)
        {
            words[pointerWordIndex] = 0UL;
            return -1;
        }
        int startWord = words.Count;
        for (int i = 0; i < count; i++)
            words.Add(0UL);
        WriteListPointer(pointerWordIndex, startWord, 6, count);
        return startWord;
    }

    public void WriteText(int pointerWordIndex, string text)
    {
        byte[] utf8 = Encoding.UTF8.GetBytes(text);
        int byteCount = utf8.Length + 1; // Trailing NUL byte required by Cap'n Proto Text.
        int wordCount = (byteCount + 7) / 8;
        int startWord = words.Count;
        Span<byte> buf = stackalloc byte[8];
        for (int w = 0; w < wordCount; w++)
        {
            buf.Clear();
            int srcOffset = w * 8;
            int copy = Math.Clamp(utf8.Length - srcOffset, 0, 8);
            if (copy > 0)
                utf8.AsSpan(srcOffset, copy).CopyTo(buf);
            words.Add(BinaryPrimitives.ReadUInt64LittleEndian(buf));
        }
        WriteListPointer(pointerWordIndex, startWord, 2, byteCount);
    }

    public void SetBool(int structDataWordStart, int bitOffset, bool value)
    {
        if (!value) return;
        int wordIdx = structDataWordStart + (bitOffset >> 6);
        words[wordIdx] |= 1UL << (bitOffset & 63);
    }

    public void SetUInt16(int structDataWordStart, int byteOffset, ushort value)
    {
        int wordIdx = structDataWordStart + (byteOffset >> 3);
        int shift = (byteOffset & 7) * 8;
        words[wordIdx] = (words[wordIdx] & ~(0xFFFFUL << shift)) | ((ulong)value << shift);
    }

    public void SetUInt32(int structDataWordStart, int byteOffset, uint value)
    {
        int wordIdx = structDataWordStart + (byteOffset >> 3);
        int shift = (byteOffset & 7) * 8;
        words[wordIdx] = (words[wordIdx] & ~(0xFFFFFFFFUL << shift)) | ((ulong)value << shift);
    }

    public void SetUInt64(int structDataWordStart, int byteOffset, ulong value)
    {
        int wordIdx = structDataWordStart + (byteOffset >> 3);
        words[wordIdx] = value;
    }

    public void SetInt64(int structDataWordStart, int byteOffset, long value) =>
        SetUInt64(structDataWordStart, byteOffset, unchecked((ulong)value));

    private void WriteStructPointer(int pointerWordIndex, int targetWord, int dataWords, int pointerCount)
    {
        int offset = targetWord - (pointerWordIndex + 1);
        ulong low = (uint)(offset << 2);
        words[pointerWordIndex] = low | ((ulong)(ushort)dataWords << 32) | ((ulong)(ushort)pointerCount << 48);
    }

    private void WriteListPointer(int pointerWordIndex, int targetWord, int elementSize, int elementCount)
    {
        int offset = targetWord - (pointerWordIndex + 1);
        ulong low = 1UL | (uint)(offset << 2);
        words[pointerWordIndex] = low | ((ulong)(uint)elementSize << 32) | ((ulong)(uint)elementCount << 35);
    }

    public byte[] ToUnpackedBytes()
    {
        var span = CollectionsMarshal.AsSpan(words);
        byte[] result = GC.AllocateUninitializedArray<byte>((1 + span.Length) * 8);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(0, 4), 0U);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4, 4), (uint)span.Length);
        for (int i = 0; i < span.Length; i++)
            BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan((1 + i) * 8, 8), span[i]);
        return result;
    }

    public byte[] ToPackedBytes() => CapnpPackedStream.Pack(ToUnpackedBytes());
}
