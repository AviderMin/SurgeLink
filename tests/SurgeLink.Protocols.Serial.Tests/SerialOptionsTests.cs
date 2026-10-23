using System.IO.Ports;
using SurgeLink.Core.Protocols;
using Xunit;

namespace SurgeLink.Protocols.Serial.Tests;

public sealed class SerialOptionsTests
{
    [Fact]
    public void DefaultsUse115200EightNOneAndNoControlLines()
    {
        var options = SerialOptions.Parse(Request(" com12 "));
        Assert.Equal("COM12", options.PortName);
        Assert.Equal(115200, options.BaudRate);
        Assert.Equal(8, options.DataBits);
        Assert.Equal(Parity.None, options.Parity);
        Assert.Equal(StopBits.One, options.StopBits);
        Assert.Equal(Handshake.None, options.Handshake);
        Assert.False(options.DtrEnable);
        Assert.False(options.RtsEnable);
    }

    [Fact]
    public void ParsesCustomSettingsAndIgnoresUnknownKeys()
    {
        var options = SerialOptions.Parse(Request("COM3") with
        {
            Options = new Dictionary<string, string>
            {
                ["serial.baudRate"] = "9600", ["serial.dataBits"] = "7", ["serial.parity"] = "even",
                ["serial.stopBits"] = "Two", ["serial.handshake"] = "XOnXOff",
                ["serial.dtrEnable"] = "true", ["serial.rtsEnable"] = "true", ["unknown"] = "ignored",
            },
        });
        Assert.Equal(9600, options.BaudRate);
        Assert.Equal(7, options.DataBits);
        Assert.Equal(Parity.Even, options.Parity);
        Assert.Equal(StopBits.Two, options.StopBits);
        Assert.Equal(Handshake.XOnXOff, options.Handshake);
        Assert.True(options.DtrEnable);
        Assert.True(options.RtsEnable);
    }

    [Theory]
    [InlineData("")]
    [InlineData("COM0")]
    [InlineData("COM-1")]
    [InlineData("COM1/file")]
    [InlineData("COM2147483648")]
    [InlineData("/dev/ttyUSB0")]
    [InlineData("file.txt")]
    public void RejectsNonComDevicePaths(string port)
    {
        var error = Assert.Throws<ProtocolException>(() => SerialOptions.Parse(Request(port)));
        Assert.Equal(ProtocolFailureKind.Configuration, error.Kind);
    }

    [Theory]
    [InlineData("serial.baudRate", "0")]
    [InlineData("serial.baudRate", "-1")]
    [InlineData("serial.baudRate", "abc")]
    [InlineData("serial.dataBits", "9")]
    [InlineData("serial.parity", "42")]
    [InlineData("serial.stopBits", "None")]
    [InlineData("serial.handshake", "42")]
    [InlineData("serial.dtrEnable", "yes")]
    [InlineData("serial.rtsEnable", "")]
    public void InvalidOptionIsConfigurationFailure(string key, string value)
    {
        var error = Assert.Throws<ProtocolException>(() => SerialOptions.Parse(Request("COM3") with
        { Options = new Dictionary<string, string> { [key] = value } }));
        Assert.Equal(ProtocolFailureKind.Configuration, error.Kind);
    }

    [Theory]
    [InlineData("8", "OnePointFive")]
    [InlineData("5", "Two")]
    public void RejectsUnsupportedDataAndStopBitCombinations(string bits, string stops)
    {
        Assert.Throws<ProtocolException>(() => SerialOptions.Parse(Request("COM3") with
        { Options = new Dictionary<string, string> { ["serial.dataBits"] = bits, ["serial.stopBits"] = stops } }));
    }

    [Fact]
    public void ManualRtsCannotConflictWithHardwareFlowControl()
    {
        Assert.Throws<ProtocolException>(() => SerialOptions.Parse(Request("COM3") with
        { Options = new Dictionary<string, string> { ["serial.handshake"] = "RequestToSend", ["serial.rtsEnable"] = "true" } }));
    }

    internal static ConnectionRequest Request(string port = "COM3") => new() { Host = port, Username = "" };
}
