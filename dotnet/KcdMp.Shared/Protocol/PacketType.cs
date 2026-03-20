namespace KcdMp.Shared.Protocol;

public enum PacketType : byte
{
    Handshake    = 0x00,
    Position     = 0x01,
    Ghost        = 0x02,
    Name         = 0x03,
    Ping         = 0x04,
    Pong         = 0x05,
    Disconnect   = 0x06,
    StateUpdate  = 0x07,
    StateSync    = 0x08,
    Event        = 0x09,
    EventRelay   = 0x0A,
    Auth         = 0x0B,
    AuthResult   = 0x0C,
    Ack          = 0xFF,
}
