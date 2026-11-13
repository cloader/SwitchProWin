using SwitchProWin.Protocol;
using Xunit;

namespace SwitchProWin.Tests;

public class OutputReportTests
{
    [Fact]
    public void Subcommand_packet_has_documented_layout()
    {
        // 文档样例：buf[0]=1, buf[1]=counter, buf[2..9]=中性震动, buf[10]=子命令, buf[11+]=参数
        var packet = OutputReportBuilder.SetInputReportMode(0x05, InputMode.StandardFull);

        Assert.Equal(0x40, packet.Length); // hid_write 全长 64 字节
        Assert.Equal(0x01, packet[0]);
        Assert.Equal(0x05, packet[1]);
        Assert.Equal(new byte[] { 0x00, 0x01, 0x40, 0x40 }, packet[2..6]); // 左马达中性
        Assert.Equal(new byte[] { 0x00, 0x01, 0x40, 0x40 }, packet[6..10]); // 右马达中性
        Assert.Equal(0x03, packet[10]); // SetInputReportMode
        Assert.Equal(0x30, packet[11]); // StandardFull
        Assert.All(packet[12..], b => Assert.Equal(0, b)); // 其余补零
    }

    [Fact]
    public void Rumble_packet_layout()
    {
        var packet = OutputReportBuilder.Rumble(0x07, new byte[] { 0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x88 });

        Assert.Equal(0x10, packet[0]);
        Assert.Equal(0x07, packet[1]);
        Assert.Equal(0x11, packet[2]);
        Assert.Equal(0x88, packet[9]);
    }

    [Fact]
    public void Spi_read_packet_encodes_address_le()
    {
        var packet = OutputReportBuilder.SpiRead(0x02, address: 0x603D, length: 9);

        Assert.Equal(0x01, packet[0]);
        Assert.Equal(0x10, packet[10]); // SpiRead 子命令
        Assert.Equal(0x3D, packet[11]); // 地址 LE
        Assert.Equal(0x60, packet[12]);
        Assert.Equal(0x00, packet[13]);
        Assert.Equal(0x00, packet[14]);
        Assert.Equal(9, packet[15]);    // 长度
    }

    [Fact]
    public void Counter_wraps_in_0x0_to_0xf()
    {
        var counter = new OutputReportBuilder.PacketCounter();
        for (int i = 1; i <= 20; i++)
        {
            byte next = counter.Next();
            Assert.Equal(i & 0x0F, next);
        }
    }

    [Fact]
    public void Usb_keepalive_is_0x80_0x04()
    {
        var packet = OutputReportBuilder.UsbKeepAlive();
        Assert.Equal(0x80, packet[0]);
        Assert.Equal(0x04, packet[1]);
    }
}

public class SubcommandReplyTests
{
    /// <summary>构造 0x21 应答：SPI 读 0x603D 处 9 字节，数据为递增序列。</summary>
    [Fact]
    public void Parses_spi_read_reply()
    {
        var report = new byte[16 + 5 + 9];
        report[0] = 0x21;
        report[13] = 0x90; // ACK 字节（MSB=1）
        report[14] = 0x10; // SpiRead

        // byte15 起的数据区：[addr 4B LE][len 1B][data...]
        report[15] = 0x3D;
        report[16] = 0x60;
        report[17] = 0x00;
        report[18] = 0x00;
        report[19] = 9;
        for (int i = 0; i < 9; i++) report[20 + i] = (byte)(0xA0 + i);

        var ok = InputReportParser.TryParseSubcommandReply(report, out byte ack, out byte subcmd, out var payload, out _);

        Assert.True(ok);
        Assert.Equal(0x90, ack);
        Assert.Equal(0x10, subcmd);

        var spiOk = SubcommandReplyParser.TryParseSpiRead(payload, out uint addr, out var data);
        Assert.True(spiOk);
        Assert.Equal(0x603Du, addr);
        Assert.Equal(9, data.Length);
        Assert.Equal(0xA0, data[0]);
        Assert.Equal(0xA8, data[8]);
    }

    [Fact]
    public void Spi_read_reply_with_wrong_address_is_detected()
    {
        var report = new byte[16 + 5 + 9];
        report[0] = 0x21;
        report[13] = 0x90;
        report[14] = 0x10;
        report[15] = 0x99; // 错误地址

        InputReportParser.TryParseSubcommandReply(report, out _, out _, out var payload, out _);
        SubcommandReplyParser.TryParseSpiRead(payload, out uint addr, out _);
        Assert.NotEqual(0x603Du, addr);
    }
}
