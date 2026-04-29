namespace KodaClaw.Contracts.Models;

[Flags]
public enum ModelCapabilitySet
{
    None  = 0,
    Text  = 1 << 0,  // 1  — text chat (all usable models must have this)
    Image = 1 << 1,  // 2  — image input understanding (vision)
    Video = 1 << 2,  // 4  — video input understanding
    File  = 1 << 3,  // 8  — document/file input understanding
    Audio = 1 << 4,  // 16 — audio input understanding (omni models)
}
