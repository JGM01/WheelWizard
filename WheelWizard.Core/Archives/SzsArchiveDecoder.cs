using WheelWizard.Core.Helpers;

namespace WheelWizard.Core.Archives;

public sealed class SzsArchiveDecoder : ISzsArchiveDecoder
{
    private const uint U8Magic = 0x55aa382d;

    public OperationResult<DecodedArchive> TryDecodeU8Archive(byte[] bytes)
    {
        try
        {
            var decompressResult = DecompressYaz0IfNeeded(bytes);
            if (decompressResult.IsFailure)
                return decompressResult.Error;

            var raw = decompressResult.Value;

            if (raw.Length < 8)
                return new OperationError { Message = "The provided file is too small to contain a valid U8 archive header." };
            if (BigEndianBinaryHelper.BufferToUint32(raw, 0) != U8Magic)
                return new OperationError { Message = "The provided file is not a valid Yaz0/U8 archive." };

            return ParseU8Archive(raw);
        }
        catch (Exception ex)
        {
            return new OperationError { Message = $"Failed to decode U8 archive: {ex.Message}", Exception = ex };
        }
    }

    public OperationResult<byte[]> DecompressYaz0IfNeeded(byte[] bytes)
    {
        try
        {
            if (bytes.Length < 4)
                return bytes;

            if (BinaryStringHelper.ReadAscii(bytes, 0, 4) != "Yaz0")
                return bytes;

            if (bytes.Length < 16)
                return new OperationError { Message = "Yaz0 header is truncated." };

            var outputSize = checked((int)BigEndianBinaryHelper.BufferToUint32(bytes, 4));
            // Even a maximal Yaz0 run expands by at most 273 bytes per three input bytes.
            // Reject impossible declarations before allocating from an untrusted header.
            if (outputSize > (long)(bytes.Length - 16) * 91)
                throw new InvalidDataException($"Impossible Yaz0 output size {outputSize} for {bytes.Length} input bytes.");
            var output = new byte[outputSize];
            var src = 0x10;
            var dst = 0;
            var groupHeader = 0;
            var bitsRemaining = 0;

            while (dst < output.Length)
            {
                if (bitsRemaining == 0)
                {
                    if (src >= bytes.Length)
                        return new OperationError { Message = "Yaz0 group header is truncated." };
                    groupHeader = bytes[src++];
                    bitsRemaining = 8;
                }

                if ((groupHeader & 0x80) != 0)
                {
                    if (src >= bytes.Length)
                        return new OperationError { Message = "Yaz0 literal chunk is truncated." };
                    output[dst++] = bytes[src++];
                }
                else
                {
                    if (src + 1 >= bytes.Length)
                        return new OperationError { Message = "Yaz0 backreference is truncated." };

                    var b1 = bytes[src++];
                    var b2 = bytes[src++];
                    var backOffset = (((b1 & 0x0f) << 8) | b2) + 1;
                    var length = b1 >> 4;
                    if (length == 0)
                    {
                        if (src >= bytes.Length)
                            return new OperationError { Message = "Yaz0 extended length byte is truncated." };
                        length = bytes[src++] + 0x12;
                    }
                    else
                    {
                        length += 2;
                    }

                    if (backOffset > dst)
                        return new OperationError { Message = "Yaz0 backreference offset is out of bounds." };

                    if (length > output.Length - dst)
                        throw new InvalidDataException($"Yaz0 run at input offset {src} exceeds declared output size.");
                    var copySrc = dst - backOffset;
                    for (var index = 0; index < length && dst < output.Length; index++)
                        output[dst++] = output[copySrc++];
                }

                groupHeader <<= 1;
                bitsRemaining--;
            }

            return output;
        }
        catch (Exception ex)
        {
            return new OperationError { Message = $"Failed to decompress Yaz0 data: {ex.Message}", Exception = ex };
        }
    }

    private static DecodedArchive ParseU8Archive(byte[] bytes)
    {
        if (bytes.Length < 0x20)
            throw new InvalidDataException("U8 header is truncated.");
        var rootOffset = checked((int)BigEndianBinaryHelper.BufferToUint32(bytes, 4));
        var dataOffset = checked((int)BigEndianBinaryHelper.BufferToUint32(bytes, 12));
        if (rootOffset < 0x20 || dataOffset > bytes.Length)
            throw new InvalidDataException("U8 root/data offset is invalid.");
        var rootNode = ReadU8Node(bytes, rootOffset);
        var nodeCount = rootNode.Size;
        if (rootNode.Type != 1 || nodeCount < 1 || nodeCount > (bytes.Length - rootOffset) / 12)
            throw new InvalidDataException("U8 root/node table is invalid or truncated.");
        var stringTableOffset = checked(rootOffset + nodeCount * 12);
        if (stringTableOffset >= dataOffset)
            throw new InvalidDataException("U8 string table is missing or overlaps file data.");
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var paths = new HashSet<string>(StringComparer.Ordinal);

        // Use the directory end indices directly; untrusted nesting must not consume the call stack.
        var directories = new Stack<(int Index, int End, string Path)>();
        directories.Push((0, nodeCount, string.Empty));
        for (var index = 1; index < nodeCount; index++)
        {
            while (index >= directories.Peek().End)
                directories.Pop();
            var parent = directories.Peek();
            var node = ReadU8Node(bytes, rootOffset + index * 12);
            var nameOffset = checked(stringTableOffset + node.NameOffset);
            if (nameOffset < stringTableOffset || nameOffset >= dataOffset)
                throw new InvalidDataException($"U8 node {index} name offset {nameOffset} is outside the string table.");
            var terminator = Array.IndexOf(bytes, (byte)0, nameOffset, dataOffset - nameOffset);
            if (terminator < 0)
                throw new InvalidDataException($"U8 node {index} name at offset {nameOffset} has no terminator.");
            var name = BinaryStringHelper.ReadAscii(bytes, nameOffset, terminator - nameOffset);
            var conventionalRoot = name == "." && index == 1 && node.Type == 1
                && parent.Index == 0 && node.Size == nodeCount;
            if (!conventionalRoot) ArchivePath.ValidateMember(name);
            var logicalPath = parent.Path.Length == 0 ? name : $"{parent.Path}/{name}";
            if (!paths.Add(logicalPath))
                throw new InvalidDataException($"Duplicate U8 member '{logicalPath}'.");

            if (node.Type == 1)
            {
                if (node.Size <= index || node.Size > parent.End || node.DataOffset != parent.Index)
                    throw new InvalidDataException($"U8 directory '{logicalPath}' has invalid parent/end indices.");
                directories.Push((index, node.Size, logicalPath));
            }
            else
            {
                if (node.DataOffset < dataOffset || node.Size < 0 || node.DataOffset > bytes.Length - node.Size)
                    throw new InvalidDataException($"U8 member '{logicalPath}' has invalid range {node.DataOffset}+{node.Size}.");
                files.Add(logicalPath, bytes[node.DataOffset..(node.DataOffset + node.Size)]);
            }
        }
        return new(files);
    }

    private static U8Node ReadU8Node(byte[] bytes, int offset)
    {
        if (offset < 0 || offset > bytes.Length - 12)
            throw new InvalidDataException($"U8 node at offset {offset} is truncated.");
        if (bytes[offset] > 1)
            throw new InvalidDataException($"U8 node at offset {offset} has unknown type {bytes[offset]}.");
        return new(
            bytes[offset],
            (bytes[offset + 1] << 16) | (bytes[offset + 2] << 8) | bytes[offset + 3],
            checked((int)BigEndianBinaryHelper.BufferToUint32(bytes, offset + 4)),
            checked((int)BigEndianBinaryHelper.BufferToUint32(bytes, offset + 8))
        );
    }
}
