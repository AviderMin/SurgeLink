namespace SurgeLink.App.Localization.Zh;

public static class ZhProtocols
{
    public static IReadOnlyDictionary<string, string> Map { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["protocol.serial.endpoint"] = "串口名称",
        ["protocol.serial.description"] = "本地串口终端",
        ["protocol.option.serial.baudRate"] = "波特率",
        ["protocol.option.serial.dataBits"] = "数据位",
        ["protocol.option.serial.parity"] = "校验位",
        ["protocol.option.serial.stopBits"] = "停止位",
        ["protocol.option.serial.handshake"] = "流控",
        ["protocol.option.serial.dtrEnable"] = "启用 DTR",
        ["protocol.option.serial.rtsEnable"] = "启用 RTS（不使用硬件流控时）",
        ["protocol.choice.serial.parity.None"] = "无",
        ["protocol.choice.serial.parity.Odd"] = "奇校验",
        ["protocol.choice.serial.parity.Even"] = "偶校验",
        ["protocol.choice.serial.parity.Mark"] = "标记校验",
        ["protocol.choice.serial.parity.Space"] = "空格校验",
        ["protocol.choice.serial.stopBits.One"] = "1",
        ["protocol.choice.serial.stopBits.OnePointFive"] = "1.5",
        ["protocol.choice.serial.stopBits.Two"] = "2",
        ["protocol.choice.serial.handshake.None"] = "无",
        ["protocol.choice.serial.handshake.XOnXOff"] = "XON/XOFF（软件流控）",
        ["protocol.choice.serial.handshake.RequestToSend"] = "RTS/CTS（硬件流控）",
        ["protocol.choice.serial.handshake.RequestToSendXOnXOff"] = "RTS/CTS + XON/XOFF",
        ["shell.editor.devicePlaceholder"] = "例如：COM3",
        ["shell.editor.deviceRequired"] = "请填写设备名称。",
        ["shell.editor.devices"] = "检测到的设备",
        ["shell.editor.devicesHint"] = "选择已检测到的串口，或在上方手动输入名称；插入适配器后可刷新列表。",
        ["shell.editor.refreshDevices"] = "刷新",
        ["shell.editor.connectionOptions"] = "连接参数",
        ["shell.editor.connectionOptionsHint"] = "请与目标设备的串口参数保持一致。",
        ["shell.editor.optionInvalid"] = "请检查“{0}”的值。",
        ["shell.editor.deviceNamePlaceholder"] = "默认为设备名称",
    };
}
