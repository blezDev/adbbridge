namespace AdbBridge.Core.Adb;

public sealed record AdbDevice(string Serial, string State, string? Model);
