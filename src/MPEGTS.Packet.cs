using System;
using System.Buffers;
using System.Linq;

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
            /// <summary>
            /// The transport_error_indicator is a 1-bit flag. When set to '1' it indicates that at least
            /// 1 uncorrectable bit error exists in the associated transport stream packet. This bit may be set to '1' by entities external to
            /// the transport layer. When set to '1' this bit shall not be reset to '0' unless the bit value(s) in error have been corrected.
            /// </summary>
            public bool TransportError { get; set; }

            /// <summary>
            /// The payload_unit_start_indicator is a 1-bit flag which has normative meaning for
            /// transport stream packets that carry PES packets (refer to 2.4.3.6) or transport stream section data (refer to Table 2-31 in
            /// 2.4.4.4).
            /// When the payload of the transport stream packet contains PES packet data, the payload_unit_start_indicator has the
            /// following significance: a '1' indicates that the payload of this transport stream packet will commence with the first byte
            /// of a PES packet and a '0' indicates no PES packet shall start in this transport stream packet. If the
            /// payload_unit_start_indicator is set to '1', then one and only one PES packet starts in this transport stream packet. This
            /// also applies to private streams of stream_type 6 (refer to Table 2-34).
            /// When the payload of the transport stream packet contains transport stream section data, the payload_unit_start_indicator
            /// has the following significance: if the transport stream packet carries the first byte of a section, the
            /// payload_unit_start_indicator value shall be '1', indicating that the first byte of the payload of this transport stream
            /// packet carries the pointer_field. If the transport stream packet does not carry the first byte of a section, the
            /// payload_unit_start_indicator value shall be '0', indicating that there is no pointer_field in the payload. Refer to 2.4.4.1
            /// and 2.4.4.2. This also applies to private streams of stream_type 5 (refer to Table 2-34).
            /// For null packets the payload_unit_start_indicator shall be set to '0'.
            ///The meaning of this bit for transport stream packets carrying only private data is not defined in this Specification.
            /// </summary>
            public bool PayloadUnitStart { get; set; }

            /// <summary>
            /// The transport_priority is a 1-bit indicator. When set to '1' it indicates that the associated packet is
            /// of greater priority than other packets having the same PID which do not have the bit set to '1'. The transport mechanism
            /// can use this to prioritize its data within an elementary stream. Depending on the application the transport_priority field
            /// may be coded regardless of the PID or within one PID only. This field may be changed by channel-specific encoders or
            /// decoders.
            /// </summary>
            public bool TransportPriority { get; set; }

            /// <summary>
            /// The PID is a 13-bit field, indicating the type of the data stored in the packet payload. PID value 0x0000 is
            /// reserved for the program association table (see Table 2-30). PID value 0x0001 is reserved for the conditional access
            /// table (see Table 2-32). PID value 0x0002 is reserved for the transport stream description table (see Table 2-36),
            /// PID value 0x0003 is reserved for IPMP control information table (see ISO/IEC 13818-11) and PID values
            /// 0x0004-0x000F are reserved. PID value 0x1FFF is reserved for null packets (see Table 2-3).
            /// </summary>
            public ushort Pid { get; set; }

            /// <summary>
            /// This 2-bit field indicates the scrambling mode of the transport stream packet payload.
            /// The transport stream packet header, and the adaptation field when present, shall not be scrambled. In the case of a null
            /// packet the value of the transport_scrambling_control field shall be set to '00' (see Table 2-4).
            /// </summary>
            public TransportScramblingControl Scrambling { get; set; }

            /// <summary>
            /// This 2-bit field indicates whether this transport stream packet header is followed by an
            /// adaptation field and/or payload (see Table 2-5).
            /// Rec. ITU-T H.222.0 | ISO/IEC 13818-1 decoders shall discard transport stream packets with the
            /// adaptation_field_control field set to a value of '00'. In the case of a null packet the value of the adaptation_field_control
            /// shall be set to '01'.
            /// </summary>
            public AdaptationFieldControl AdaptationFieldControl { get; set; }

            /// <summary>
            /// The continuity_counter is a 4-bit field incrementing with each transport stream packet with the
            /// same PID. The continuity_counter wraps around to 0 after its maximum value. The continuity_counter shall not be
            /// incremented when the adaptation_field_control of the packet equals '00' or '10'.
            /// In transport streams, duplicate packets may be sent as two, and only two, consecutive transport stream packets of the
            /// same PID. The duplicate packets shall have the same continuity_counter value as the original packet and the
            /// adaptation_field_control field shall be equal to '01' or '11'. In duplicate packets each byte of the original packet shall be
            /// duplicated, with the exception that in the program clock reference fields, if present, a valid value shall be encoded.
            /// The continuity_counter in a particular transport stream packet is continuous when it differs by a positive value of one
            /// from the continuity_counter value in the previous transport stream packet of the same PID, or when either of the
            /// non-incrementing conditions (adaptation_field_control set to '00' or '10', or duplicate packets as described above) are
            /// met. The continuity counter may be discontinuous when the discontinuity_indicator is set to '1' (refer to 2.4.3.4). In the
            ///case of a null packet the value of the continuity_counter is undefined.
            /// </summary>
            public byte Continuity { get; set; }

            public AdaptationField AdaptationField { get; set; }

            /// <summary>
            /// Data bytes shall be contiguous bytes of data from the PES packets (refer to 2.4.3.6), transport stream
            /// sections (refer to 2.4.4), packet stuffing bytes after transport stream sections, or private data not in these structures as
            /// indicated by the PID. In the case of null packets with PID value 0x1FFF, data_bytes may be assigned any value. The
            /// number of data_bytes, N, is specified by 184 minus the number of bytes in the adaptation_field(), as described in
            /// 2.4.3.4.
            /// </summary>
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
-- Payload {Payload?.Length ?? 0} bytes{(IsPESPayload ? " [PES]" : string.Empty)} --
 {string.Join("\n ", BitConverter.ToString(Payload).Split("-").Select((v,i) => (Index: i, Value: v)).GroupBy(a => a.Index / 16).Select(a => string.Join(" ", a.Select(b => b.Value))))}";
            }

            public bool HasAdaptionField =>
                AdaptationFieldControl == AdaptationFieldControl.AdaptationFieldAndPayload
                || AdaptationFieldControl == AdaptationFieldControl.AdaptationFieldOnly;

            public bool HasPayload =>
                AdaptationFieldControl == AdaptationFieldControl.AdaptationFieldAndPayload
                || AdaptationFieldControl == AdaptationFieldControl.PayloadOnly;

            public bool IsPESPayload => (Payload?.Length ?? 0) != 0 && Payload[0] == 0x00 && Payload[1] == 0x00 && Payload[2] == 0x01;

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

                    if (pkt.HasAdaptionField)
                    {
                        var af = AdaptationField.Parse(header.Slice(sr.Position));
                        if (!af.Equals(default))
                        {
                            pkt.AdaptationField = af;
                            // From what ive seen sometimes this is > 188 bytes
                            // For FFMPEG src, we would simply skip this pkt
                            // https://ffmpeg.org/doxygen/trunk/mpegts_8c_source.html#l2787
                            if (sr.Remaining >= af.Length + 1)
                            {
                                sr.Advance(af.Length + 1);
                            }
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