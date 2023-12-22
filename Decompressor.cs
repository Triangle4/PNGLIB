using System;
using System.Collections.Generic;
using System.Linq;

public class Decompressor
{
    private readonly byte[] compressedBytes;
    private int byteIndex;
    private int prevBit;
    private byte currentByte;
    private byte[] decompressedBytes;
    private readonly int verbosity;

    private const int MaxBits = 20;

    private static readonly (int, int)[] LENGTHS = new[]
    {
        (0, 3), (0, 4), (0, 5), (0, 6), (0, 7), (0, 8), (0, 9), (0, 10), (1, 11), (1, 13),
        (1, 15), (1, 17), (2, 19), (2, 23), (2, 27), (2, 31), (3, 35), (3, 43), (3, 51), (3, 59),
        (4, 67), (4, 83), (4, 99), (4, 115), (5, 131), (5, 163), (5, 195), (5, 227), (0, 258), (0, 259)
    };

    private static readonly (int, int)[] DISTANCES = new[]
    {
        (0, 1), (0, 2), (0, 3), (0, 4), (1, 5), (1, 7), (2, 9), (2, 13), (3, 17), (3, 25),
        (4, 33), (4, 49), (5, 65), (5, 97), (6, 129), (6, 193), (7, 257), (7, 385), (8, 513), (8, 769),
        (9, 1025), (9, 1537), (10, 2049), (10, 3073), (11, 4097), (11, 6145), (12, 8193), (12, 12289), (13, 16385), (13, 24577), (0, 32769)
    };

    public Decompressor(byte[] bytesArray, int verbosity = 3)
    {
        compressedBytes = bytesArray;
        byteIndex = 0;
        prevBit = 7;
        decompressedBytes = new byte[0];
        this.verbosity = verbosity;
    }

    private void Log(int verbosity, params object[] args)
    {
        if (verbosity <= this.verbosity)
        {
            Console.WriteLine("[inflate] " + string.Join(" ", args));
        }
    }
    private int ReadDataElement(int nbits)
    {
        int value = 0;
        for (int i = 0; i < nbits; i++)
        {
            value |= NextBit() << i;
        }
        return value;
    }
    private int ReadHuffmanBits(int nbits, int prefix = 0)
    {
        int value = prefix;
        for (int i = 0; i < nbits; i++)
        {
            value = (value << 1) | NextBit();
        }
        return value;
    }
    private int NextBit()
    {
        if (prevBit == 7)
        {
            prevBit = 0;
            currentByte = compressedBytes[byteIndex++];
        }
        else
        {
            prevBit++;
        }
        int bit = (currentByte >> prevBit) & 1;
        return bit;
    }
    private int ReadHuffmanSymbol()
    {
        int preview = ReadHuffmanBits(5);
        if (0b00110 <= preview && preview <= 0b10111)
        {
            return ReadHuffmanBits(3, preview) - 0b00110000 + 0;
        }
        else if (0b11001 <= preview && preview <= 0b11111)
        {
            return ReadHuffmanBits(4, preview) - 0b110010000 + 144;
        }
        else if (0b00000 <= preview && preview <= 0b00101)
        {
            return ReadHuffmanBits(2, preview) - 0b00000000 + 256;
        }
        else
        {
            return ReadHuffmanBits(3, preview) - 0b11000000 + 280;
        }
    }
    private List<(string, int, int)> ReadFixedHuffmanBlock()
    {
        List<(string, int, int)> symbols = new List<(string, int, int)>();
        while (true)
        {
            int symbol = ReadHuffmanSymbol();
            if (symbol < 0x100)
            {
                Log(3, $"[fix] Literal: {symbol:x2}");
                symbols.Add(("lit", symbol, 0));
            }
            else if (symbol == 0x100)
            {
                break;
            }
            else
            {
                int ebits, length;
                (ebits, length) = LENGTHS[symbol - 257];
                length += ReadDataElement(ebits);
                (ebits, int distance) = DISTANCES[ReadHuffmanBits(5)];
                distance += ReadDataElement(ebits);
                Log(3, $"[fix] Reference: length={length}, dist={distance}");
                symbols.Add(("ref", length, distance));
            }
        }
        Log(2, "[fix] End of block.");
        return symbols;
    }
    private int ReadDynamicHuffmanSymbol(Dictionary<int, int>[] tree)
    {
        int value = 0;
        for (int i = 1; i < MaxBits; i++)
        {
            value = (value << 1) | NextBit();
            if (tree[i].TryGetValue(value, out int symbol))
            {
                return symbol;
            }
        }
        throw new Exception("This is not supposed to happen");
    }
    private List<int> ReadCompressedCodelengths(Dictionary<int, int>[] tree, int num)
    {
        List<int> codelengths = new List<int>();
        while (codelengths.Count < num)
        {
            int symbol = ReadDynamicHuffmanSymbol(tree);
            if (symbol <= 15)
            {
                codelengths.Add(symbol);
            }
            else if (symbol == 16)
            {
                codelengths.AddRange(Enumerable.Repeat(codelengths.Last(), 3 + ReadDataElement(2)));
            }
            else if (symbol == 17)
            {
                codelengths.AddRange(Enumerable.Repeat(0, 3 + ReadDataElement(3)));
            }
            else if (symbol == 18)
            {
                codelengths.AddRange(Enumerable.Repeat(0, 11 + ReadDataElement(7)));
            }
        }
        if (codelengths.Count != num)
        {
            throw new Exception("Invalid number of codelengths");
        }
        return codelengths;
    }
    private List<(string, int, int)> ReadDynamicHuffmanBlock()
    {
        int hlit = ReadDataElement(5) + 257;
        int hdist = ReadDataElement(5) + 1;
        int hclen = ReadDataElement(4) + 4;

        int[] codelengthsAlphabetLengths = new int[19];
        int[] foo = { 16, 17, 18, 0, 8, 7, 9, 6, 10, 5, 11, 4, 12, 3, 13, 2, 14, 1, 15 };
        foreach (int x in foo.Take(hclen))
        {
            codelengthsAlphabetLengths[x] = ReadDataElement(3);
        }

        Dictionary<int, int>[] codelengthsTree = CodeLengthsToCodes(codelengthsAlphabetLengths);

        Log(2, "[dyn] Codelengths tree:", codelengthsTree);

        int [] litlenCodelengths = ReadCompressedCodelengths(codelengthsTree, hlit).ToArray();
        int [] distCodelengths = ReadCompressedCodelengths(codelengthsTree, hdist).ToArray();
        Dictionary<int, int>[] litlenTree = CodeLengthsToCodes(litlenCodelengths);
        Dictionary<int, int>[] distTree = CodeLengthsToCodes(distCodelengths);

        Log(2, "[dyn] Length tree:", litlenTree);
        Log(2, "[dyn] Distance tree:", distTree);

        List<(string, int, int)> symbols = new List<(string, int, int)>();
        while (true)
        {
            int symbol = ReadDynamicHuffmanSymbol(litlenTree);
            if (symbol < 0x100)
            {
                Log(3, $"[dyn] Literal: {symbol:x2}");
                symbols.Add(("lit", symbol, 0));
            }
            else if (symbol == 0x100)
            {
                break;
            }
            else
            {
                int ebits, length;
                (ebits, length) = LENGTHS[symbol - 257];
                length += ReadDataElement(ebits);

                int distanceSymbol = ReadDynamicHuffmanSymbol(distTree);
                (ebits, int distance) = DISTANCES[distanceSymbol];
                distance += ReadDataElement(ebits);

                Log(3, $"[dyn] Reference: length={length}, dist={distance}");
                symbols.Add(("ref", length, distance));
            }
        }
        Log(2, "[dyn] End of block.");
        return symbols;
    }
    private void ProcessSymbols(List<(string, int, int)> symbols)
    {
        foreach ((string type, int arg1, int arg2) in symbols)
        {
            if (type == "lit")
            {
                decompressedBytes = decompressedBytes.Concat(new byte[] { (byte)arg1 }).ToArray();
            }
            else if (type == "ref")
            {
                for (int i = 0; i < arg1; i++)
                {
                    decompressedBytes = decompressedBytes.Concat(new byte[] { decompressedBytes[^arg2] }).ToArray();
                }
            }
            else
            {
                throw new Exception("Unexpected symbol");
            }
        }
    }

    private bool ReadBlock()
    {
        bool bfinal = NextBit() == 1;
        int btype = ReadDataElement(2);

        if (bfinal)
        {
            Log(1, "This is the final block!");
        }

        if (btype == 0b00)
        {
            Log(1, "Parsing Non-compressed block (BTYPE=00)");
            while (prevBit != 7)
            {
                int bit = NextBit();
                if (bit != 0)
                {
                    throw new Exception("Invalid non-compressed block" + bit + " " + compressedBytes[byteIndex]);
                }
            }
            int size = ReadDataElement(16);
            int notsize = ReadDataElement(16);
            if (size != (notsize ^ 0xffff))
            {
                throw new Exception("Invalid non-compressed block size");
            }
            decompressedBytes = decompressedBytes.Concat(compressedBytes.Skip(byteIndex).Take(size)).ToArray();
            Log(2, $"[non] Read {size} non-compressed bytes");
            Log(2, "[non] End of block.");
        }
        else if (btype == 0b01)
        {
            Log(1, "Parsing fixed Huffman coded block (BTYPE=01)");
            ProcessSymbols(ReadFixedHuffmanBlock());
        }
        else if (btype == 0b10)
        {
            Log(1, "Parsing dynamic Huffman coded block (BTYPE=10)");
            ProcessSymbols(ReadDynamicHuffmanBlock());
        }
        else
        {
            throw new Exception("Invalid block type (BTYPE=11)");
        }

        return bfinal;
    }

    public byte[] Inflate()
    {
        while (!ReadBlock()){}
        return decompressedBytes;
    }

    private static Dictionary<int, int>[] CodeLengthsToCodes(int[] codelengths)
    {
        int[] blCount = Enumerable.Range(0, MaxBits).Select(i => codelengths.Count(x => x == i)).ToArray();
        Dictionary<int, int>[] tree = new Dictionary<int, int>[codelengths.Length];
        int[] nextCode = new int[MaxBits];
        int code = 0;
        blCount[0] = 0;

        for (int bits = 1; bits < MaxBits; bits++)
        {
            code = (code + blCount[bits - 1]) << 1;
            nextCode[bits] = code;
        }

        for (int n = 0; n < codelengths.Length; n++)
        {
            int length = codelengths[n];
            if (length > 0)
            {
                tree[n] = new Dictionary<int, int>();
                tree[n][nextCode[length]] = n;
                nextCode[length]++;
            }
        }
        return tree;
    }
}

