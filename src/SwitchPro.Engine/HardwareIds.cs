namespace SwitchPro.Engine;

/// <summary>手柄种类。</summary>
public enum ControllerKind
{
    ProController,
    JoyConLeft,
    JoyConRight,
}

/// <summary>Switch 手柄 USB/蓝牙 VID/PID 与判别。</summary>
public static class HardwareIds
{
    public const int NintendoVendorId = 0x057E;

    public const int ProControllerPid = 0x2009;
    public const int JoyConLeftPid = 0x2006;
    public const int JoyConRightPid = 0x2007;
    public const int ChargingGripPid = 0x200E;

    /// <summary>SPI 0x6012 设备类型字节：1=Joy-Con L，2=Joy-Con R，3=Pro。</summary>
    public const uint SpiDeviceTypeAddress = 0x6012;

    /// <summary>是否为本软件支持的设备。</summary>
    public static bool IsSupported(int vendorId, int productId)
        => vendorId == NintendoVendorId
           && (productId == ProControllerPid || productId == JoyConLeftPid
               || productId == JoyConRightPid || productId == ChargingGripPid);

    /// <summary>PID 对应的已知种类；握把（200E）一个 PID 两个接口各连一只 Joy-Con，
    /// 需 SPI 读设备类型判别，返回 null。</summary>
    public static ControllerKind? KindFromPid(int productId) => productId switch
    {
        ProControllerPid => ControllerKind.ProController,
        JoyConLeftPid => ControllerKind.JoyConLeft,
        JoyConRightPid => ControllerKind.JoyConRight,
        _ => null,
    };
}
