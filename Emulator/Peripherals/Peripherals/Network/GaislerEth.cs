//
// Copyright (c) Antmicro
// Copyright (c) Realtime Embedded
//
// This file is part of the Emul8 project.
// Full license details are defined in the 'LICENSE' file.
//
using System;
using Emul8.Core;
using Emul8.Core.Structure;
using Emul8.Logging;
using Emul8.Peripherals.Bus;
using Emul8.Network;

namespace Emul8.Peripherals.Network
{
    public class GaislerEth : NetworkWithPHY, IDoubleWordPeripheral, IGaislerAPB, IMACInterface, IKnownSize, INetworkInterface
    {
        public GaislerEth(Machine machine) : base(machine)
        {
            IRQ = new GPIO();
            Link = new NetworkLink(this);
            Reset();
        }

        public override void Reset()
        {
            registers = new regs();
            MAC = new MACAddress();
            transmitDescriptorBase = 0;
            transmitDescriptorOffset = 0;
            receiveDescriptorBase = 0;
            transmitDescriptorOffset = 0;
        }

        public GPIO IRQ { get; private set; }

        #region IDoubleWordPeripheral implementation

        public uint ReadDoubleWord(long offset)
        {
            switch((Offset)offset)
            {
            case Offset.Control:
                return registers.Control;
            case Offset.Status:
                return registers.Status;
            case Offset.MacAddressHi:
                //return registers.MacAddresHi;
                return (uint)BitConverter.ToUInt16(MAC.Bytes, 4);
            case Offset.MacAddressLo:
                //return registers.MacAddresLo;
                return BitConverter.ToUInt32(MAC.Bytes, 0);
            case Offset.MDIOControlStatus:
                return registers.MDIOControlStatus;
            case Offset.TxDescriptorPointer:
                return registers.TxDescriptorPointer;
            case Offset.RxDescriptorPointer:
                return registers.RxDescriptorPointer;
            case Offset.HashTableHi:
                return registers.HashTableHi;
            case Offset.HashTableLo:
                return registers.HashTableLo;
            default:
                this.LogUnhandledRead(offset);
                return 0u;
            }
        }

        public void WriteDoubleWord(long offset, uint value)
        {
            switch((Offset)offset)
            {
            case Offset.Control:
                if(value == 0x40)
                {
                    break;
                }
                if((value & 0x01u) != 0)
                {
                    this.transmitFrame();
                }
                registers.Control = value;
                break;
            case Offset.Status:
                //registers.Status = value;
                registers.Status &= ~(value & 0xffu);
                break;
            case Offset.MacAddressHi:
                registers.MacAddresHi = value;
                break;
            case Offset.MacAddressLo:
                registers.MacAddresLo = value;
                break;
            case Offset.MDIOControlStatus:

                var id = ((value >> 11) & 0x1f);
                var reg = ((value >> 6) & 0x1f);
                var read = (value & 0x02) != 0;
                var write = (value & 0x01) != 0;
                
                if(!phys.ContainsKey(id))
                {
                    this.Log(LogLevel.Warning, "Write to phy with unknown ID {0}", id);
                    return;
                }
                var phy = phys[id];
                if(read)
                {
                    var phyRead = phy.Read((ushort)reg);
                    registers.MDIOControlStatus &= 0x0000FFFF;//clear data
                    registers.MDIOControlStatus |= (uint)(phyRead << 16); 
                }
                else if(write)
                {
                    phy.Write((ushort)reg, (ushort)((value >> 16) & 0xFFFF));
                }
                else//unknown 
                {
                    this.Log(LogLevel.Warning, "Unknown phy operation - neither read nor write ");
                }
                /*if ( (registers.InterruptMask & (1u<<0)) == 0 )
                {
                    registers.InterruptStatus |= 1u<<0;
                    IRQ.Set();
                }*/
                break;  
            case Offset.TxDescriptorPointer:
                registers.TxDescriptorPointer = value;
                transmitDescriptorBase = value & ~(0x3ffu);
                transmitDescriptorOffset = value & 0x3ffu;
                break;
            case Offset.RxDescriptorPointer:
                registers.RxDescriptorPointer = value;
                receiveDescriptorBase = value & ~(0x3ffu);
                receiveDescriptorOffset = value & 0x3ffu;
                break;
            case Offset.HashTableHi:
                registers.HashTableHi = value;
                break;
            case Offset.HashTableLo:
                registers.HashTableLo = value;
                break;
            default:
                this.LogUnhandledWrite(offset, value);
                break;
            }
        }

        #endregion


        #region IGaislerAPB implementation

        public uint GetVendorID()
        {
            return vendorID;
        }

        public uint GetDeviceID()
        {
            return deviceID;
        }

        public uint GetInterruptNumber()
        {
            var irqEndpoint = IRQ.Endpoint;
            if(irqEndpoint != null)
            {              
                return (uint)irqEndpoint.Number;
            }
            else
            {
                return 0;
            }

        }

        public GaislerAPBPlugAndPlayRecord.SpaceType GetSpaceType()
        {
            return GaislerAPBPlugAndPlayRecord.SpaceType.APBIOSpace;
        }

        public NetworkLink Link { get; private set; }

        private readonly uint vendorID = 0x01;
        // Aeroflex Gaisler
        private readonly uint deviceID = 0x1D;
        // GRLIB GRETH

        #endregion

        #region IKnownSize implementation

        public long Size
        {
            get
            {
                return 0x100;
            }
        }

        #endregion

        #region INetworkInterface implementation

        public void ReceiveFrame(EthernetFrame frame)
        {
            machine.ReportForeignEvent(frame, ReceiveFrameInner);
        }

        private void ReceiveFrameInner(EthernetFrame frame)
        {
            if((registers.Control & (1u << 1)) == 0)
            {
                //if receiving is disabled discard packet
                return;
            }
            var rd = new receiveDescriptor(machine.SystemBus);
            rd.Fetch(receiveDescriptorBase | receiveDescriptorOffset);
            if(!rd.Enable)
            {
                //if receive descritptor disabled discard packet
                return;
            }
            machine.SystemBus.WriteBytes(frame.Bytes, (long)rd.PacketAddress);
            registers.Status = 1u << 2;
            if(rd.Wrap)
            {
                receiveDescriptorOffset = 0;
            }
            else
            {
                if(receiveDescriptorOffset != 0x3f8)
                {
                    receiveDescriptorOffset += 8;
                }
                else
                {
                    receiveDescriptorOffset = 0;
                }
            }
            if(rd.InterruptEnable && ((registers.Control & (1u << 3)) != 0))
            {
                registers.Status |= 1u << 2;
                this.IRQ.Set();
                this.IRQ.Unset();
            }
            rd.Length = (uint)frame.Length;
            rd.Enable = false;
            rd.Wrap = false;
            rd.WriteBack();
        }

        private void transmitFrame()
        {
            var td = new transmitDescriptor(machine.SystemBus);
            td.Fetch(transmitDescriptorBase | transmitDescriptorOffset);

            if(!td.Enable)
            {
                return; //if decriptor is disabled there is nothing to send (just return)
            }

            var packet = new EthernetFrame(machine.SystemBus.ReadBytes((long)td.PacketAddress, (int)td.Length));

            if(Link.IsConnected)
            {
                this.Log(LogLevel.Info, "Sending packet length {0}", packet.Length);
                this.Log(LogLevel.Info, "Packet address = 0x{0:X}", td.PacketAddress);
                Link.TransmitFrameFromInterface(packet);
            }

            registers.Status |= 1u << 3;

            if(td.Wrap)
            {
                transmitDescriptorOffset = 0;
            }
            else
            {
                if(transmitDescriptorOffset != 0x3f8)
                {
                    transmitDescriptorOffset += 8;
                }
                else
                {
                    transmitDescriptorOffset = 0;
                }
            }

            if(td.InterruptEnable && ((registers.Control & (1u << 2)) != 0))
            {
                //if interrupts enabled
                registers.Status |= 1u << 3; //transmitter interrupt bit 
                this.IRQ.Set();
                this.IRQ.Unset();
            }

            td.Enable = false;
            td.Wrap = false;
            td.InterruptEnable = false;
            td.Length = 0;
            td.UnderrunError = false;
            td.AttemptLimitError = false;
            td.WriteBack();
           
        }

        #endregion

        #region IMACInterface implementation

        public MACAddress MAC { get; set; }

        #endregion

        #region registers

        private regs registers;

        private class regs
        {
            public uint Control = 1u << 7;
            public uint Status;
            public uint MacAddresHi;
            public uint MacAddresLo;
            public uint MDIOControlStatus;
            public uint TxDescriptorPointer;
            public uint RxDescriptorPointer;
            public uint HashTableHi;
            public uint HashTableLo;
        }

        private enum Offset : uint
        {
            Control = 0x00,
            Status = 0x04,
            MacAddressHi = 0x08,
            MacAddressLo = 0x0C,
            MDIOControlStatus = 0x10,
            TxDescriptorPointer = 0x14,
            RxDescriptorPointer = 0x18,
            HashTableHi = 0x20,
            HashTableLo = 0x24
        }

        #endregion

        #region descriptors

        private uint transmitDescriptorBase;
        private uint transmitDescriptorOffset;

        private uint receiveDescriptorBase;
        private uint receiveDescriptorOffset;

        private class transmitDescriptor
        {
            public transmitDescriptor(SystemBus sysbus)
            {
                sbus = sysbus;
            }

            private SystemBus sbus;

            private uint word0;
            private uint word1;
            private uint ramAddress;

            public bool AttemptLimitError;
            public bool UnderrunError;
            public bool InterruptEnable;
            public bool Wrap;
            public bool Enable;
            public uint Length;
            public uint PacketAddress;

            public void Fetch(uint address)
            {
                ramAddress = address;

                word0 = sbus.ReadDoubleWord(ramAddress);
                word1 = sbus.ReadDoubleWord(ramAddress + 4);

                word0 = (uint)System.Net.IPAddress.HostToNetworkOrder((int)word0);
                word1 = (uint)System.Net.IPAddress.HostToNetworkOrder((int)word1);

                AttemptLimitError = (word0 & (1u << 15)) != 0;
                UnderrunError = (word0 & (1u << 14)) != 0;
                InterruptEnable = (word0 & (1u << 13)) != 0;
                Wrap = (word0 & (1u << 12)) != 0;
                Enable = (word0 & (1u << 11)) != 0;
                Length = word0 & 0x7ffu;

                PacketAddress = word1 & ~(0x03u);
            }

            public void WriteBack()
            {
                word0 = (AttemptLimitError ? 1u << 15 : 0) | (UnderrunError ? 1u << 14 : 0) | (InterruptEnable ? 1u << 13 : 0);
                word0 |= (Wrap ? 1u << 12 : 0) | (Enable ? 1u << 11 : 0) | (Length & 0x7ffu);

                word1 = PacketAddress & ~(0x03u);

                word0 = (uint)System.Net.IPAddress.HostToNetworkOrder((int)word0);
                word1 = (uint)System.Net.IPAddress.HostToNetworkOrder((int)word1);

                sbus.WriteDoubleWord(ramAddress, word0);
                sbus.WriteDoubleWord(ramAddress + 4, word1);
            }
        }

        private class receiveDescriptor
        {
            public receiveDescriptor(SystemBus sysbus)
            {
                sbus = sysbus;
            }

            private SystemBus sbus;
            
            private uint word0;
            private uint word1;
            private uint ramAddress;

            public bool MulticastAddress;
            public bool LengthError;
            public bool OverrunError;
            public bool CRCError;
            public bool FrameTooLong;
            public bool AlignmentError;
            public bool InterruptEnable;
            public bool Wrap;
            public bool Enable;
            public uint Length;
            public uint PacketAddress;

            public void Fetch(uint address)
            {
                ramAddress = address;
                word0 = sbus.ReadDoubleWord(ramAddress);
                word1 = sbus.ReadDoubleWord(ramAddress + 4);

                word0 = (uint)System.Net.IPAddress.HostToNetworkOrder((int)word0);
                word1 = (uint)System.Net.IPAddress.HostToNetworkOrder((int)word1);

                MulticastAddress = (word0 & (1u << 26)) != 0;
                LengthError = (word0 & (1u << 18)) != 0;
                OverrunError = (word0 & (1u << 17)) != 0;
                CRCError = (word0 & (1u << 16)) != 0;
                FrameTooLong = (word0 & (1u << 15)) != 0;
                AlignmentError = (word0 & (1u << 14)) != 0;
                InterruptEnable = (word0 & (1u << 13)) != 0;
                Wrap = (word0 & (1u << 12)) != 0;
                Enable = (word0 & (1u << 11)) != 0;
                Length = word0 & (0x7ffu);

                PacketAddress = word1 & ~(0x03u);
            }

            public void WriteBack()
            {
                word0 = (MulticastAddress ? 1u << 26 : 0) | (LengthError ? 1u << 18 : 0) | (OverrunError ? 1u << 17 : 0);
                word0 |= (CRCError ? 1u << 16 : 0) | (FrameTooLong ? 1u << 15 : 0) | (AlignmentError ? 1u << 14 : 0);
                word0 |= (InterruptEnable ? 1u << 13 : 0) | (Wrap ? 1u << 12 : 0) | (Enable ? 1u << 18 : 0) | (Length & (0x7ffu));

                word1 = PacketAddress & ~(0x03u);

                word0 = (uint)System.Net.IPAddress.HostToNetworkOrder((int)word0);
                word1 = (uint)System.Net.IPAddress.HostToNetworkOrder((int)word1);

                sbus.WriteDoubleWord(ramAddress, word0);
                sbus.WriteDoubleWord(ramAddress + 4, word1);
            }
        }

        #endregion
    }
}
