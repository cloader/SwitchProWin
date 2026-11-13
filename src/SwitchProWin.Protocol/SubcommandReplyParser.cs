namespace SwitchProWin.Protocol;

/// <summary>子命令应答（0x21 报告）的载荷解析。</summary>
public static class SubcommandReplyParser
{
    /// <summary>从 0x21 应答的数据区（byte15 起）解析 SPI Flash 读结果。
    /// 数据区格式：[地址 4 字节 LE][长度 1 字节][数据...]。
    /// ACK 字节位于 0x21 报告的 byte13（MSB=1 即 ACK），不在此数据区内。
    /// </summary>
    /// <returns>true = 解析成功。</returns>
    public static bool TryParseSpiRead(ReadOnlySpan<byte> payload, out uint address, out ReadOnlySpan<byte> data)
    {
        address = 0;
        data = default;

        if (payload.Length < 5)
            return false;

        address = (uint)(payload[0] | (payload[1] << 8) | (payload[2] << 16) | (payload[3] << 24));
        byte length = payload[4];
        if (payload.Length < 5 + length)
            return false;

        data = payload.Slice(5, length);
        return true;
    }
}
