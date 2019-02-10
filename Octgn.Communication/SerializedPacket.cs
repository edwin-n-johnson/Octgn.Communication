﻿using System;
using System.IO;
using System.Text;

namespace Octgn.Communication
{
    public class SerializedPacket : IPacket
    {
        public byte PacketType { get; private set; }

        public PacketFlag Flags { get; private set; }

        public string Destination { get; private set; }

        public User Origin { get; private set; }

        public DateTimeOffset Sent { get; private set; }

        public byte[] Data { get; private set; }

        public Type GetPacketType() {
            return Packet.GetType(PacketType);
        }

        public Packet DeserializePacket(ISerializer serializer) {
            var newPacket = Packet.Create(PacketType);
            newPacket.Sent = Sent;
            newPacket.Destination = Destination;
            newPacket.Origin = Origin;

            using (var ms = new MemoryStream(Data, HEADER_SIZE, Data.Length - HEADER_SIZE))
            using (var reader = new BinaryReader(ms)) {
                newPacket.Deserialize(reader, serializer);
            }

            return newPacket;
        }

        const int DESTINATION_SIZE = 64;
        const int ORIGIN_SIZE = 64;
        const int SENT_SIZE = 40;

        public const int HEADER_SIZE =
              sizeof(byte)       // Packet type
            + sizeof(PacketFlag) // Packet Flags
            + DESTINATION_SIZE   // Destination
            + ORIGIN_SIZE        // Origin
            + SENT_SIZE          // Sent
        ;

        public static byte[] Create(IPacket packet, ISerializer serializer) {
            if (packet == null) throw new ArgumentNullException(nameof(packet));
            if (serializer == null) throw new ArgumentNullException(nameof(serializer));

            if (!Packet.IsPacketTypeRegistered(packet.PacketType))
                throw new UnregisteredPacketException(packet);

            using (var ms = new MemoryStream())
            using (var writer = new BinaryWriter(ms)) {
                // Packet Type
                writer.Write(packet.PacketType);

                // Packet Flags
                writer.Write((byte)packet.Flags);

                // Destination
                WriteString(packet.Destination ?? string.Empty, DESTINATION_SIZE, writer);

                // Origin
                WriteString(packet.Origin?.ToString() ?? string.Empty, ORIGIN_SIZE, writer);

                // Sent
                WriteString(packet.Sent.ToString("o"), SENT_SIZE, writer);

                if (ms.Length != HEADER_SIZE)
                    throw new InvalidOperationException($"Data written is not the same size as the header.");

                // Body
                if(packet is Packet regularPacket) {
                    regularPacket.Serialize(writer, serializer);
                }

                ms.Flush();

                return ms.ToArray();
            }
        }

        internal static void WriteString(string str, int length, BinaryWriter writer) {
            if (str.Length > length) throw new InvalidOperationException($"Data {str} is too long");

            var buffer = new char[length];

            for(var i = 0; i < length; i++) {
                if (i < str.Length)
                    buffer[i] = str[i];
                else
                    break;
            }

            writer.Write(buffer);
        }

        public static SerializedPacket Read(byte[] data) {
            if (data.Length < HEADER_SIZE)
                throw new InvalidOperationException($"Data size of {data.Length} is not long enough.");

            var header = new SerializedPacket();

            var currentIndex = 0;

            // Packet Type
            header.PacketType = data[currentIndex];
            currentIndex++;

            // Packet Flags
            header.Flags = (PacketFlag)data[currentIndex];
            currentIndex++;

            // Destination
            header.Destination = Encoding.UTF8.GetString(data, currentIndex, DESTINATION_SIZE);
            { // trim ending 0's
                var zeroIndex = header.Destination.IndexOf('\0');
                if (zeroIndex >= 0) header.Destination = header.Destination.Substring(0, zeroIndex);
            }
            currentIndex += DESTINATION_SIZE;

            // Origin
            var originString = Encoding.UTF8.GetString(data, currentIndex, ORIGIN_SIZE);
            { // trim ending 0's
                var zeroIndex = originString.IndexOf('\0');
                if (zeroIndex >= 0) originString = originString.Substring(0, zeroIndex);
            }
            User.TryParse(originString, out var origin);
            header.Origin = origin;
            currentIndex += ORIGIN_SIZE;

            // Sent Date
            var dateString = Encoding.UTF8.GetString(data, currentIndex, SENT_SIZE);
            { // trim ending 0's
                var zeroIndex = dateString.IndexOf('\0');
                if (zeroIndex >= 0) dateString = dateString.Substring(0, zeroIndex);
            }
            header.Sent = DateTimeOffset.Parse(dateString);
            currentIndex += SENT_SIZE;

            if (currentIndex != HEADER_SIZE)
                throw new InvalidOperationException($"Read different amount of data than header size.");

            header.Data = data;

            return header;
        }

        private string _toString;
        public override string ToString() {
            return _toString ?? (_toString = $"#Type-{PacketType}");
        }
    }
}
