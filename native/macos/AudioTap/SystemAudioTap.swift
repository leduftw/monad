import AudioToolbox
import CoreAudio
import Foundation

/// Captures everything the machine plays, using a Core Audio process tap
/// (macOS 14.2+). Unlike the Windows WASAPI loopback path this needs no virtual
/// audio device: the tap is attached to a private aggregate device that borrows
/// the current default output device for its IO clock.
///
/// The tap itself is created once and outlives device changes, so the stream
/// format stays constant for the lifetime of the process. Only the aggregate
/// device is rebuilt when the user switches outputs (speakers -> AirPods, say),
/// which otherwise leaves the IOProc attached to a device that no longer exists.
final class SystemAudioTap: @unchecked Sendable {
    struct Format: Codable {
        let sampleRate: Double
        let channels: Int
    }

    let ring: SampleRing

    /// Called when the sample rate changes under us, which the stream format
    /// cannot express mid-flight. The host restarts us and reads a new header.
    var onSampleRateChange: ((Double) -> Void)?

    private let tapID: AudioObjectID
    private let tapUID: String
    private let channels: Int

    /// The rate the aggregate device actually clocks at.
    ///
    /// This is deliberately not taken from `kAudioTapPropertyFormat`, which
    /// advertises the tap's own nominal rate: on a machine whose output device
    /// runs at 44.1 kHz the tap still claims 48 kHz, and believing it stretches
    /// every clip by 8.8% -- inaudible as a glitch, but quite enough to stop
    /// any of it being recognised.
    private var runningSampleRate: Double

    private let stateLock = UnfairLock()
    private var aggregateID = AudioObjectID(kAudioObjectUnknown)
    private var ioProcID: AudioDeviceIOProcID?
    private var isStopped = false
    private var hasStarted = false

    /// Scratch space used to interleave when the tap hands us one buffer per
    /// channel. Allocated up front because the IOProc must not allocate.
    private let scratch: UnsafeMutablePointer<Float>
    private let scratchCapacity: Int

    private let listenerQueue = DispatchQueue(label: "io.monad.audiotap.device-changes")
    private var deviceListener: AudioObjectPropertyListenerBlock?

    init(ringCapacitySeconds: Double = 4.0) throws {
        let description = CATapDescription(stereoGlobalTapButExcludeProcesses: [])
        description.name = "Monad System Audio Tap"
        description.isPrivate = true
        description.muteBehavior = .unmuted

        var tapID = AudioObjectID(kAudioObjectUnknown)
        try checked("create process tap", AudioHardwareCreateProcessTap(description, &tapID))

        self.tapID = tapID
        self.tapUID = description.uuid.uuidString

        var addr = AudioObjectPropertyAddress(
            mSelector: kAudioTapPropertyFormat,
            mScope: kAudioObjectPropertyScopeGlobal,
            mElement: kAudioObjectPropertyElementMain)
        var asbd = AudioStreamBasicDescription()
        var size = UInt32(MemoryLayout<AudioStreamBasicDescription>.size)

        do {
            try checked("read tap format", AudioObjectGetPropertyData(tapID, &addr, 0, nil, &size, &asbd))
        } catch {
            AudioHardwareDestroyProcessTap(tapID)
            throw error
        }

        guard asbd.mFormatFlags & kAudioFormatFlagIsFloat != 0, asbd.mBitsPerChannel == 32 else {
            AudioHardwareDestroyProcessTap(tapID)
            throw TapUnsupportedFormatError(
                bitsPerChannel: asbd.mBitsPerChannel,
                formatFlags: asbd.mFormatFlags)
        }

        self.channels = Int(asbd.mChannelsPerFrame)
        self.runningSampleRate = asbd.mSampleRate // provisional until the device is up

        let ringCapacity = max(
            1024,
            Int(max(asbd.mSampleRate, 48_000) * ringCapacitySeconds) * max(self.channels, 1))
        self.ring = SampleRing(capacity: ringCapacity)

        self.scratchCapacity = 1 << 16
        self.scratch = .allocate(capacity: self.scratchCapacity)
        self.scratch.initialize(repeating: 0, count: self.scratchCapacity)
    }

    deinit {
        self.scratch.deinitialize(count: self.scratchCapacity)
        self.scratch.deallocate()
    }

    /// The format callers should describe the stream as. Only meaningful once
    /// `start()` has returned, since the true rate comes from the running device.
    var format: Format {
        self.stateLock.lock()
        defer { self.stateLock.unlock() }
        return Format(sampleRate: self.runningSampleRate, channels: self.channels)
    }

    func start() throws {
        try self.buildAggregateDevice()
        self.installDefaultDeviceListener()
    }

    /// Tears down the IOProc, aggregate device and tap. Safe to call twice --
    /// signal handlers and normal shutdown both route here.
    func stop() {
        self.stateLock.lock()
        if self.isStopped {
            self.stateLock.unlock()
            return
        }
        self.isStopped = true
        self.stateLock.unlock()

        self.removeDefaultDeviceListener()
        self.tearDownAggregateDevice()

        AudioHardwareDestroyProcessTap(self.tapID)
    }

    // MARK: - Aggregate device lifecycle

    private func buildAggregateDevice() throws {
        let outputDeviceID = try CoreAudioSystem.defaultOutputDeviceID()
        let outputUID = try CoreAudioSystem.deviceUID(outputDeviceID)

        let description: [String: Any] = [
            kAudioAggregateDeviceNameKey: "Monad Capture",
            kAudioAggregateDeviceUIDKey: UUID().uuidString,
            kAudioAggregateDeviceMainSubDeviceKey: outputUID,
            kAudioAggregateDeviceIsPrivateKey: true,
            kAudioAggregateDeviceIsStackedKey: false,
            kAudioAggregateDeviceTapAutoStartKey: true,
            kAudioAggregateDeviceSubDeviceListKey: [[kAudioSubDeviceUIDKey: outputUID]],
            kAudioAggregateDeviceTapListKey: [[
                kAudioSubTapDriftCompensationKey: true,
                kAudioSubTapUIDKey: self.tapUID,
            ]],
        ]

        var aggregateID = AudioObjectID(kAudioObjectUnknown)
        try checked(
            "create aggregate device",
            AudioHardwareCreateAggregateDevice(description as CFDictionary, &aggregateID))

        var ioProcID: AudioDeviceIOProcID?
        do {
            try checked(
                "create IOProc",
                AudioDeviceCreateIOProcIDWithBlock(&ioProcID, aggregateID, nil) { [self] _, inputData, _, _, _ in
                    self.consume(inputData)
                })

            try checked("start aggregate device", AudioDeviceStart(aggregateID, ioProcID))
        } catch {
            if let ioProcID { AudioDeviceDestroyIOProcID(aggregateID, ioProcID) }
            AudioHardwareDestroyAggregateDevice(aggregateID)
            throw error
        }

        // The device is the authority on the rate its IOProc delivers.
        let rate = (try? CoreAudioSystem.nominalSampleRate(of: aggregateID))
            ?? (try? CoreAudioSystem.nominalSampleRate(of: outputDeviceID))
            ?? self.runningSampleRate

        self.stateLock.lock()
        self.aggregateID = aggregateID
        self.ioProcID = ioProcID
        let previous = self.runningSampleRate
        let started = self.hasStarted
        self.runningSampleRate = rate
        self.hasStarted = true
        self.stateLock.unlock()

        if started && rate != previous {
            self.onSampleRateChange?(rate)
        }
    }

    private func tearDownAggregateDevice() {
        self.stateLock.lock()
        let aggregateID = self.aggregateID
        let ioProcID = self.ioProcID
        self.aggregateID = AudioObjectID(kAudioObjectUnknown)
        self.ioProcID = nil
        self.stateLock.unlock()

        guard aggregateID != kAudioObjectUnknown else { return }

        if let ioProcID {
            AudioDeviceStop(aggregateID, ioProcID)
            AudioDeviceDestroyIOProcID(aggregateID, ioProcID)
        }

        AudioHardwareDestroyAggregateDevice(aggregateID)
    }

    // MARK: - Default output device changes

    private func installDefaultDeviceListener() {
        var addr = AudioObjectPropertyAddress(
            mSelector: kAudioHardwarePropertyDefaultOutputDevice,
            mScope: kAudioObjectPropertyScopeGlobal,
            mElement: kAudioObjectPropertyElementMain)

        let listener: AudioObjectPropertyListenerBlock = { [weak self] _, _ in
            self?.rebuildAfterDeviceChange()
        }

        let status = AudioObjectAddPropertyListenerBlock(
            AudioObjectID(kAudioObjectSystemObject), &addr, self.listenerQueue, listener)

        if status == noErr {
            self.deviceListener = listener
        } else {
            // Not fatal: capture still works, it just will not survive the user
            // switching output devices mid-session.
            logError("could not watch for output device changes (OSStatus \(status)); "
                + "capture may stop if you switch outputs")
        }
    }

    private func removeDefaultDeviceListener() {
        guard let listener = self.deviceListener else { return }
        self.deviceListener = nil

        var addr = AudioObjectPropertyAddress(
            mSelector: kAudioHardwarePropertyDefaultOutputDevice,
            mScope: kAudioObjectPropertyScopeGlobal,
            mElement: kAudioObjectPropertyElementMain)

        AudioObjectRemovePropertyListenerBlock(
            AudioObjectID(kAudioObjectSystemObject), &addr, self.listenerQueue, listener)
    }

    private func rebuildAfterDeviceChange() {
        self.stateLock.lock()
        let stopped = self.isStopped
        self.stateLock.unlock()

        guard !stopped else { return }

        self.tearDownAggregateDevice()

        do {
            try self.buildAggregateDevice()
        } catch {
            logError("output device changed but capture could not be restarted: \(error.localizedDescription)")
        }
    }

    // MARK: - Realtime callback

    /// Runs on the Core Audio realtime thread: no allocation, no I/O, no locks
    /// other than the ring's priority-donating one.
    private func consume(_ bufferList: UnsafePointer<AudioBufferList>) {
        let buffers = UnsafeMutableAudioBufferListPointer(UnsafeMutablePointer(mutating: bufferList))
        guard let first = buffers.first, let firstData = first.mData else { return }

        // Interleaved: the tap hands us a single buffer holding every channel.
        if buffers.count == 1 {
            let sampleCount = Int(first.mDataByteSize) / MemoryLayout<Float>.size
            guard sampleCount > 0 else { return }
            self.ring.write(firstData.bindMemory(to: Float.self, capacity: sampleCount), count: sampleCount)
            return
        }

        // Non-interleaved: one mono buffer per channel, so weave them together.
        let channels = buffers.count
        let framesPerChannel = Int(first.mDataByteSize) / MemoryLayout<Float>.size
        let total = framesPerChannel * channels
        guard framesPerChannel > 0, total <= self.scratchCapacity else { return }

        for (channel, buffer) in buffers.enumerated() {
            guard let data = buffer.mData else { return }
            let source = data.bindMemory(to: Float.self, capacity: framesPerChannel)

            for frame in 0..<framesPerChannel {
                self.scratch[frame * channels + channel] = source[frame]
            }
        }

        self.ring.write(self.scratch, count: total)
    }
}

struct TapUnsupportedFormatError: LocalizedError {
    let bitsPerChannel: UInt32
    let formatFlags: AudioFormatFlags

    var errorDescription: String? {
        "the system audio tap offered an unsupported format "
            + "(\(self.bitsPerChannel)-bit, flags \(self.formatFlags)); expected 32-bit float"
    }
}
