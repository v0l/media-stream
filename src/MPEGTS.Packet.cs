using System;
using System.Buffers;

namespace MediaStreams
{
    public partial class MPEGTS
    {
        public const byte SyncMarker = 0x47;
        public const byte PacketLength = 188;

        public enum TransportScramblingControl
        {
            NotScrambeled = 0,
            Reserved = 1,
            ScrambeledEvent = 2,
            ScrambeledOdd = 3
        }

        public enum AdaptationFieldControl
        {
            Reserved = 0,
            PayloadOnly = 1,
            AdaptationFieldOnly = 2,
            AdaptationFieldAndPayload = 3
        }

        [Flags]
        public enum AdaptationFieldFlags
        {
            /// <summary>
            /// Set when adaptation extension data is present
            /// </summary>
            AdaptationFieldExtension = 0x01,

            /// <summary>
            /// Set when transport private data is present
            /// </summary>
            TransportPrivateData = 0x02,

            /// <summary>
            /// Set when splice countdown field is present
            /// </summary>
            SplicingPoint = 0x04,

            /// <summary>
            /// Set when OPCR field is present
            /// </summary>
            OPCR = 0x08,

            /// <summary>
            /// Set when PCR field is present
            /// </summary>
            PCR = 0x10,

            /// <summary>
            /// Set when this stream should be considered "high priority"
            /// </summary>
            ElementaryStreamPriority = 0x20,

            /// <summary>
            /// Set when the stream may be decoded without errors from this point
            /// </summary>
            RandomAccess = 0x40,

            /// <summary>
            /// Set if current TS packet is in a discontinuity state with respect to either the continuity counter or the program clock reference
            /// </summary>
            Discontinuity = 0x80,

            /// <summary>
            /// All bit flags are set
            /// </summary>
            All = 0x100
        }

        public struct Packet
        {
            public bool TransportError { get; set; }

            public bool PayloadUnitStart { get; set; }

            public bool TransportPriority { get; set; }

            public ushort Pid { get; set; }

            public TransportScramblingControl Scrambling { get; set; }

            public AdaptationFieldControl AdaptationFieldControl { get; set; }

            public byte Continuity { get; set; }

            public AdaptationField AdaptationField { get; set; }

            public byte[] Payload { get; set; }

            public ReadOnlySequence<byte> OriginalData { get; set; }

            public override string ToString()
            {
                return $@"=====================
PID                 : {Pid}
TransportError      : {TransportError}
PayloadUnitStart    : {PayloadUnitStart}
TransportPriority   : {TransportPriority}
Scrambling          : {Scrambling}
AdaptionControl     : {AdaptationFieldControl}
Continuity          : {Continuity}{(HasAdaptionField ? $@"
AdaptationField     : {AdaptationField}" : string.Empty)}
Payload             : {Payload?.Length ?? 0} bytes{(IsPESPayload ? " [PES]" : string.Empty)}";
            }

            public bool HasAdaptionField =>
                AdaptationFieldControl == AdaptationFieldControl.AdaptationFieldAndPayload
                || AdaptationFieldControl == AdaptationFieldControl.AdaptationFieldOnly;

            public bool HasPayload =>
                AdaptationFieldControl == AdaptationFieldControl.AdaptationFieldAndPayload
                || AdaptationFieldControl == AdaptationFieldControl.PayloadOnly;

            public bool IsPESPayload => Payload != null && Payload[0] == 0x00 && Payload[1] == 0x00 && Payload[2] == 0x01;

            public void Validate()
            {
                /*if (!((int)AdaptationFieldControl >= 1 && (int)AdaptationFieldControl <= 3))
                {
                    throw new Exception("Invalid AFC");
                }*/
                if (Pid > 8191)
                {
                    throw new Exception("Invalid PID");
                }
                if (!((int)Scrambling >= 0 && (int)Scrambling <= 3))
                {
                    throw new Exception("Invalid TSC");
                }
                if (HasAdaptionField)
                {
                    AdaptationField.Validate();
                }
                if (HasPayload && Payload == null)
                {
                    throw new Exception("Payload was null");
                }
            }

            /// <summary>
            /// Read the header portion of a TSPacket
            /// </summary>
            /// <param name="header"></param>
            /// <returns></returns>
            public static Packet Parse(ReadOnlySequence<byte> header)
            {
                var sr = new SequenceReader<byte>(header);
                if (sr.TryReadBigEndian(out int hv))
                {
                    var pkt = new Packet()
                    {
                        TransportError = (hv & 0x800000) > 1,
                        PayloadUnitStart = (hv & 0x400000) > 1,
                        TransportPriority = (hv & 0x200000) > 1,
                        Pid = (ushort)((hv & 0x001fff) >> 8),
                        Scrambling = (TransportScramblingControl)((hv & 0xc0) >> 6),
                        AdaptationFieldControl = (AdaptationFieldControl)((hv & 0x30) >> 4),
                        Continuity = (byte)(hv & 0x0f)
                    };

                    if (pkt.HasAdaptionField && !pkt.TransportError)
                    {
                        var af = AdaptationField.Parse(header.Slice(sr.Position));
                        if (!af.Equals(default))
                        {
                            pkt.AdaptationField = af;
                            sr.Advance(af.Length + 1);
                        }
                    }

                    if (pkt.HasPayload)
                    {
                        pkt.ReadPayload(header.Slice(sr.Position));
                        sr.Advance(pkt.Payload.Length);
                    }
                    //Transport error packets are not valid
                    if (!pkt.TransportError)
                    {
                        pkt.Validate();
                    }
                    return pkt;
                }
                return default;
            }

            /// <summary>
            /// Read the Payload for this packet
            /// </summary>
            /// <param name="pl"></param>
            /// <returns></returns>
            public bool ReadPayload(ReadOnlySequence<byte> pl)
            {
                var sr = new SequenceReader<byte>(pl);

                Payload = new byte[sr.Length];
                return sr.TryCopyTo(Payload);
            }

            public bool Write(Span<byte> mem)
            {
                if (mem.Length < PacketLength)
                {
                    throw new Exception($"Not enough memory to write packet, must have at least {PacketLength} bytes, got {mem.Length} bytes");
                }
                var span = mem.Slice(0, PacketLength);

                uint hv = (SyncMarker << 24)
                    & (TransportError ? 1 << 23 : UInt32.MaxValue)
                    & (PayloadUnitStart ? 1 << 22 : UInt32.MaxValue)
                    & (uint)Pid << 8
                    & (uint)Scrambling << 6
                    & (uint)AdaptationFieldControl << 4
                    & Continuity;

                BitConverter.TryWriteBytes(span, hv);

                var offset = 4;
                if (HasAdaptionField)
                {
                    AdaptationField.Write(span.Slice(offset));
                    offset += AdaptationField.Length;
                }

                if (HasPayload)
                {
                    Payload.CopyTo(span.Slice(offset));
                    offset += Payload.Length;
                }

                if (offset < PacketLength)
                {
                    span.Slice(offset).Fill(0xff);
                }
                return true;
            }
        }
    }
}