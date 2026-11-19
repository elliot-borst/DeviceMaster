namespace DeviceMaster.Core.Devices;

public enum DeviceKind
{
    Unknown = 0,

    /// <summary>Corsair iCUE LINK System Hub — fans and their lighting over the Link chain (HID).</summary>
    CorsairLinkHub,

    /// <summary>Lian Li SL V3 wireless TX/RX controller pair (WinUSB).</summary>
    LianLiSlv3Controller,

    /// <summary>Classic wired Lian Li Uni Hub (0CF2, HID). Recognized but not implemented.</summary>
    LianLiUniHub,

    /// <summary>Turzx / Turing-family smart screen (USB serial).</summary>
    TurzxScreen,

    /// <summary>Motherboard RGB controller (e.g. ASUS Aura). Recognized so we can explicitly ignore it.</summary>
    MotherboardRgbController,

    /// <summary>Desk peripheral with controllable lights (e.g. VKB joystick LEDs).</summary>
    PeripheralLights,
}
