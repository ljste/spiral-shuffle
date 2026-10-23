using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Imcodec.IO;
using Imcodec.MessageLayer;
using Imcodec.MessageLayer.Generated;

namespace Imlight.CoreLib.Shared.Networking;

internal static class KiPacketCodec {
    private static readonly IReadOnlyDictionary<byte, MessageProtocol> Protocols = typeof(IMessage).Assembly
        .GetExportedTypes().Where(type => type.IsClass && !type.IsAbstract && typeof(MessageProtocol).IsAssignableFrom(type))
        .Select(Activator.CreateInstance).OfType<MessageProtocol>().ToDictionary(protocol => protocol.ServiceId);

    internal static byte[] Encode(IMessage message) {
        byte[] packet = MessageEncoder.Encode(message);
        if (BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(2)) == 0x8000) {
            // Older Imcodec builds omit the body headers from the extended length.
            BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4), (uint)(packet.Length - 8));
        }
        return packet;
    }

    internal static IReadOnlyCollection<IMessage> Decode(byte[] packet) {
        if (BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(2)) != 0x8000) {
            return MessageEncoder.Decode(packet) ?? Array.Empty<IMessage>();
        }

        BitReader reader = new(packet[8..]);
        bool control = reader.ReadUInt8() == 1;
        byte operation = reader.ReadUInt8();
        reader.ReadUInt16();
        if (control) {
            IMessage message = new ControlMessageProtocol().Dispatch(operation);
            message.Decode(reader);
            return [message];
        }

        List<IMessage> messages = [];
        long end = (packet.Length - 9L) * 8;
        while (reader.BitPos() < end) {
            byte service = reader.ReadUInt8();
            byte order = reader.ReadUInt8();
            reader.ReadUInt16();
            if (!Protocols.TryGetValue(service, out MessageProtocol? protocol)) {
                throw new InvalidDataException($"Unknown game protocol {service}.");
            }
            IMessage message = protocol.Dispatch(order);
            message.Decode(reader);
            if (reader.BitPos() > end) {
                throw new InvalidDataException("A game message extends past its packet.");
            }
            messages.Add(message);
        }
        return messages;
    }
}
