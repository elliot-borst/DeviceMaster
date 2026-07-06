namespace DeviceMaster.Core.Devices;

public enum DeviceKind
{
    Unknown = 0,

    /// <summary>Corsair iCUE LINK System Hub — fans, pump, coolant temp over the Link chain (HID).</summary>
    CorsairLinkHub,

    /// <summary>Corsair pump/res LCD module. Enumerates as its own HID device, not through the hub.</summary>
    CorsairLcd,

    /// <summary>Lian Li SL V3 wireless TX/RX controller pair (WinUSB).</summary>
    LianLiSlv3Controller,

    /// <summary>Individual Lian Li UNI FAN SL V3 fan node — one USB device per fan (WinUSB).</summary>
    LianLiSlv3FanNode,

    /// <summary>Classic wired Lian Li Uni Hub (0CF2, HID). Recognized but not implemented.</summary>
    LianLiUniHub,

    /// <summary>Turzx / Turing-family smart screen (USB serial).</summary>
    TurzxScreen,

    /// <summary>Motherboard RGB controller (e.g. ASUS Aura). Recognized so we can explicitly ignore it.</summary>
    MotherboardRgbController,
}
