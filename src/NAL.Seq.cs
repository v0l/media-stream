using System;
using System.Buffers;

namespace MediaStreams.AVC
{
    public struct SeqParameterSet : RBSP
    {
        //profile_idc u(8)
        //constraint_set0_flag u(1)
        //constraint_set1_flag u(1)
        //constraint_set2_flag u(1)
        //reserved_zero_5bits u(5)
        //level_idc u(8)
        //seq_parameter_set_id ue(v)
        //log2_max_frame_num_minus4 ue(v)
        //pic_order_cnt_type ue(v)
        //  if(pic_order_cnt_type == 0)
        //    log2_max_pic_order_cnt_lsb_minus4 ue(v)
        //  else == 1
        //    delta_pic_order_always_zero_flag u(1)

        public byte ProfileIDC { get; set; }
        public bool Constraint0 { get; set; }
        public bool Constraint1 { get; set; }
        public bool Constraint2 { get; set; }
        public byte LevelIDC { get; set; }
        public ulong Id { get; set; }

        public SequencePosition Read(ReadOnlySequence<byte> v)
        {
            throw new NotImplementedException();
        }
    }
}