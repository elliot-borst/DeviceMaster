namespace DeviceMaster.Core.Devices;

public enum DeviceKind
{
    Unknown = 0,

    /// <summary>Corsair iCUE LINK System Hub — fans, pump, coolant temp over the Link chain (HID).</summary>
    CorsairLinkHub,

    /// <summary>Corsair LCD module (XD7 screen). Enumerates as its own HID device, not through the hub.</summary>
    CorsairLcd,

    /// <summary>Lian Li SL V3 wireless TX/RX controller pair (WinUSB).</summary>
    LianLiSlv3Controller,

    /// <summary>Individual Lian Li UNI FAN SL V3 fan node — one USB device per fan (WinUSB).</summary>
    LianLiSlv3FanNode,

    /// <summary>Classic Lian Li Uni Hub (0CF2, HID). Not present on this machine; kept for completeness.</summary>
    LianLiUniHub,

    /// <summary>Turzx / Turing-family smart screen (USB serial).</summary>
    TurzxScreen,

    /// <summary>Motherboard RGB controller (e.g. ASUS Aura). Recognized so we can explicitly ignore it.</summary>
    MotherboardRgbController,
}
