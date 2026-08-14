import CoreAudio
import Foundation

/// A failed Core Audio call, carrying the `OSStatus` and its four-char-code
/// spelling when it has one (Core Audio reports most errors that way).
struct CoreAudioError: LocalizedError {
    let operation: String
    let status: OSStatus

    var errorDescription: String? {
        if let code = Self.fourCharCode(self.status) {
            return "\(self.operation) failed: OSStatus \(self.status) '\(code)'"
        }

        return "\(self.operation) failed: OSStatus \(self.status)"
    }

    private static func fourCharCode(_ status: OSStatus) -> String? {
        let value = UInt32(bitPattern: status)
        let bytes: [UInt8] = [
            UInt8(truncatingIfNeeded: value >> 24),
            UInt8(truncatingIfNeeded: value >> 16),
            UInt8(truncatingIfNeeded: value >> 8),
            UInt8(truncatingIfNeeded: value),
        ]

        guard bytes.allSatisfy({ $0 >= 0x20 && $0 < 0x7F }) else { return nil }

        return String(bytes: bytes, encoding: .ascii)
    }
}

func checked(_ operation: String, _ status: OSStatus) throws {
    guard status == noErr else {
        throw CoreAudioError(operation: operation, status: status)
    }
}

/// A macOS audio output device, as reported to the `devices` subcommand.
struct OutputDevice: Codable {
    let uid: String
    let name: String
    let isDefault: Bool
}

enum CoreAudioSystem {
    private static func address(
        _ selector: AudioObjectPropertySelector,
        scope: AudioObjectPropertyScope = kAudioObjectPropertyScopeGlobal
    ) -> AudioObjectPropertyAddress {
        AudioObjectPropertyAddress(
            mSelector: selector,
            mScope: scope,
            mElement: kAudioObjectPropertyElementMain)
    }

    static func defaultOutputDeviceID() throws -> AudioObjectID {
        var addr = self.address(kAudioHardwarePropertyDefaultOutputDevice)
        var deviceID = AudioObjectID(kAudioObjectUnknown)
        var size = UInt32(MemoryLayout<AudioObjectID>.size)

        try checked(
            "read default output device",
            AudioObjectGetPropertyData(AudioObjectID(kAudioObjectSystemObject), &addr, 0, nil, &size, &deviceID))

        guard deviceID != kAudioObjectUnknown else {
            throw CoreAudioError(operation: "read default output device", status: kAudioHardwareBadDeviceError)
        }

        return deviceID
    }

    static func stringProperty(
        _ selector: AudioObjectPropertySelector,
        of deviceID: AudioObjectID,
        operation: String
    ) throws -> String {
        var addr = self.address(selector)
        var value: CFString?
        var size = UInt32(MemoryLayout<CFString?>.size)

        try withUnsafeMutablePointer(to: &value) { pointer in
            try checked(operation, AudioObjectGetPropertyData(deviceID, &addr, 0, nil, &size, pointer))
        }

        guard let result = value as String? else {
            throw CoreAudioError(operation: operation, status: kAudioHardwareUnspecifiedError)
        }

        return result
    }

    /// The rate a device is actually clocking at, which is not necessarily the
    /// rate a tap attached to it advertises.
    static func nominalSampleRate(of deviceID: AudioObjectID) throws -> Double {
        var addr = self.address(kAudioDevicePropertyNominalSampleRate)
        var rate = Double(0)
        var size = UInt32(MemoryLayout<Double>.size)

        try checked(
            "read device sample rate",
            AudioObjectGetPropertyData(deviceID, &addr, 0, nil, &size, &rate))

        return rate
    }

    static func deviceUID(_ deviceID: AudioObjectID) throws -> String {
        try self.stringProperty(kAudioDevicePropertyDeviceUID, of: deviceID, operation: "read device UID")
    }

    static func deviceName(_ deviceID: AudioObjectID) throws -> String {
        try self.stringProperty(kAudioObjectPropertyName, of: deviceID, operation: "read device name")
    }

    /// Every device that can play audio, i.e. that has at least one output channel.
    static func outputDevices() throws -> [OutputDevice] {
        var addr = self.address(kAudioHardwarePropertyDevices)
        var size = UInt32(0)

        try checked(
            "size device list",
            AudioObjectGetPropertyDataSize(AudioObjectID(kAudioObjectSystemObject), &addr, 0, nil, &size))

        let count = Int(size) / MemoryLayout<AudioObjectID>.size
        guard count > 0 else { return [] }

        var deviceIDs = [AudioObjectID](repeating: kAudioObjectUnknown, count: count)
        try checked(
            "read device list",
            AudioObjectGetPropertyData(AudioObjectID(kAudioObjectSystemObject), &addr, 0, nil, &size, &deviceIDs))

        let defaultID = try? self.defaultOutputDeviceID()

        return deviceIDs.compactMap { deviceID in
            guard self.hasOutputChannels(deviceID),
                  let uid = try? self.deviceUID(deviceID),
                  let name = try? self.deviceName(deviceID)
            else {
                return nil
            }

            return OutputDevice(uid: uid, name: name, isDefault: deviceID == defaultID)
        }
    }

    private static func hasOutputChannels(_ deviceID: AudioObjectID) -> Bool {
        var addr = self.address(kAudioDevicePropertyStreamConfiguration, scope: kAudioObjectPropertyScopeOutput)
        var size = UInt32(0)

        guard AudioObjectGetPropertyDataSize(deviceID, &addr, 0, nil, &size) == noErr, size > 0 else {
            return false
        }

        let raw = UnsafeMutableRawPointer.allocate(byteCount: Int(size), alignment: MemoryLayout<AudioBufferList>.alignment)
        defer { raw.deallocate() }

        guard AudioObjectGetPropertyData(deviceID, &addr, 0, nil, &size, raw) == noErr else {
            return false
        }

        let bufferList = UnsafeMutableAudioBufferListPointer(raw.assumingMemoryBound(to: AudioBufferList.self))

        return bufferList.contains { $0.mNumberChannels > 0 }
    }
}

func logError(_ message: String) {
    FileHandle.standardError.write(Data("monad-audiotap: \(message)\n".utf8))
}
