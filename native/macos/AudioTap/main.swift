import Darwin
import Foundation

// Monad's macOS capture sidecar.
//
// Streams whatever the machine is playing to stdout as raw little-endian
// float32, preceded by a single JSON header line. Diagnostics go to stderr so
// they can never corrupt the audio stream. The host process (Monad, .NET) owns
// the lifecycle: it spawns this, reads the stream, and closes the pipe to stop.

private let usage = """
monad-audiotap -- system audio capture for Monad (macOS 14.2+)

USAGE:
  monad-audiotap capture [--duration <seconds>]
      Stream system audio to stdout. Writes one JSON header line
      ({"sampleRate":48000,"channels":2,"format":"f32le"}) followed by raw
      little-endian float32 samples. Runs until stopped.

  monad-audiotap devices
      List audio output devices as JSON.

  monad-audiotap probe [--duration <seconds>]
      Verify that system audio capture works, and report what was heard.
      Exits non-zero if a tap cannot be created.
"""

/// Writes every byte, retrying short writes and EINTR. Returns false once the
/// pipe is gone, which is how we notice the host process has exited.
private func writeAll(_ pointer: UnsafeRawPointer, _ byteCount: Int) -> Bool {
    var offset = 0

    while offset < byteCount {
        let written = write(1, pointer.advanced(by: offset), byteCount - offset)

        if written > 0 {
            offset += written
            continue
        }

        if written < 0 && errno == EINTR { continue }

        return false
    }

    return true
}

/// Exits when stdin reaches EOF, i.e. when the host process closes the pipe or
/// dies. Skipped when stdin is a terminal: reading a TTY from a background
/// process raises SIGTTIN and would suspend us.
private func watchForHostExit(onExit: @escaping () -> Void) {
    guard isatty(0) == 0 else { return }

    let thread = Thread {
        var byte: UInt8 = 0

        while true {
            let result = read(0, &byte, 1)
            if result > 0 { continue }
            if result < 0 && errno == EINTR { continue }
            break
        }

        onExit()
    }

    thread.stackSize = 64 << 10
    thread.start()
}

private func parseDuration(_ arguments: [String]) throws -> Double? {
    guard let index = arguments.firstIndex(of: "--duration") else { return nil }

    guard index + 1 < arguments.count, let seconds = Double(arguments[index + 1]), seconds > 0 else {
        throw ArgumentError(message: "--duration needs a positive number of seconds")
    }

    return seconds
}

struct ArgumentError: LocalizedError {
    let message: String
    var errorDescription: String? { self.message }
}

// MARK: - capture

private func runCapture(_ arguments: [String]) throws {
    let duration = try parseDuration(arguments)

    let tap = try SystemAudioTap()

    let stopping = Stopping()
    let signalSources = installSignalHandlers { stopping.request() }
    watchForHostExit { stopping.request() }

    tap.onSampleRateChange = { rate in
        logError("output sample rate changed to \(Int(rate)) Hz; exiting so the host re-reads the format")
        stopping.requestRestart()
    }

    // Started before the header is written: the rate the device actually runs
    // at is only known once it is running, and the header has to state it.
    try tap.start()

    let header = try JSONEncoder().encode(
        CaptureHeader(sampleRate: tap.format.sampleRate, channels: tap.format.channels, format: "f32le"))

    var headerLine = Data(header)
    headerLine.append(0x0A)

    guard headerLine.withUnsafeBytes({ writeAll($0.baseAddress!, $0.count) }) else {
        tap.stop()
        return
    }

    let deadline = duration.map { Date().addingTimeInterval($0) }
    let chunkCapacity = 1 << 15
    let chunk = UnsafeMutablePointer<Float>.allocate(capacity: chunkCapacity)
    defer { chunk.deallocate() }

    var totalDropped = 0

    while !stopping.isRequested {
        if let deadline, Date() >= deadline { break }

        let (count, dropped) = tap.ring.read(into: chunk, maxCount: chunkCapacity)
        totalDropped += dropped

        if count == 0 {
            // Nothing playing: the tap simply stops delivering buffers. Idle
            // briefly rather than spinning; the host treats the gap as silence.
            usleep(20_000)
            continue
        }

        guard writeAll(chunk, count * MemoryLayout<Float>.size) else { break }
    }

    signalSources.forEach { $0.cancel() }
    tap.stop()

    if totalDropped > 0 {
        logError("dropped \(totalDropped) samples because the host could not keep up")
    }

    if stopping.needsRestart {
        exit(75) // EX_TEMPFAIL: the host restarts us and reads the new header
    }
}

private struct CaptureHeader: Codable {
    let sampleRate: Double
    let channels: Int
    let format: String
}

/// Set from signal handlers and the stdin watcher; read by the capture loop.
private final class Stopping: @unchecked Sendable {
    private let lock = UnfairLock()
    private var requested = false
    private var restart = false

    var isRequested: Bool {
        self.lock.lock()
        defer { self.lock.unlock() }
        return self.requested
    }

    /// True when we are stopping only so the host can start us again.
    var needsRestart: Bool {
        self.lock.lock()
        defer { self.lock.unlock() }
        return self.restart
    }

    func request() {
        self.lock.lock()
        self.requested = true
        self.lock.unlock()
    }

    func requestRestart() {
        self.lock.lock()
        self.requested = true
        self.restart = true
        self.lock.unlock()
    }
}

/// `DispatchSourceSignal` runs the handler on a normal queue, so unlike a raw
/// `signal()` handler it is allowed to touch locks.
private func installSignalHandlers(_ handler: @escaping () -> Void) -> [DispatchSourceSignal] {
    [SIGINT, SIGTERM, SIGHUP].map { signalNumber in
        signal(signalNumber, SIG_IGN)
        let source = DispatchSource.makeSignalSource(signal: signalNumber, queue: .global())
        source.setEventHandler(handler: handler)
        source.resume()
        return source
    }
}

// MARK: - devices

private func runDevices() throws {
    let encoder = JSONEncoder()
    encoder.outputFormatting = [.prettyPrinted, .sortedKeys]

    let data = try encoder.encode(try CoreAudioSystem.outputDevices())
    FileHandle.standardOutput.write(data)
    FileHandle.standardOutput.write(Data("\n".utf8))
}

// MARK: - probe

private func runProbe(_ arguments: [String]) throws {
    let duration = try parseDuration(arguments) ?? 1.5

    let tap = try SystemAudioTap()
    try tap.start()

    let chunkCapacity = 1 << 15
    let chunk = UnsafeMutablePointer<Float>.allocate(capacity: chunkCapacity)
    defer { chunk.deallocate() }

    var samples = 0
    var peak: Float = 0
    let deadline = Date().addingTimeInterval(duration)

    while Date() < deadline {
        let (count, _) = tap.ring.read(into: chunk, maxCount: chunkCapacity)

        if count == 0 {
            usleep(20_000)
            continue
        }

        samples += count
        for index in 0..<count { peak = max(peak, abs(chunk[index])) }
    }

    tap.stop()

    let seconds = Double(samples) / (tap.format.sampleRate * Double(max(tap.format.channels, 1)))
    let result = ProbeResult(
        ok: true,
        sampleRate: tap.format.sampleRate,
        channels: tap.format.channels,
        capturedSeconds: seconds,
        peak: peak)

    let encoder = JSONEncoder()
    encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
    FileHandle.standardOutput.write(try encoder.encode(result))
    FileHandle.standardOutput.write(Data("\n".utf8))

    if samples == 0 {
        logError("the tap was created but delivered no audio -- either nothing was playing, "
            + "or this terminal has not been granted audio recording permission "
            + "(System Settings > Privacy & Security > Screen & System Audio Recording)")
    }
}

private struct ProbeResult: Codable {
    let ok: Bool
    let sampleRate: Double
    let channels: Int
    let capturedSeconds: Double
    let peak: Float
}

// MARK: - entry point

signal(SIGPIPE, SIG_IGN)

let arguments = Array(CommandLine.arguments.dropFirst())
let command = arguments.first ?? "capture"
let rest = Array(arguments.dropFirst())

do {
    switch command {
    case "capture":
        try runCapture(rest)
    case "devices":
        try runDevices()
    case "probe":
        try runProbe(rest)
    case "help", "--help", "-h":
        print(usage)
    default:
        logError("unknown command '\(command)'")
        print(usage)
        exit(64) // EX_USAGE
    }
} catch {
    logError(error.localizedDescription)
    exit(1)
}
