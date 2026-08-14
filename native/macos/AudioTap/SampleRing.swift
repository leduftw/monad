import Foundation
import os

/// A lock that is safe to take from a Core Audio realtime thread.
///
/// `os_unfair_lock` participates in priority donation, so a preempted consumer
/// cannot stall the audio graph the way a plain mutex could. It has to live
/// behind a stable pointer -- taking the address of a stored property can hand
/// `os_unfair_lock_lock` a temporary copy, which silently stops locking.
final class UnfairLock {
    private let pointer: UnsafeMutablePointer<os_unfair_lock>

    init() {
        self.pointer = .allocate(capacity: 1)
        self.pointer.initialize(to: os_unfair_lock())
    }

    deinit {
        self.pointer.deinitialize(count: 1)
        self.pointer.deallocate()
    }

    func lock() { os_unfair_lock_lock(self.pointer) }
    func unlock() { os_unfair_lock_unlock(self.pointer) }
}

/// Single-producer / single-consumer ring buffer of interleaved `Float` samples.
///
/// The producer is the realtime IOProc and the consumer is the stdout writer
/// thread. On overflow the oldest samples are discarded rather than blocking
/// the producer: for recognition, fresh audio matters more than completeness,
/// and stalling the IOProc would glitch playback for the whole machine.
final class SampleRing: @unchecked Sendable {
    private let lock = UnfairLock()
    private let storage: UnsafeMutablePointer<Float>
    private let capacity: Int

    private var writeIndex = 0
    private var available = 0
    private var dropped = 0

    init(capacity: Int) {
        precondition(capacity > 0, "ring capacity must be positive")
        self.capacity = capacity
        self.storage = .allocate(capacity: capacity)
        self.storage.initialize(repeating: 0, count: capacity)
    }

    deinit {
        self.storage.deinitialize(count: self.capacity)
        self.storage.deallocate()
    }

    /// Appends samples. Called on the realtime audio thread.
    func write(_ source: UnsafePointer<Float>, count: Int) {
        guard count > 0 else { return }

        self.lock.lock()
        defer { self.lock.unlock() }

        // A single burst larger than the ring: keep only its newest tail.
        if count >= self.capacity {
            self.storage.update(from: source + (count - self.capacity), count: self.capacity)
            self.dropped += self.available + (count - self.capacity)
            self.writeIndex = 0
            self.available = self.capacity
            return
        }

        let toEnd = min(count, self.capacity - self.writeIndex)
        self.storage.advanced(by: self.writeIndex).update(from: source, count: toEnd)

        if count > toEnd {
            self.storage.update(from: source + toEnd, count: count - toEnd)
        }

        self.writeIndex = (self.writeIndex + count) % self.capacity

        let filled = self.available + count
        if filled > self.capacity {
            self.dropped += filled - self.capacity
            self.available = self.capacity
        } else {
            self.available = filled
        }
    }

    /// Drains up to `maxCount` samples. Returns the samples taken and how many
    /// were dropped since the previous read.
    func read(into destination: UnsafeMutablePointer<Float>, maxCount: Int) -> (count: Int, dropped: Int) {
        self.lock.lock()
        defer { self.lock.unlock() }

        let droppedSinceLastRead = self.dropped
        self.dropped = 0

        let count = min(self.available, maxCount)
        guard count > 0 else { return (0, droppedSinceLastRead) }

        let readIndex = (self.writeIndex - self.available + self.capacity) % self.capacity
        let toEnd = min(count, self.capacity - readIndex)

        destination.update(from: self.storage + readIndex, count: toEnd)

        if count > toEnd {
            (destination + toEnd).update(from: self.storage, count: count - toEnd)
        }

        self.available -= count

        return (count, droppedSinceLastRead)
    }
}
