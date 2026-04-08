namespace KcdMp.Shared.Protocol;

public enum EventType : ushort
{
    DamageDealt  = 1,
    PlayerDied   = 2,
    PlayerDowned = 3,
    Revive       = 4,
    NpcDamage    = 5,
    ChatSubmit   = 100,
    ChatMessage  = 101,
}
