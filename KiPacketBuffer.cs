using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;

namespace Imlight.CoreLib.Shared.Networking;

internal sealed class KiPacketBuffer {
    private const int MaximumBodyLength = 16 * 1024 * 1024;
    private byte[] _buffer = new byte[4096];
    private int _count;

    internal IReadOnlyList<byte[]> Append(byte[] data, int count) {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, data.Length);
        int required = checked(_count + count);
        if (required > MaximumBodyLength + 8) {
            throw new InvalidDataException("Incoming game packet exceeds the size limit.");
        }

        if (required > _buffer.Length) {
            Array.Resize(ref _buffer, Math.Max(required, Math.Min(_buffer.Length * 2, MaximumBodyLength + 8)));
        }
        data.AsSpan(0, count).CopyTo(_buffer.AsSpan(_count));
        _count = required;

        List<byte[]> packets = [];
        int offset = 0;
        while (_count - offset >= 4) {
            ReadOnlySpan<byte> remaining = _buffer.AsSpan(offset, _count - offset);
            if (BinaryPrimitives.ReadUInt16LittleEndian(remaining) != 0xF00D) {
                throw new InvalidDataException("Invalid game packet header.");
            }

            uint bodyLength = BinaryPrimitives.ReadUInt16LittleEndian(remaining[2..]);
            int headerLength = 4;
            if (bodyLength == 0x8000) {
                if (remaining.Length < 8) {
                    break;
                }
                bodyLength = BinaryPrimitives.ReadUInt32LittleEndian(remaining[4..]);
                headerLength = 8;
            }
            if (bodyLength < 4 || bodyLength > MaximumBodyLength) {
                throw new InvalidDataException("Invalid game packet length.");
            }

            int packetLength = headerLength + (int)bodyLength;
            if (remaining.Length < packetLength) {
                break;
            }
            packets.Add(remaining[..packetLength].ToArray());
            offset += packetLength;
        }

        if (offset > 0) {
            _buffer.AsSpan(offset, _count - offset).CopyTo(_buffer);
            _count -= offset;
        }
        return packets;
    }
}
